module TradingBot.Program

open System
open System.ClientModel
open System.Net.Http
open System.Threading
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

let private makeLogger () : ILogger =
    let factory =
        LoggerFactory.Create(fun b ->
            b.AddSimpleConsole(fun o ->
                o.SingleLine     <- true
                o.TimestampFormat <- "HH:mm:ss "
            ) |> ignore)
    factory.CreateLogger("TradingBot")

let private printConfig (cfg : AppSettings) =
    printfn "TradingBot v0.1"
    printfn "  Starting cash:   $%g"      cfg.StartingCashUsd
    printfn "  Cycle interval:  %g hours" cfg.CycleIntervalHours
    printfn "  Assets:          %s"       (String.concat ", " cfg.Assets)
    printfn "  Broker:          %s"       cfg.Broker
    printfn "  Database:        %s"       cfg.DatabasePath
    printfn "  LLM model:       %s"       cfg.Llm.Model
    printfn "  LLM endpoint:    %s"       cfg.Llm.Endpoint
    let secretStatus key value =
        if String.IsNullOrEmpty value then sprintf "%s: (missing)" key
        else sprintf "%s: ****" key
    printfn "  Secrets — %s" (secretStatus "Gemini key" cfg.Llm.ApiKey)
    printfn ""

let private runReport (cfg : AppSettings) =
    let db = Persistence.create cfg.DatabasePath
    db.Init cfg.StartingCashUsd
    let portfolio = db.GetPortfolio ()
    printfn "Portfolio (from %s):" cfg.DatabasePath
    printfn "  Cash:      $%s"     (Usd.value portfolio.CashUsd |> string)
    printfn "  Positions: %d"      portfolio.Positions.Count
    for KeyValue(asset, p) in portfolio.Positions do
        printfn "    %-4s qty=%.8f  avgCost=$%.4f"
            (Asset.value asset) (Qty.value p.Qty) (Usd.value p.AvgCostUsd)
    printfn "  As of:     %s"      (portfolio.AsOf.ToString("u"))
    let recent = db.RecentTrades (24.0 * 7.0)
    printfn "  Trades (last 7d): %d" (List.length recent)
    for t in recent |> List.truncate 10 do
        printfn "    %s %-4s qty=%.8f @ $%.4f (fee $%.4f)"
            (t.At.ToString("u"))
            (TradeAction.toString t.Side)
            (Qty.value t.Qty)
            (Usd.value t.PriceUsd)
            (Usd.value t.FeeUsd)

let private runProbe (cfg : AppSettings) =
    task {
        use http = newHttpClient ()
        let assets = cfg.Assets |> List.map Asset

        printfn "Probing CoinGecko..."
        try
            let prices = Prices.create http
            let! snapshots = prices.Fetch assets
            for p in snapshots do
                printfn "  %-4s $%-12s  24h: %+6.2f%%   7d: %+6.2f%%"
                    (Asset.value p.Asset)
                    (Usd.value p.PriceUsd |> string)
                    p.Change24hPct
                    p.Change7dPct
        with ex ->
            eprintfn "  CoinGecko probe failed: %s" ex.Message

        printfn ""
        printfn "Probing RSS feeds..."
        try
            let news = News.create http
            let! items = news.Fetch assets
            printfn "  Received %d headlines:" (List.length items)
            for n in items |> List.truncate 10 do
                printfn "  [%-13s] %s"
                    (n.Source.Substring(0, min 13 n.Source.Length))
                    n.Title
        with ex ->
            eprintfn "  RSS probe failed: %s" ex.Message

        printfn ""
        printfn "Probing Gemini (LLM)..."
        if String.IsNullOrEmpty cfg.Llm.ApiKey then
            printfn "  (Gemini API key not set — skipping)"
            printfn "  Set with: dotnet user-secrets set \"Llm:ApiKey\" \"YOUR_KEY\""
        else
            try
                let chat = makeChatClient cfg
                let agent = Agent.create chat cfg
                let portfolio = {
                    CashUsd   = Usd cfg.StartingCashUsd
                    Positions = Map.empty
                    AsOf      = DateTimeOffset.UtcNow
                }
                let prices = Prices.create http
                let! priceSnapshots = prices.Fetch assets
                let! result = agent.Propose portfolio priceSnapshots [] []
                match result with
                | Error e -> eprintfn "  Gemini probe failed: %s" e
                | Ok (decisions, _raw) ->
                    printfn "  Received %d decisions:" (List.length decisions)
                    for d in decisions do
                        printfn "    %-4s %-4s $%.2f conf=%.2f — %s"
                            (Asset.value d.Asset)
                            (TradeAction.toString d.Action)
                            (Usd.value d.SizeUsd)
                            d.Confidence
                            d.Rationale
            with ex ->
                eprintfn "  Gemini probe failed: %s" ex.Message
    }

let private buildOrch
    (logger : ILogger)
    (cfg : AppSettings)
    (http : HttpClient)
    : Persistence * Orchestrator =
    let db     = Persistence.create cfg.DatabasePath
    db.Init cfg.StartingCashUsd
    let prices = Prices.create http
    let news   = News.create http
    let broker = PaperBroker.create db prices cfg.Risk
    let chat   = makeChatClient cfg
    let agent  = Agent.create chat cfg
    let orch   = Orchestrator.create logger cfg db prices news agent broker
    db, orch

let private runOnce (logger : ILogger) (cfg : AppSettings) =
    task {
        use http = newHttpClient ()
        let _, orch = buildOrch logger cfg http
        let! summary = orch.RunCycle ()
        logger.LogInformation(
            sprintf "Cycle done: %d decisions, %d orders, %d fills, %d errors"
                (List.length summary.Decisions)
                (List.length summary.Orders)
                (List.length summary.Fills)
                (List.length summary.Errors))
        for e in summary.Errors do
            logger.LogWarning(sprintf "%s" e)
    }

let private runLoop (logger : ILogger) (cfg : AppSettings) (cancel : CancellationToken) =
    task {
        use http = newHttpClient ()
        let _, orch = buildOrch logger cfg http
        let interval = TimeSpan.FromHours cfg.CycleIntervalHours
        do! Orchestrator.runLoop logger orch interval cancel
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
            runReport cfg
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
            eprintfn "Usage: TradingBot [--probe | --once | --report]"
            eprintfn "  (no args)   run the cycle loop at the configured cadence"
            eprintfn "  --probe     smoke-test CoinGecko, CryptoPanic, and Gemini"
            eprintfn "  --once      run a single cycle and exit"
            eprintfn "  --report    print current portfolio and recent trades"
            1
    with ex ->
        eprintfn "Failed: %s" ex.Message
        eprintfn "%s" ex.StackTrace
        1
