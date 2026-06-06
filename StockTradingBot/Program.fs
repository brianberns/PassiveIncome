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

    let printAssetRecommendations results =
        printfn "Recommendations:"
        for result in results do
            printfn ""
            match result with
                | Ok reco ->
                    printfn $"{reco.Asset.Symbol}: {reco.Action}"
                    printfn $"{reco.Reason}"
                | Error (asset : Asset, exn : exn) ->
                    printfn $"Asset error: {asset}: {exn.Message}"

    let placeOrders portfolio recommendations =

            // separate sells from buys
        let sells, buys =
            recommendations
                |> Array.partitionWith (fun reco ->
                    match reco.Action with
                        | AssetAction.Sell -> Choice1Of2 reco.Asset
                        | AssetAction.Buy -> Choice2Of2 reco.Asset
                        | _ -> failwith "Unexpected")

            // can only sell assets we own
        let sellQuantities =
            Array.choose (fun asset ->
                portfolio.PositionMap
                    |> Map.tryFind asset
                    |> Option.map (fun value ->
                        asset, value.Quantity))
                sells

        async {

                // sell first to generate cash
            let! sellResults =
                sellQuantities
                    |> Seq.map (fun (asset, quantity) ->
                        async {
                            let! result =
                                Broker.sell asset quantity broker
                            return asset, quantity, result
                        })
                    |> Async.Sequential

                // compute spendable cash
            let cash =
                let totalSales =
                    sellResults
                        |> Seq.sumBy (fun (_, quantity, result) ->
                            match result with
                                | Ok avgPrice -> quantity * avgPrice
                                | Error _ -> Money.Zero)
                let slush = Usd 1m
                portfolio.TradableCash + totalSales - slush

                // buy
            if cash > Money.Zero then
                let! buyResults =
                    let portion = cash / decimal buys.Length
                    buys
                        |> Seq.map (fun asset ->
                            async {
                                let! result =
                                    Broker.buy asset portion broker
                                return asset, portion, result
                            })
                        |> Async.Sequential
                return sellResults, buyResults
            else
                return sellResults, Array.empty
        }

    let printAssetResults sellResults buyResults =

        printfn ""
        if Array.isEmpty sellResults then
            printfn "No sales"
        else
            printfn "Sales:"
            for asset, quantity, result in sellResults do
                let msg =
                    match result with
                        | Ok avgPrice ->
                            $"{quantity * avgPrice} total"
                        | Error (exn : exn) -> exn.Message
                printfn $"   Sell {quantity} shares of {asset}: {msg}"

        printfn ""
        if Array.isEmpty buyResults then
            printfn "No buys"
        else
            printfn "Buys:"
            for asset, totalPrice, result in buyResults do
                let msg =
                    match result with
                        | Ok _ ->
                            $"{totalPrice} total"
                        | Error (exn : exn) -> exn.Message
                printfn $"   Buy {asset}: {msg}"

    let runOverview portfolio marketOverview =
        async {
                // get asset recommendations for each candidate
            printfn ""
            printMarketOverview marketOverview
            let! result =
                AssetRecommendation.getAsync
                    httpClient agent
                    marketOverview

                // make trades based on recommendations
            printfn ""
            match result with
                | Success results ->
                    printAssetRecommendations results
                    let recos =
                        results
                            |> Array.choose (function
                                | Ok reco
                                    when reco.Action <> AssetAction.Hold ->
                                    Some reco
                                | _ -> None)
                    let! sellResults, buyResults =
                        placeOrders portfolio recos
                    printAssetResults sellResults buyResults
                | AgentError exn ->
                    printfn $"Asset recommendation error: {exn.Message}"
        }

    let run () =
        async {
            match! Broker.getPortfolio broker with
                | Ok portfolio ->
                    printPortfolio portfolio
                    match! MarketOverview.getAsync httpClient agent with
                        | MarketOverviewResult.Success overview ->
                            do! runOverview portfolio overview
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
