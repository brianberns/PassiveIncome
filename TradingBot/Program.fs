module TradingBot.Program

open System
open System.ClientModel
open System.Net.Http
open System.Threading
open Alpaca.Markets
open Microsoft.Extensions.AI
open Microsoft.Extensions.Logging
open OpenAI

let private newHttpClient () =
    let c = new HttpClient(Timeout = TimeSpan.FromSeconds 30.0)
    c.DefaultRequestHeaders.UserAgent.ParseAdd("TradingBot/0.1 (+passive-income-bot)")
    c

let private makeChatClient (cfg : AppSettings) : IChatClient =
    let creds  = ApiKeyCredential(cfg.Llm.ApiKey)
    let opts   = OpenAIClientOptions(Endpoint = Uri cfg.Llm.Endpoint)
    let openAi = OpenAIClient(creds, opts)
    openAi.GetChatClient(cfg.Llm.Model).AsIChatClient()

/// Build Alpaca trading + data clients for the configured environment.
let private makeAlpacaClients (cfg : AppSettings) : IAlpacaTradingClient * IAlpacaDataClient =
    let env = if cfg.Alpaca.Paper then Environments.Paper else Environments.Live
    let key = SecretKey(cfg.Alpaca.KeyId, cfg.Alpaca.Secret)
    env.GetAlpacaTradingClient(key), env.GetAlpacaDataClient(key)

let private makeBroker (cfg : AppSettings) (db : Persistence) (trading : IAlpacaTradingClient) (prices : Prices) : Broker =
    match cfg.Broker with
    | "Paper" -> PaperBroker.create db prices cfg.Risk
    | _       -> AlpacaBroker.create db trading

let private makeLogger () : ILogger =
    let factory =
        LoggerFactory.Create(fun b ->
            b.AddSimpleConsole(fun o ->
                o.SingleLine     <- true
                o.TimestampFormat <- "HH:mm:ss "
            ) |> ignore)
    factory.CreateLogger("TradingBot")

let private printConfig (cfg : AppSettings) =
    printfn "TradingBot v0.3 (Alpaca, news-discovery)"
    printfn "  Starting cash:   $%g"      cfg.StartingCashUsd
    printfn "  Cycle interval:  %g hours" cfg.CycleIntervalHours
    let seed = if List.isEmpty cfg.Assets then "(pure discovery)" else String.concat ", " cfg.Assets
    printfn "  Seed assets:     %s"       seed
    printfn "  Discovery:       max %d candidates/cycle, floor px ≥ $%g, ADDV ≥ $%g"
        cfg.Discovery.MaxCandidatesPerCycle cfg.Discovery.MinSharePrice cfg.Discovery.MinAvgDollarVolume
    printfn "  Broker:          %s (%s)"  cfg.Broker (if cfg.Alpaca.Paper then "paper" else "LIVE")
    printfn "  Database:        %s"       cfg.DatabasePath
    printfn "  LLM model:       %s"       cfg.Llm.Model
    let secretStatus key value =
        if String.IsNullOrEmpty value then sprintf "%s: (missing)" key else sprintf "%s: ****" key
    printfn "  Secrets — %s, %s"
        (secretStatus "Gemini key" cfg.Llm.ApiKey)
        (secretStatus "Alpaca key" cfg.Alpaca.KeyId)
    printfn ""

