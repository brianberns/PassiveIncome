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
    let agent = Agent.create config Model.gemini

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
            |> Array.where (fun reco ->
                reco.Action <> AssetAction.Hold)
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
                    return SellResult.create
                        asset quantity result
                })
            |> Async.Sequential   // avoid hammering the broker API

    /// Slush to avoid spending more than we have.
    let slush = Usd 1m

    /// Gets spendable cash from portfolio and sold assets.
    let getSpendableCash portfolio sellResults =
        let totalSales =
            sellResults
                |> Seq.sumBy (fun (result : SellResult) ->
                    match result.Result with
                        | Ok (avgPrice : Money) ->
                            result.Quantity * avgPrice
                        | Error _ -> Money.Zero)
        portfolio.TradableCash + totalSales - slush

    /// Buys the given assets using the given cash.
    let buyAssets (assets : _[]) (cash : Money) =
        let portion = cash / decimal assets.Length   // amount to spend on each asset
        assets
            |> Seq.map (fun asset ->
                async {
                    let! result =
                        Broker.buy asset portion broker
                    return BuyResult.create
                        asset portion result
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

    let runOverview portfolio marketOverview =
        async {
                // get asset recommendations for all candidates
            let! recoResult =
                getRecommendations portfolio marketOverview

                // make trades based on recommendations
            match recoResult with
                | Success recoDetailResults ->
                    let! sellResults, buyResults =
                        recoDetailResults
                            |> Array.choose (function
                                | Ok reco -> Some reco
                                | _ -> None)
                            |> placeOrders portfolio
                    return recoResult, sellResults, buyResults
                | AgentError _ ->
                    return recoResult, Array.empty, Array.empty
        }

    let runOne () =
        async {
            match! Broker.isMarketOpen broker with
                | Ok true ->
                    match! Broker.getPortfolio broker with
                        | Ok portfolio ->
                            let! marketOverviewResult =
                                MarketOverview.getAsync httpClient agent
                            match marketOverviewResult with
                                | MarketOverviewResult.Success overview ->
                                    let! recoResult, sellResults, buyResults =
                                        runOverview portfolio overview
                                    return RunResult.create
                                        (Some (Ok portfolio))
                                        (Some marketOverviewResult)
                                        (Some recoResult)
                                        sellResults
                                        buyResults
                                | _ ->
                                    return RunResult.createWithoutRecommendation
                                        (Some (Ok portfolio))
                                        (Some marketOverviewResult)
                        | Error exn ->
                            return RunResult.createWithoutOverview
                                (Some (Error exn))
                | Ok false ->
                    return RunResult.createWithoutOverview
                        None
                | Error exn ->
                    return RunResult.createWithoutOverview
                        (Some (Error exn))
        }

    let rec runLoop () =
        async {
            let! runResult = runOne ()
            Log.logRun runResult
            do! Async.Sleep(TimeSpan.FromHours(1))
            do! runLoop ()
        }

    Console.OutputEncoding <- Text.Encoding.UTF8
    runLoop () |> Async.RunSynchronously
