namespace StockTradingBot

/// Result of placing an order.
type OrderResult =
    {
        /// Asset traded.
        Asset : Asset

        /// Reason for trade.
        Reason : string

        /// Relative price change, if known.
        PriceChangeOpt : Option<decimal>

        /// Result of trade.
        Result : Result<FilledOrderDetail, string (*message*)>
    }

module OrderResult =

    /// Creates an order result.
    let create asset reason priceChangeOpt result =
        {
            Asset = asset
            Reason = reason
            PriceChangeOpt = priceChangeOpt
            Result = result
        }

#if !FABLE_COMPILER

open FSharp.Control

module Order =

    /// Organizes assets by trend (positive/negative).
    let private getTrendMaps assessment =

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

        lookup Trend.Positive,
        lookup Trend.Negative

    type private SellRequest =
        {
            Asset : Asset
            Quantity : decimal
            Reason : string
        }

    module private SellRequest =

        let create asset quantity reason =
            {
                Asset = asset
                Quantity = quantity
                Reason = reason
            }

    /// Sells the given asset quantities.
    let private sellAssetQuantities broker saleRequests =
        saleRequests
            |> Seq.map (fun req ->
                async {
                    let! result =
                        broker.Sell req.Asset req.Quantity
                    return OrderResult.create
                        req.Asset req.Reason None result
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

    type private BuyRequest =
        {
            Asset : Asset
            Reason : string
            PriceChangeOpt : Option<decimal>
        }

    module private BuyRequest =

        let create asset reason priceChangeOpt =
            {
                Asset = asset
                Reason = reason
                PriceChangeOpt = priceChangeOpt
            }

    /// Buys the given asset.
    let private buyAsset broker buyRequest money =
        async {
            let! result = broker.Buy buyRequest.Asset money
            return OrderResult.create
                buyRequest.Asset
                buyRequest.Reason
                buyRequest.PriceChangeOpt
                result
        }

    /// Buys the given assets using the given cash.
    let private buyAssets broker (buyRequests : _[]) (cash : Money) =
        let portion = cash / decimal buyRequests.Length   // amount to spend on each asset
        if portion > Money.One then                       // Alpaca: notional amount must be >= 1.00
            buyRequests
                |> Seq.map (fun req ->
                    buyAsset broker req portion)
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
            let buyMap, sellMap = getTrendMaps assessment

                // decide what to do with assets in existing portfolio
            let sellRequests =
                [
                    for (KeyValue(asset, value)) in portfolio.PositionMap do
                        match Map.tryFind asset sellMap with

                                // sell, explicit reason
                            | Some reason ->
                                SellRequest.create
                                    asset value.Quantity reason

                                // hold, possibly buying more of this asset
                            | None when buyMap.ContainsKey(asset) ->
                                ()

                                // sell, to generate cash
                            | None when buyMap.Count > 0 ->
                                SellRequest.create
                                    asset value.Quantity "Making room in portfolio."

                                // hold, no reason to sell
                            | None -> ()
                ]

                // sell first to generate cash
            let! sellResults = sellAssetQuantities broker sellRequests
            let cash = getSpendableCash portfolio sellResults

                // buy assets with cash on hand
            if buyMap.Count > 0 && cash > slush then   // don't try to spend a trivial amount
                let! buyResults =
                    let buyRequests =
                        buyMap
                            |> Map.toArray
                            |> Array.map (fun (asset, reason) ->
                                BuyRequest.create asset reason None)
                    buyAssets broker buyRequests cash
                return sellResults, buyResults
            else
                return sellResults, Array.empty
        }

#endif
