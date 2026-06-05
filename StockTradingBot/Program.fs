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
                | Success overview ->
                    printfn $"Trend: {overview.Trend}"
                    printfn ""
                    printfn "Candidates:"
                    for candidate in overview.Candidates do
                        printfn ""
                        printfn $"{candidate.Asset.Symbol}"
                        printfn $"{candidate.Reason}"
                    match! AssetRecommendation.getAsync httpClient agent overview with
                        | Ok recos ->
                            printfn ""
                            printfn "Recommendations:"
                            for reco in recos do
                                printfn ""
                                printfn $"{reco.Asset.Symbol}: {reco.Action}"
                                printfn $"{reco.Reason}"
                        | Error exn ->
                            printfn $"Asset recommendation error: {exn.Message}"
                | FeedErrors errors ->
                    for feed, exn in errors do
                        printfn $"News feed error: {feed.Name}: {exn.Message}"
                | AgentError exn ->
                    printfn $"Market overview error: {exn.Message}"
        } |> Async.RunSynchronously

    let test () =
        let broker = Broker.create config
        async {
            let! portfolio = Broker.getPortfolio broker
            printfn "%A" portfolio
            let! isOpen = Broker.isMarketOpen broker
            printfn "%A" isOpen
        } |> Async.RunSynchronously

    Console.OutputEncoding <- Text.Encoding.UTF8
    run ()
