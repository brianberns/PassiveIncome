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
    let broker = Broker.create config

    let httpClient =
        let client = new HttpClient()
        client.DefaultRequestHeaders
            .UserAgent
            .ParseAdd("StockTradingBot/0.1 (mailto:brianberns@gmail.com)")   // needed to avoid 429 errors from Yahoo
        client

    let printPortfolio (portfolio : Portfolio) =
        printfn "Portfolio"
        printfn $"   Tradable cash: {portfolio.TradableCash}"
        for (asset, value) in Map.toSeq portfolio.PositionMap do
            printfn $"   {asset}: {value.Quantity} @ {value.AverageEntryPrice}"

    let printMarketOverview marketOverview =
        printfn $"Trend: {marketOverview.Trend}"
        printfn ""
        printfn "Candidates:"
        for candidate in marketOverview.Candidates do
            printfn ""
            printfn $"{candidate.Asset.Symbol}"
            printfn $"{candidate.Reason}"

    let printAssetRecommendation (result : AssetRecommendationResult) =
        match result with
            | Success results ->
                printfn "Recommendations:"
                for result in results do
                    printfn ""
                    match result with
                        | Ok reco ->
                            printfn $"{reco.Asset.Symbol}: {reco.Action}"
                            printfn $"{reco.Reason}"
                        | Error (asset, exn) ->
                            printfn $"Asset error: {asset}: {exn.Message}"
            | AgentError exn ->
                printfn $"Asset recommendation error: {exn.Message}"

    let runOverview marketOverview =
        async {
            printfn ""
            printMarketOverview marketOverview
            let! result =
                AssetRecommendation.getAsync
                    httpClient agent
                    marketOverview.Trend
                    marketOverview.Candidates
            printfn ""
            printAssetRecommendation result
        }

    let run () =
        async {
            match! Broker.getPortfolio broker with
                | Ok portfolio ->
                    printPortfolio portfolio
                    match! MarketOverview.getAsync httpClient agent with
                        | MarketOverviewResult.Success overview ->
                            do! runOverview overview
                        | FeedErrors errors ->
                            for feed, exn in errors do
                                printfn $"News feed error: {feed.Name}: {exn.Message}"
                        | MarketOverviewResult.AgentError exn ->
                            printfn $"Market overview error: {exn.Message}"
                | Error exn ->
                    printfn $"Portfolio error: {exn.Message}"
        } |> Async.RunSynchronously

    Console.OutputEncoding <- Text.Encoding.UTF8
    run ()
