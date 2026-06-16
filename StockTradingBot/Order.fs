namespace StockTradingBot

/// Result of placing an order.
type OrderResult =
    {
        /// Asset traded.
        Asset : Asset

        /// Reason for trade.
        Reason : string

        /// News item IDs supporting this trade.
        NewsItemIds : string[]

        /// Result of trade.
        Result : Result<FilledOrderDetail, string (*message*)>
    }

module OrderResult =

    /// Creates an order result.
    let create asset reason newsItemIds result =
        {
            Asset = asset
            Reason = reason
            NewsItemIds = newsItemIds
            Result = result
        }

#if !FABLE_COMPILER

open FSharp.Control

module Order =

    /// Sells the given asset quantities.
    let private sellAssetQuantities broker assetTuples =
        assetTuples
            |> Seq.map (fun (asset, quantity, reason, newsItemIds) ->
                async {
                    let! result =
                        broker.Sell asset quantity
                    return OrderResult.create
                        asset reason newsItemIds result
                })
            |> Async.Sequential   // avoid hammering the broker API

    /// Slush to avoid spending more than we have.
    let private slush = Usd 1m

    /// Gets spendable cash from portfolio and sold assets.
    let private getSpendableCash portfolio sellResults =
        let totalSales =
            sellResults
                |> Seq.sumBy (fun (result : OrderResult) ->
                    match result.Result with
                        | Ok detail -> detail.TotalPrice
                        | Error _ -> Money.Zero)
        portfolio.TradableCash + totalSales - slush

    /// Buys the given assets using the given cash.
    let private buyAssets broker (assetTuples : _[]) (cash : Money) =
        let portion = cash / decimal assetTuples.Length   // amount to spend on each asset
        assetTuples
            |> Seq.map (fun (asset, reason, newsItemIds) ->
                async {
                    let! result =
                        broker.Buy asset portion
                    return OrderResult.create
                        asset reason newsItemIds result
                })
            |> Async.Sequential   // avoid hammering the broker API

    /// Places orders based on the given assessment.
    let placeOrders broker portfolio assessment =
        assert(
            MarketAssessment.tryFindError assessment
                |> Option.isNone)
        async {
                // organize assets by trend (positive/negative)
            let lookup =
                let trendMap =
                    assessment.AssetAssessments
                        |> Array.groupBy _.Trend
                        |> Array.map (fun (trend, group) ->
                            let map =
                                group
                                    |> Array.map (fun aa ->
                                        aa.Asset,
                                        (aa.Reason, aa.NewsItemIds))
                                    |> Map
                            assert(map.Count = group.Length)
                            trend, map)
                        |> Map
                fun trend ->
                    trendMap
                        |> Map.tryFind trend
                        |> Option.defaultValue Map.empty
            let buyMap = lookup Trend.Positive
            let sellMap = lookup Trend.Negative

                // decide what to do with all assets in portfolio
            let sellTuples =
                [
                    for (KeyValue(asset, value)) in portfolio.PositionMap do
                        match Map.tryFind asset sellMap with

                                // sell, explicit reason
                            | Some (reason, newsItemIds) ->
                                asset, value.Quantity, reason, newsItemIds

                                // hold, buying more of this asset
                            | None when buyMap.ContainsKey(asset) -> ()

                                // sell, to generate cash
                            | None when buyMap.Count > 0 ->
                                asset, value.Quantity, "Cash generation.", [||]

                                // hold, no reason to sell
                            | None -> ()
                ]

                // sell first to generate cash
            let! sellResults = sellAssetQuantities broker sellTuples
            let cash = getSpendableCash portfolio sellResults

                // buy assets with cash on hand
            if buyMap.Count > 0 && cash > slush then   // don't try to spend a trivial amount
                let! buyResults =
                    let buyTuples =
                        [|
                            for (KeyValue(asset, (reason, newsItemIds))) in buyMap do
                                asset, reason, newsItemIds
                        |]
                    buyAssets broker buyTuples cash
                return sellResults, buyResults
            else
                return sellResults, Array.empty
        }

#endif
