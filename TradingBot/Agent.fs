namespace TradingBot

open System
open System.ClientModel
open System.Text
open System.Threading.Tasks
open Microsoft.Extensions.AI

// --- Stage 1 (discovery) wire DTOs ---
type CandidateDto = {
    Ticker : string
    Reason : string
}

type DiscoveryDto = {
    MarketTrend : string
    Candidates  : CandidateDto array
}

// --- Stage 2 (per-asset decision) wire DTO ---
// Strings on enum-like fields; validated/translated to domain types after parse.
type AgentDecisionDto = {
    Action           : string
    SizeUsd          : decimal
    Confidence       : float
    ManipulationRisk : string
    Rationale        : string
}

type Agent = {
    /// Stage 1: general news -> (market trend, candidate tickers) + raw response.
    IdentifyCandidates :
        NewsItem list -> Task<Result<(string * Candidate list) * string, string>>
    /// Stage 2: one asset, given the market trend, portfolio, its price snapshot,
    /// and its own news -> a Decision (incl. manipulation risk) + raw response.
    EvaluateAsset :
        string -> Portfolio -> PriceSnapshot -> NewsItem list
            -> Task<Result<Decision * string, string>>
}

module Agent =

    let private maxAttempts = 3

    /// Walk the inner-exception chain. For any ClientResultException, also include
    /// the HTTP response body — Google's 429 etc. carry the quotaId in there, which
    /// the generic message strips out.
    let private describeException (ex : exn) : string =
        let sb = StringBuilder()
        let rec walk (e : exn) =
            sb.Append(e.GetType().Name).Append(": ").Append(e.Message) |> ignore
            match e with
            | :? ClientResultException as cre ->
                try
                    let body = cre.GetRawResponse().Content.ToString()
                    if not (String.IsNullOrEmpty body) then
                        sb.Append(" | body: ").Append(body) |> ignore
                with _ -> ()
            | _ -> ()
            if not (isNull e.InnerException) then
                sb.Append(" >> ") |> ignore
                walk e.InnerException
        walk ex
        sb.ToString()

    /// Shared structured-output call with retry/backoff. Gemini occasionally
    /// returns truncated JSON (transient — a retry succeeds) but a 429 means we're
    /// rate-limited, so we don't retry that (it just burns more quota).
    let private callStructured<'T> (chatClient : IChatClient) (prompt : string) : Task<Result<'T * string, string>> =
        let options = ChatOptions()
        options.Temperature     <- Nullable 0.2f
        options.MaxOutputTokens <- Nullable 4000

        let attempt () =
            task {
                let mutable rawText = ""
                try
                    let! response =
                        ChatClientStructuredOutputExtensions.GetResponseAsync<'T>(
                            chatClient, prompt, options)
                    rawText <- response.Text |> Option.ofObj |> Option.defaultValue ""
                    return Ok (response.Result, rawText)
                with ex ->
                    let preview =
                        if rawText.Length > 300 then rawText.Substring(0, 300) + "…" else rawText
                    return Error (sprintf "%s | raw (%d bytes): %s"
                                          (describeException ex) rawText.Length preview)
            }

        let rec loop n =
            task {
                let! r = attempt ()
                match r with
                | Ok _ -> return r
                | Error e when e.Contains "Status: 429" ->
                    return Error (sprintf "rate limited (no retry): %s" e)
                | Error e when n >= maxAttempts ->
                    return Error (sprintf "failed after %d attempts: %s" n e)
                | Error _ ->
                    do! Task.Delay (1000 * pown 2 (n - 1))
                    return! loop (n + 1)
            }
        loop 1

    // ---------------- Stage 1: discovery ----------------

    let private buildDiscoveryPrompt (news : NewsItem list) : string =
        let sb = StringBuilder()
        let w (s : string) = sb.AppendLine(s) |> ignore
        w "You are a skeptical financial analyst scanning today's news for trade ideas."
        w "From the headlines below, identify:"
        w "  (a) the broad market/sector trend they collectively suggest, and"
        w "  (b) specific US-listed stock tickers that are most directly affected and worth a closer look."
        w ""
        w "Be rigorous and skeptical. MANY headlines are promotional press releases, paid"
        w "'microcap'/'penny stock' hype, or pump-and-dump material designed to look like news."
        w "Do NOT surface a ticker just because a press release is bullish about it. Favour"
        w "established, liquid companies with genuine, market-moving news. If a story smells"
        w "promotional, leave it out."
        w ""
        w "Return ONLY ticker symbols (not company names) for liquid US equities."
        w ""
        w "## Headlines"
        if List.isEmpty news then
            w "  (no fresh news this cycle)"
        else
            for n in news |> List.truncate 30 do
                w (sprintf "  [%s] %s" n.Source n.Title)
                if n.Summary <> "" then w (sprintf "      %s" n.Summary)
        w ""
        w "Output JSON: a one-paragraph marketTrend, and a candidates array of {ticker, reason}."
        sb.ToString()

    let private toCandidates (dto : DiscoveryDto) : Candidate list =
        dto.Candidates
        |> Array.choose (fun c ->
            let t = if isNull c.Ticker then "" else c.Ticker.Trim().ToUpperInvariant()
            if t = "" then None
            else Some ({ Ticker = Asset t; Reason = (if isNull c.Reason then "" else c.Reason) } : Candidate))
        |> Array.toList

    // ---------------- Stage 2: per-asset decision ----------------

    let private buildDecisionPrompt
        (cfg : AppSettings) (marketTrend : string) (portfolio : Portfolio)
        (price : PriceSnapshot) (news : NewsItem list) : string =
        let sb = StringBuilder()
        let w (s : string) = sb.AppendLine(s) |> ignore
        let symbol = Asset.value price.Asset

        w (sprintf "You are a cautious trader deciding what to do about ONE stock: %s." symbol)
        w "Bias toward Hold. Only Buy/Sell when the news and price give a clear, well-founded reason."
        w ""
        w "Assess manipulation risk explicitly. Treat hype, vague 'huge potential' language,"
        w "thin-float/penny-stock promotion, or coordinated-pump signals as strong reasons to"
        w "avoid buying (and to sell if held). Report it as manipulationRisk = Low | Medium | High."
        w ""
        w "## Overall market trend"
        w (if String.IsNullOrWhiteSpace marketTrend then "  (none provided)" else "  " + marketTrend)
        w ""
        w "## Portfolio"
        w (sprintf "Cash: $%.2f" (Usd.value portfolio.CashUsd))
        match Map.tryFind price.Asset portfolio.Positions with
        | Some p ->
            w (sprintf "Current position in %s: qty=%.6f avgCost=$%.4f"
                   symbol (Qty.value p.Qty) (Usd.value p.AvgCostUsd))
        | None ->
            w (sprintf "No current position in %s." symbol)
        w ""
        w "## Price"
        w (sprintf "  %s: $%.4f (1d: %+.2f%%, 5d: %+.2f%%)"
               symbol (Usd.value price.PriceUsd) price.Change24hPct price.Change7dPct)
        w ""
        w (sprintf "## Recent %s news" symbol)
        if List.isEmpty news then
            w "  (no fresh ticker-specific news)"
        else
            for n in news |> List.truncate 12 do
                w (sprintf "  [%s] %s" n.Source n.Title)
                if n.Summary <> "" then w (sprintf "      %s" n.Summary)
        w ""
        w "## Constraints"
        w (sprintf "- Minimum trade size: $%.2f" cfg.Risk.MinTradeUsd)
        w (sprintf "- Maximum trade size: $%.2f" cfg.Risk.MaxTradeUsd)
        w (sprintf "- Maximum position size: %.0f%% of portfolio value" (cfg.Risk.MaxPositionPct * 100.0))
        w (sprintf "- Keep at least %.0f%% in cash" (cfg.Risk.CashReservePct * 100.0))
        w (sprintf "- %.0fh cooldown between trades on the same asset" cfg.Risk.PerAssetCooldownHours)
        w ""
        w "Output JSON: { action, sizeUsd, confidence, manipulationRisk, rationale }."
        sb.ToString()

    let private toDecision (asset : Asset) (dto : AgentDecisionDto) : Result<Decision, string> =
        match TradeAction.tryParse dto.Action with
        | None -> Error (sprintf "Unknown action '%s' for %s" dto.Action (Asset.value asset))
        | Some action ->
            // Unparseable manipulation risk defaults to High (most cautious).
            let mr = ManipulationRisk.tryParse dto.ManipulationRisk |> Option.defaultValue High
            Ok {
                Asset            = asset
                Action           = action
                SizeUsd          = Usd dto.SizeUsd
                Confidence       = dto.Confidence
                ManipulationRisk = mr
                Rationale        = (if isNull dto.Rationale then "" else dto.Rationale)
            }

    let create (chatClient : IChatClient) (cfg : AppSettings) : Agent =
        {
            IdentifyCandidates = fun news ->
                task {
                    let prompt = buildDiscoveryPrompt news
                    let! r = callStructured<DiscoveryDto> chatClient prompt
                    match r with
                    | Error e -> return Error (sprintf "IdentifyCandidates %s" e)
                    | Ok (dto, raw) ->
                        let trend = if isNull dto.MarketTrend then "" else dto.MarketTrend
                        return Ok ((trend, toCandidates dto), raw)
                }

            EvaluateAsset = fun marketTrend portfolio price news ->
                task {
                    let prompt = buildDecisionPrompt cfg marketTrend portfolio price news
                    let! r = callStructured<AgentDecisionDto> chatClient prompt
                    match r with
                    | Error e -> return Error (sprintf "EvaluateAsset(%s) %s" (Asset.value price.Asset) e)
                    | Ok (dto, raw) ->
                        match toDecision price.Asset dto with
                        | Error e -> return Error e
                        | Ok d    -> return Ok (d, raw)
                }
        }