let private runReport (cfg : AppSettings) =
    task {
        let db = Persistence.create cfg.DatabasePath
        db.Init cfg.StartingCashUsd
        let trading, data = makeAlpacaClients cfg
        let prices = Prices.create data
        let broker = makeBroker cfg db trading prices
        let! portfolio = broker.GetPortfolio ()

        let heldAssets = portfolio.Positions |> Map.toList |> List.map fst
        let! priceMap =
            task {
                try
                    let! snaps = prices.Fetch heldAssets
                    return snaps |> List.map (fun s -> s.Asset, s.PriceUsd) |> Map.ofList
                with _ -> return Map.empty
            }

        let cash = Usd.value portfolio.CashUsd
        printfn "Portfolio (%s):" cfg.Broker
        printfn "  Cash:      $%.4f" cash
        printfn "  Positions: %d"    portfolio.Positions.Count

        let mutable positionsValue = 0m
        for KeyValue(asset, p) in portfolio.Positions do
            let qty       = Qty.value p.Qty
            let avgCost   = Usd.value p.AvgCostUsd
            let costBasis = qty * avgCost
            match Map.tryFind asset priceMap with
            | Some price ->
                let mkt = qty * Usd.value price
                positionsValue <- positionsValue + mkt
                let pnl    = mkt - costBasis
                let pnlPct = if costBasis = 0m then 0m else pnl / costBasis * 100m
                printfn "    %-5s qty=%.6f  avg=$%.2f  now=$%.2f  value=$%.2f  P&L=$%+.2f (%+.2f%%)"
                    (Asset.value asset) qty avgCost (Usd.value price) mkt pnl pnlPct
            | None ->
                positionsValue <- positionsValue + costBasis
                printfn "    %-5s qty=%.6f  avg=$%.2f  (no live price — valued at cost $%.2f)"
                    (Asset.value asset) qty avgCost costBasis

        let totalValue  = cash + positionsValue
        let totalPnl    = totalValue - cfg.StartingCashUsd
        let totalPnlPct = totalPnl / cfg.StartingCashUsd * 100m
        printfn "  ----"
        printfn "  Total value: $%.2f  (cash $%.2f + positions $%.2f)" totalValue cash positionsValue
        printfn "  Since start: $%+.2f (%+.2f%%) on $%.2f" totalPnl totalPnlPct cfg.StartingCashUsd

        let recent = db.RecentTrades (24.0 * 7.0)
        printfn "  Trades (last 7d): %d" (List.length recent)
        for t in recent |> List.truncate 10 do
            printfn "    %s %-5s qty=%.6f @ $%.4f"
                (t.At.ToString("u")) (TradeAction.toString t.Side) (Qty.value t.Qty) (Usd.value t.PriceUsd)
    }

let private runDecisions (cfg : AppSettings) (n : int) =
    let db = Persistence.create cfg.DatabasePath
    db.Init cfg.StartingCashUsd
    let cycles = db.RecentDecisionCycles n
    if List.isEmpty cycles then
        printfn "No decision cycles recorded yet."
    else
        printfn "Last %d decision cycle(s), newest first:" (List.length cycles)
        let indented = System.Text.Json.JsonSerializerOptions(WriteIndented = true)
        for (ts, raw) in cycles do
            printfn ""
            printfn "=== %s ===" (ts.ToString("u"))
            try
                use doc = System.Text.Json.JsonDocument.Parse(raw)
                printfn "%s" (System.Text.Json.JsonSerializer.Serialize(doc.RootElement, indented))
            with _ -> printfn "%s" raw

let private runProbe (cfg : AppSettings) =
    task {
        use http = newHttpClient ()

        printfn "Probing Alpaca account..."
        if String.IsNullOrEmpty cfg.Alpaca.KeyId then
            printfn "  (Alpaca keys not set — skipping)"
            printfn "  Set with: dotnet user-secrets set \"Alpaca:KeyId\" \"...\" / \"Alpaca:Secret\" \"...\""
        else
            try
                let trading, _ = makeAlpacaClients cfg
                let! account = trading.GetAccountAsync()
                let! clock   = trading.GetClockAsync()
                printfn "  Account status: %A  cash $%.2f  equity $%.2f"
                    account.Status account.TradableCash (account.Equity.GetValueOrDefault())
                printfn "  Market open: %b  (next open %s)" clock.IsOpen (clock.NextOpenUtc.ToString("u"))
            with ex ->
                eprintfn "  Alpaca probe failed: %s" ex.Message

        printfn ""
        printfn "Probing Stage 1: general news → discovery..."
        if String.IsNullOrEmpty cfg.Llm.ApiKey then
            printfn "  (Gemini API key not set — skipping)"
        else
            try
                let news   = News.create http
                let agent  = Agent.create (makeChatClient cfg) cfg
                let _, data = makeAlpacaClients cfg
                let prices = Prices.create data
                let! general = news.FetchGeneral ()
                printfn "  General headlines: %d" (List.length general)
                let! disc = agent.IdentifyCandidates general
                match disc with
                | Error e -> eprintfn "  Discovery failed: %s" e
                | Ok ((trend, candidates), _raw) ->
                    printfn "  Market trend: %s" trend
                    let capped = candidates |> List.truncate cfg.Discovery.MaxCandidatesPerCycle
                    printfn "  Candidates (%d):" (List.length capped)
                    // Liquidity gate per candidate
                    let survivors = System.Collections.Generic.List<PriceSnapshot>()
                    for c in capped do
                        let! priced = prices.FetchOne c.Ticker
                        match priced with
                        | None ->
                            printfn "    %-6s  no price (excluded)         — %s" (Asset.value c.Ticker) c.Reason
                        | Some (snap, addv) ->
                            let pass =
                                Usd.value snap.PriceUsd >= cfg.Discovery.MinSharePrice
                                && addv >= cfg.Discovery.MinAvgDollarVolume
                            printfn "    %-6s  $%-8.2f ADDV $%-12.0f %s — %s"
                                (Asset.value c.Ticker) (Usd.value snap.PriceUsd) addv
                                (if pass then "PASS" else "below floor") c.Reason
                            if pass then survivors.Add snap
                    // Stage 2: one batched call over all survivors
                    match List.ofSeq survivors with
                    | [] -> printfn "  (no survivors to evaluate in Stage 2)"
                    | snaps ->
                        printfn ""
                        printfn "  Stage 2 (batched, %d assets):" (List.length snaps)
                        let portfolio = { CashUsd = Usd cfg.StartingCashUsd; Positions = Map.empty; AsOf = DateTimeOffset.UtcNow }
                        let! items =
                            snaps
                            |> List.map (fun snap ->
                                task {
                                    let! tnews = news.FetchForTicker snap.Asset
                                    return snap, tnews
                                })
                            |> System.Threading.Tasks.Task.WhenAll
                        let! d = agent.EvaluateAssets trend portfolio (List.ofArray items)
                        match d with
                        | Error e -> eprintfn "    failed: %s" e
                        | Ok (decs, _raw) ->
                            for dec in decs do
                                printfn "    %-6s %-4s $%.2f conf=%.2f manip=%s — %s"
                                    (Asset.value dec.Asset) (TradeAction.toString dec.Action)
                                    (Usd.value dec.SizeUsd) dec.Confidence
                                    (ManipulationRisk.toString dec.ManipulationRisk) dec.Rationale
            with ex ->
                eprintfn "  Discovery probe failed: %s" ex.Message
    }

