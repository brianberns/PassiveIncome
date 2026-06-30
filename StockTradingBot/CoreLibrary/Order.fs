namespace StockTradingBot

/// Result of placing an order.
type OrderResult =
    {
        /// Asset traded.
        Asset : Asset

        /// Reason for trade.
        Reason : string

        /// Result of trade.
        Result : Result<FilledOrderDetail, string (*message*)>
    }

module OrderResult =

    /// Creates an order result.
    let create asset reason result =
        {
            Asset = asset
            Reason = reason
            Result = result
        }

#if !FABLE_COMPILER

open FSharp.Control

module Order =

    /// Sells the given asset quantities.
    let private sellAssetQuantities broker assetTuples =
        assetTuples
            |> Seq.map (fun (asset, quantity, reason) ->
                async {
                    let! result =
                        broker.Sell asset quantity
                    return OrderResult.create asset reason result
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

    /// Buys the given asset.
    let private buyAsset broker asset money reason =
        async {
            let! result = broker.Buy asset money
            return OrderResult.create asset reason result
        }

    /// Buys the given assets using the given cash.
    let private buyAssets broker (assetTuples : _[]) (cash : Money) =
        let portion = cash / decimal assetTuples.Length   // amount to spend on each asset
        if portion > Money.One then                       // Alpaca: notional amount must be >= 1.00
            assetTuples
                |> Seq.map (fun (asset, reason) ->
                    buyAsset broker asset portion reason)
                |> Async.Sequential   // avoid hammering the broker API
        else
            async { return Array.empty }

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
                                        aa.Asset, aa.Reason)
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
                            | Some reason ->
                                asset, value.Quantity, reason

                                // hold, buying more of this asset
                            | None when buyMap.ContainsKey(asset) -> ()

                                // sell, to generate cash
                            | None when buyMap.Count > 0 ->
                                asset, value.Quantity, "Making room in portfolio."

                                // hold, no reason to sell
                            | None -> ()
                ]

                // sell first to generate cash
            let! sellResults = sellAssetQuantities broker sellTuples
            let cash = getSpendableCash portfolio sellResults

                // buy assets with cash on hand
            if buyMap.Count > 0 && cash > slush then   // don't try to spend a trivial amount
                let! buyResults =
                    let buyTuples = Map.toArray buyMap
                    buyAssets broker buyTuples cash
                return sellResults, buyResults
            else
                return sellResults, Array.empty
        }

#endif
