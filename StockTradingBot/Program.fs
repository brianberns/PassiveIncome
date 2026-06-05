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
                    for candidate in overview.Candidates do
                        printfn $"{candidate.Symbol}: {candidate.Reason}"
                | FeedErrors errors ->
                    for feed, exn in errors do
                        printfn $"Error in {feed.Name} news feed: {exn.Message}"
                | ChatError exn ->
                    printfn $"{exn.Message}"
        } |> Async.RunSynchronously

    let test () =
        let broker = Broker.create config
        async {
            let! portfolio = Broker.getPortfolio broker
            printfn "%A" portfolio
            let! isOpen = Broker.isMarketOpen broker
            printfn "%A" isOpen
            match! Broker.getBars (Symbol "AAPL") broker with
                | Ok bars ->
                    for bar in bars do
                        printfn "%A: %A - %A" (bar.TimeUtc.ToLocalTime()) (Usd bar.Open) (Usd bar.Close)
                | Error exn -> printfn "%A" exn
        } |> Async.RunSynchronously

    let test2() =
        async {
            let! result =
                AssetInvestigation.getAsync
                    httpClient agent [Symbol "AAPL"; Symbol "MRVL"]
            printfn "%A" result
        } |> Async.RunSynchronously

    Console.OutputEncoding <- Text.Encoding.UTF8
    // run ()
    test2 ()