let private buildOrch (logger : ILogger) (cfg : AppSettings) : Broker * Orchestrator =
    let db          = Persistence.create cfg.DatabasePath
    db.Init cfg.StartingCashUsd
    let trading, data = makeAlpacaClients cfg
    let prices      = Prices.create data
    let http        = newHttpClient ()
    let news        = News.create http
    let broker      = makeBroker cfg db trading prices
    let chat        = makeChatClient cfg
    let agent       = Agent.create chat cfg
    let orch        = Orchestrator.create logger cfg db prices news agent broker
    broker, orch

let private runOnce (logger : ILogger) (cfg : AppSettings) =
    task {
        let _, orch = buildOrch logger cfg
        let! summary = orch.RunCycle ()
        logger.LogInformation(
            sprintf "Cycle done: %d decisions, %d orders, %d fills, %d errors"
                (List.length summary.Decisions) (List.length summary.Orders)
                (List.length summary.Fills) (List.length summary.Errors))
        for e in summary.Errors do logger.LogWarning(sprintf "%s" e)
    }

let private runLoop (logger : ILogger) (cfg : AppSettings) (cancel : CancellationToken) =
    task {
        let broker, orch = buildOrch logger cfg
        let interval = TimeSpan.FromHours cfg.CycleIntervalHours
        do! Orchestrator.runLoop logger broker.IsMarketOpen orch interval cancel
    }

[<EntryPoint>]
let main argv =
    try
        let cfg = Config.load ()
        printConfig cfg
        match argv with
        | [| "--probe" |] ->
            (runProbe cfg).GetAwaiter().GetResult()
            0
        | [| "--once" |] ->
            let logger = makeLogger ()
            (runOnce logger cfg).GetAwaiter().GetResult()
            0
        | [| "--report" |] ->
            (runReport cfg).GetAwaiter().GetResult()
            0
        | [| "--decisions" |] ->
            runDecisions cfg 3
            0
        | [| "--decisions"; nStr |] ->
            let n = match Int32.TryParse nStr with | true, v -> v | _ -> 3
            runDecisions cfg n
            0
        | [||] ->
            let logger = makeLogger ()
            use cts = new CancellationTokenSource()
            Console.CancelKeyPress.Add(fun e ->
                e.Cancel <- true
                logger.LogInformation("Ctrl-C received — stopping after current cycle")
                cts.Cancel())
            logger.LogInformation(sprintf "Starting loop at %g h cadence" cfg.CycleIntervalHours)
            (runLoop logger cfg cts.Token).GetAwaiter().GetResult()
            0
        | _ ->
            eprintfn "Usage: TradingBot [--probe | --once | --report | --decisions [n]]"
            eprintfn "  (no args)       run the cycle loop (hourly, only while the market is open)"
            eprintfn "  --probe         smoke-test Alpaca + the two-stage discovery pipeline"
            eprintfn "  --once          run a single cycle and exit"
            eprintfn "  --report        portfolio with live mark-to-market P&L + recent trades"
            eprintfn "  --decisions [n] print the last n LLM decision cycles (default 3)"
            1
    with ex ->
        eprintfn "Failed: %s" ex.Message
        eprintfn "%s" ex.StackTrace
        1
