namespace TradingBot

open System
open System.Text
open System.Threading.Tasks
open Microsoft.Extensions.AI

/// Wire DTO for structured LLM output. Strings on enum-like fields so the
/// schema generator emits proper JSON Schema enums. Translated to F# domain
/// types after deserialization.
type AgentDecisionDto = {
    Asset      : string
    Action     : string
    SizeUsd    : decimal
    Confidence : float
    Rationale  : string
}

type AgentResponseDto = {
    MarketView : string
    Decisions  : AgentDecisionDto array
}

type Agent = {
    /// Returns (decisions, raw response text) on success.
    Propose :
        Portfolio
            -> PriceSnapshot list
            -> NewsItem list
            -> Trade list
            -> Task<Result<Decision list * string, string>>
}

module Agent =

    let private buildPrompt
        (cfg : AppSettings)
        (portfolio : Portfolio)
        (prices : PriceSnapshot list)
        (news : NewsItem list)
        (recentTrades : Trade list)
        : string =
        let sb = StringBuilder()
        let w (s : string) = sb.AppendLine(s) |> ignore

        w "You are a cautious crypto trader managing a small USD-denominated portfolio."
        w (sprintf "You may only trade these assets: %s." (String.concat ", " cfg.Assets))
        w "Output exactly one decision per asset (Buy, Sell, or Hold)."
        w "Bias toward Hold. Only propose a trade when news, momentum, or risk balance gives you a clear reason."
        w ""
        w "## Portfolio"
        w (sprintf "Cash: $%.2f" (Usd.value portfolio.CashUsd))
        if Map.isEmpty portfolio.Positions then
            w "Positions: (none)"
        else
            w "Positions:"
            for KeyValue(asset, p) in portfolio.Positions do
                w (sprintf "  %s: qty=%.8f avgCost=$%.4f"
                       (Asset.value asset) (Qty.value p.Qty) (Usd.value p.AvgCostUsd))
        w ""
        w "## Prices"
        for p in prices do
            w (sprintf "  %s: $%.4f (24h: %+.2f%%, 7d: %+.2f%%)"
                   (Asset.value p.Asset) (Usd.value p.PriceUsd) p.Change24hPct p.Change7dPct)
        w ""
        w "## News (recent headlines, with sentiment votes positive/negative/important)"
        if List.isEmpty news then
            w "  (no fresh news this cycle)"
        else
            for n in news |> List.truncate 20 do
                w (sprintf "  [%s | +%d/-%d/!%d] %s"
                       n.Source n.VotesPositive n.VotesNegative n.VotesImportant n.Title)
        w ""
        w "## Recent trades (last 24h)"
        if List.isEmpty recentTrades then
            w "  (none)"
        else
            for t in recentTrades |> List.truncate 10 do
                w (sprintf "  %s %s qty=%.8f @ $%.4f (fee $%.4f)"
                       (TradeAction.toString t.Side) (Asset.value t.Asset)
                       (Qty.value t.Qty) (Usd.value t.PriceUsd) (Usd.value t.FeeUsd))
        w ""
        w "## Constraints"
        w (sprintf "- Minimum trade size: $%.2f" cfg.Risk.MinTradeUsd)
        w (sprintf "- Maximum trade size: $%.2f" cfg.Risk.MaxTradeUsd)
        w (sprintf "- Maximum position size: %.0f%% of portfolio value"
                   (cfg.Risk.MaxPositionPct * 100.0))
        w (sprintf "- Keep at least %.0f%% in cash" (cfg.Risk.CashReservePct * 100.0))
        let roundTripBps = (cfg.Risk.FeeBps + cfg.Risk.SpreadBps + cfg.Risk.SlippageBps) * 2
        w (sprintf "- Round-trip cost ~%.2f%%; don't propose trades without enough expected edge"
                   (float roundTripBps / 100.0))
        w (sprintf "- %.0fh cooldown between trades on the same asset" cfg.Risk.PerAssetCooldownHours)
        w ""
        w "Now output your decisions as JSON conforming to the response schema."
        sb.ToString()

    let private toDecision (dto : AgentDecisionDto) : Result<Decision, string> =
        match TradeAction.tryParse dto.Action with
        | None ->
            Error (sprintf "Unknown action '%s' for asset %s" dto.Action dto.Asset)
        | Some action ->
            Ok {
                Asset      = Asset (dto.Asset.ToUpperInvariant())
                Action     = action
                SizeUsd    = Usd dto.SizeUsd
                Confidence = dto.Confidence
                Rationale  = dto.Rationale
            }

    let create (chatClient : IChatClient) (cfg : AppSettings) : Agent =
        {
            Propose = fun portfolio prices news recentTrades ->
                task {
                    let prompt = buildPrompt cfg portfolio prices news recentTrades
                    let options = ChatOptions()
                    options.Temperature     <- Nullable 0.2f
                    options.MaxOutputTokens <- Nullable 4000
                    let mutable rawText = ""
                    try
                        let! response =
                            ChatClientStructuredOutputExtensions.GetResponseAsync<AgentResponseDto>(
                                chatClient, prompt, options)
                        rawText <-
                            response.Text
                            |> Option.ofObj
                            |> Option.defaultValue ""
                        let dto = response.Result
                        let decisions =
                            dto.Decisions
                            |> Array.choose (fun d ->
                                match toDecision d with
                                | Ok dec  -> Some dec
                                | Error _ -> None)
                            |> Array.toList
                        return Ok (decisions, rawText)
                    with ex ->
                        return Error (sprintf "Agent.Propose failed: %s\nRaw response (%d bytes):\n%s"
                                              ex.Message
                                              rawText.Length
                                              rawText)
                }
        }
