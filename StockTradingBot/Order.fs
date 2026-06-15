namespace StockTradingBot

/// Result of placing an order.
type OrderResult =
    {
        /// Asset traded.
        Asset : Asset

        /// Result of trade.
        Result : Result<FilledOrderDetail, string (*messgae*)>
    }

module OrderResult =

    /// Creates an order result.
    let create asset result =
        {
            Asset = asset
            Result = result
        }

#if !FABLE_COMPILER

open FSharp.Control

module Order =

    /// Obtains quantity of each of the given assets in the
    /// given portfolio.
    let private getQuantities portfolio assets =
        Seq.choose (fun asset ->
            portfolio.PositionMap
                |> Map.tryFind asset
                |> Option.map (fun value ->
                    asset, value.Quantity))
            assets

    /// Sells the given asset quantities.
    let private sellAssetQuantities broker assetQuantities =
        assetQuantities
            |> Seq.map (fun (asset, quantity) ->
                async {
                    let! result =
                        broker.Sell asset quantity
                    return OrderResult.create asset result
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
    let private buyAssets broker (assets : Set<_>) (cash : Money) =
        let portion = cash / decimal assets.Count   // amount to spend on each asset
        assets
            |> Seq.map (fun asset ->
                async {
                    let! result =
                        broker.Buy asset portion
                    return OrderResult.create asset result
                })
            |> Async.Sequential   // avoid hammering the broker API

    /// Places orders based on the given assessment.
    let placeOrders broker portfolio assessment =
        async {
                // organize assets by trend (positive/negative)
            let lookup =
                let actionMap =
                    assessment.AssetAssessments
                        |> Array.groupBy _.Trend
                        |> Array.map (fun (trend, group) ->
                            let assets =
                                group
                                    |> Seq.map _.Asset
                                    |> set
                            trend, assets)
                        |> Map
                fun action ->
                    actionMap
                        |> Map.tryFind action
                        |> Option.defaultValue Set.empty
            let buys = lookup Trend.Positive
            let sells = lookup Trend.Negative

                // sell in-portfolio assets if necessary to fund buys
            let sells =
                if buys.Count > 0 then
                    portfolio.PositionMap.Keys
                        |> Seq.where (fun asset ->
                            buys.Contains(asset) |> not)
                        |> set
                        |> Set.union sells
                else sells

                // sell first to generate cash
            let! sellResults =
                sells
                    |> getQuantities portfolio   // sell all owned shares
                    |> sellAssetQuantities broker
            let cash = getSpendableCash portfolio sellResults

                // buy assets with cash on hand
            if buys.Count > 0 && cash > slush then   // don't try to spend a trivial amount
                let! buyResults = buyAssets broker buys cash
                return sellResults, buyResults
            else
                return sellResults, Array.empty
        }

#endif
