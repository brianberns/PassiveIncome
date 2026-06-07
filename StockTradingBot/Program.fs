namespace StockTradingBot

open System
open System.Net.Http
open System.Reflection

open Microsoft.Extensions.Configuration

module Program =

    /// Program configuration.
    let config =
        let assembly = Assembly.GetExecutingAssembly()
        ConfigurationBuilder()
            .AddUserSecrets(assembly)
            .Build()

    /// Decision-making agent.
    let agent = Agent.create config Model.groq

    /// Broker for buying/selling assets.
    let broker = Broker.create config

    /// HTTP client for fetching news feeds.
    let httpClient =
        let client = new HttpClient()
        client.DefaultRequestHeaders
            .UserAgent
            .ParseAdd("StockTradingBot/0.1 (mailto:brianberns@gmail.com)")   // needed to avoid 429 errors from Yahoo
        client

    /// Gets trade recommendations based on the given market
    /// overview.
    let getRecommendations portfolio marketOverview =

        let candidates =
            let portfolioCandidates =
                portfolio.PositionMap.Keys
                    |> Seq.map (fun asset ->
                        Candidate.create asset "In portfolio")
            set [
                yield! portfolioCandidates   // always consider selling assets in portfolio
                yield! marketOverview.Candidates
            ]

        AssetRecommendation.getAsync
            httpClient agent
            marketOverview.Trend
            candidates

    /// Separates sell recommendations from buy recommendations.
    let partition recommendations =
        recommendations
            |> Array.partitionWith (fun reco ->
                match reco.Action with
                    | AssetAction.Sell -> Choice1Of2 reco.Asset
                    | AssetAction.Buy -> Choice2Of2 reco.Asset
                    | _ -> failwith "Unexpected")

    /// Obtains quantity of each of the given assets in the
    /// given portfolio.
    let getQuantities portfolio assets =
        Seq.choose (fun asset ->
            portfolio.PositionMap
                |> Map.tryFind asset
                |> Option.map (fun value ->
                    asset, value.Quantity))
            assets

    /// Sells the given asset quantities.
    let sellAssetQuantities assetQuantities =
        assetQuantities
            |> Seq.map (fun (asset, quantity) ->
                async {
                    let! result =
                        Broker.sell asset quantity broker
                    return asset, quantity, result
                })
            |> Async.Sequential   // avoid hammering the broker API

    /// Slush to avoid spending more than we have.
    let slush = Usd 1m

    /// Gets spendable cash from portfolio and sold assets.
    let getSpendableCash portfolio sellResults =
        let totalSales =
            sellResults
                |> Seq.sumBy (fun (_, quantity, result) ->
                    match result with
                        | Ok (avgPrice : Money) ->
                            quantity * avgPrice
                        | Error _ -> Money.Zero)
        portfolio.TradableCash + totalSales - slush

    /// Buys the given assets using the given cash.
    let buyAssets (assets : _[]) (cash : Money) =
        let portion = cash / decimal assets.Length
        assets
            |> Seq.map (fun asset ->
                async {
                    let! result =
                        Broker.buy asset portion broker
                    return asset, portion, result
                })
            |> Async.Sequential   // avoid hammering the broker API

    /// Places orders based on the given recommendations.
    let placeOrders portfolio recommendations =
        async {
                // sell assets first to generate cash
            let sells, buys =
                partition recommendations
            let! sellResults =
                sells
                    |> getQuantities portfolio   // can only sell assets we own
                    |> sellAssetQuantities
            let cash = getSpendableCash portfolio sellResults

                // buy assets with cash on hand
            if buys.Length > 0 && cash > Money.Zero then
                let! buyResults = buyAssets buys cash
                return sellResults, buyResults
            else
                return sellResults, Array.empty
        }

    let printPortfolio (portfolio : Portfolio) =
        printfn "Portfolio:"
        printfn $"   Tradable cash: {portfolio.TradableCash}"
        for (asset, value) in Map.toSeq portfolio.PositionMap do
            printfn $"   {asset}: {value.Quantity} @ {value.AverageEntryPrice}"

    let printMarketOverview marketOverview =
        printfn $"Trend: {marketOverview.Trend}"
        let candidates =
            marketOverview.Candidates
                |> Seq.map _.Asset.Symbol
                |> String.concat ", "
        printfn $"Candidates: {candidates}"

    let printAssetRecommendations results =
        printfn "Recommendations:"
        for result in results do
            match result with
                | Ok reco ->
                    if reco.Action <> AssetAction.Hold then
                        printfn ""
                        printfn $"{reco.Asset.Symbol}: {reco.Action}"
                        printfn $"{reco.Reason}"
                | Error (asset : Asset, exn : exn) ->
                    printfn ""
                    printfn $"Asset error: {asset}: {exn.Message}"

    let printAssetResults sellResults buyResults =
        let count =
            Array.length sellResults + Array.length buyResults
        if count > 0 then
            printfn "Orders:"
            for asset : Asset, quantity, result in sellResults do
                let msg =
                    match result with
                        | Ok avgPrice ->
                            $"{quantity * avgPrice} total"
                        | Error (exn : exn) -> exn.Message
                printfn $"   Sell {quantity} shares of {asset}: {msg}"
            for asset : Asset, totalPrice : Money, result in buyResults do
                let msg =
                    match result with
                        | Ok _ ->
                            $"{totalPrice} total"
                        | Error (exn : exn) -> exn.Message
                printfn $"   Buy {asset}: {msg}"

    let runOverview portfolio marketOverview =
        async {
                // get asset recommendations for all candidates
            printfn ""
            printMarketOverview marketOverview
            let! result =
                getRecommendations portfolio marketOverview

                // make trades based on recommendations
            printfn ""
            match result with
                | Success results ->
                    printAssetRecommendations results

                    printfn ""
                    match! Broker.isMarketOpen broker with
                        | Ok true ->
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
                        | Ok false -> printfn "Market is closed"
                        | Error exn -> printfn $"Market error: {exn.Message}"

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
