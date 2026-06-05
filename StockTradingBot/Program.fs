namespace StockTradingBot

open System
open System.Net.Http
open System.Reflection

open Microsoft.Extensions.Configuration

module Program =

    let config =
        let assembly = Assembly.GetExecutingAssembly()
        ConfigurationBuilder()
            .AddUserSecrets(assembly)
            .Build()

    let agent = Agent.create config

    let httpClient =
        let client = new HttpClient()
        client.DefaultRequestHeaders
            .UserAgent
            .ParseAdd("StockTradingBot/0.1 (mailto:brianberns@gmail.com)")   // needed to avoid 429 errors from Yahoo
        client

    let run () =

        async {
            match! MarketOverview.getAsync httpClient agent with
                | Overview overview ->
                    printfn $"Trend: {overview.Trend}"
                    printfn ""
                    printfn "Candidates:"
                    for candidate in overview.Candidates do
                        printfn ""
                        printfn $"{candidate.Asset.Symbol}"
                        printfn $"{candidate.Reason}"
                    match! AssetInvestigation.getAsync httpClient agent overview with
                        | Ok invs ->
                            printfn ""
                            printfn "Decisions:"
                            for inv in invs do
                                printfn ""
                                printfn $"{inv.Asset.Symbol}: {inv.Action}"
                                printfn $"{inv.Reason}"
                        | Error exn ->
                            printfn $"Asset investigation error: {exn.Message}"
                | FeedErrors errors ->
                    for feed, exn in errors do
                        printfn $"News feed error: {feed.Name}: {exn.Message}"
                | ChatError exn ->
                    printfn $"Market overview error: {exn.Message}"
        } |> Async.RunSynchronously

    let test () =
        let broker = Broker.create config
        async {
            let! portfolio = Broker.getPortfolio broker
            printfn "%A" portfolio
            let! isOpen = Broker.isMarketOpen broker
            printfn "%A" isOpen
            match! Broker.getBars (Asset.create "AAPL") broker with
                | Ok bars ->
                    for bar in bars do
                        printfn "%A: %A - %A" (bar.TimeUtc.ToLocalTime()) (Usd bar.Open) (Usd bar.Close)
                | Error exn -> printfn "%A" exn
        } |> Async.RunSynchronously

    Console.OutputEncoding <- Text.Encoding.UTF8
    run ()
