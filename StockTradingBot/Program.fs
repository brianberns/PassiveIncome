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
                | MarketOverviewResult.Success overview ->
                    printfn $"Trend: {overview.Trend}"
                    printfn ""
                    printfn "Candidates:"
                    for candidate in overview.Candidates do
                        printfn ""
                        printfn $"{candidate.Asset.Symbol}"
                        printfn $"{candidate.Reason}"

                    let! result =
                        AssetRecommendation.getAsync httpClient agent overview
                    match result with
                        | AssetRecommendationResult.Success results ->
                            printfn ""
                            printfn "Recommendations:"
                            for result in results do
                                printfn ""
                                match result with
                                    | Ok reco ->
                                        printfn $"{reco.Asset.Symbol}: {reco.Action}"
                                        printfn $"{reco.Reason}"
                                    | Error (asset, exn) ->
                                        printfn $"Asset error: {asset}: {exn.Message}"
                        | AssetRecommendationResult.AgentError exn ->
                            printfn $"Asset recommendation error: {exn.Message}"
                | FeedErrors errors ->
                    for feed, exn in errors do
                        printfn $"News feed error: {feed.Name}: {exn.Message}"
                | MarketOverviewResult.AgentError exn ->
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
