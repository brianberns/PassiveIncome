namespace StockTradingBot

open System

/// Result of placing an order.
type OrderResult =
    {
        /// Asset traded.
        Asset : Asset

        /// Result of trade.
        Result :
            Result<
                Money (*avg. fill price*)
                    * decimal (*filled quantity*),
                exn>
    }

module OrderResult =

    /// Creates an order result.
    let create asset result =
        {
            Asset = asset
            Result = result
        }

/// Result of a single run.
type RunResult =
    {
        /// Portfolio at start of run.
        PortfolioResultOpt : Option<Result<Portfolio, exn>>

        /// Market overview.
        MarketOverviewResultOpt : Option<MarketOverviewResult>

        /// Asset recommentation.
        RecommendationResultOpt : Option<AssetRecommendationResult>

        /// Sell results.
        SellResults : OrderResult[]

        /// Buy results.
        BuyResults : OrderResult[]
    }

module RunResult =

    /// Creates a run result.
    let create
        portfolioResultOpt
        marketOverviewResultOpt
        recommendationResultOpt
        sellResults
        buyResults =
        {
            PortfolioResultOpt = portfolioResultOpt
            MarketOverviewResultOpt = marketOverviewResultOpt
            RecommendationResultOpt = recommendationResultOpt
            SellResults = sellResults
            BuyResults = buyResults
        }

    /// Creates a run result.
    let createWithoutRecommendation
        portfolioResultOpt
        marketOverviewResultOpt =
        create
            portfolioResultOpt
            marketOverviewResultOpt
            None Array.empty Array.empty

    /// Creates a run result.
    let createWithoutOverview portfolioResultOpt =
        createWithoutRecommendation
            portfolioResultOpt
            None

module Log =

    /// Logs a portfolio.
    let private logPortfolio result =
        printfn ""
        printfn "Portfolio:"
        match result with
            | Ok portfolio ->
                printfn $"   Tradable cash: {portfolio.TradableCash}"
                for (asset, value) in Map.toSeq portfolio.PositionMap do
                    printfn $"   {asset}: {value.Quantity} @ {value.AverageEntryPrice}"
            | Error (exn : exn) ->
                printfn $"   Error: {exn.Message}"

    /// Logs a market overview.
    let private logMarketOverview result =
        printfn ""
        printfn "Market overview:"
        match result with
            | MarketOverviewResult.Success overview ->
                printfn $"Trend: {overview.Trend}"
                let candidates =
                    overview.Candidates
                        |> Seq.map _.Asset.Symbol
                        |> String.concat ", "
                printfn $"Candidates: {candidates}"
            | FeedErrors errors ->
                for feed, exn in errors do
                    printfn $"News feed error: {feed.Name}: {exn.Message}"
            | MarketOverviewResult.AgentError exn ->
                printfn $"Agent error: {exn.Message}"

    /// Logs asset recommendations.
    let private logAssetRecommendations result =
        printfn ""
        printfn "Recommendations:"
        match result with
            | AssetRecommendationResult.Success results ->
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
            | AssetRecommendationResult.AgentError exn ->
                printfn $"Agent error: {exn.Message}"

    /// Logs asset orders.
    let private logAssetOrders sellResults buyResults =
        printfn ""
        printfn "Orders:"
        let count =
            Array.length sellResults + Array.length buyResults
        if count > 0 then
            for (sellResult : OrderResult) in sellResults do
                match sellResult.Result with
                    | Ok (avgPrice, quantity) ->
                        printfn $"   Sold {quantity} shares of {sellResult.Asset} \
                            @ {avgPrice}: {quantity * avgPrice} total"
                    | Error exn ->
                        printfn $"Sell error {exn.Message}"
            for (buyResult : OrderResult) in buyResults do
                match buyResult.Result with
                    | Ok (avgPrice, quantity) ->
                        printfn $"   Bought {quantity} shares of {buyResult.Asset} \
                            @ {avgPrice}: {quantity * avgPrice} total"
                    | Error exn ->
                        printfn $"Buy error {exn.Message}"
        else
            printfn "   None"

    /// Logs a run.
    let logRun runResult =

        printfn ""
        printfn "-----------------------------------------"
        printfn ""
        printfn $"{DateTime.Now}"

            // is market open?
        let marketIsOpen =
            runResult.PortfolioResultOpt.IsSome   // to-do: this is a hacky signal
        if marketIsOpen then
            Option.iter
                logPortfolio
                runResult.PortfolioResultOpt
            Option.iter
                logMarketOverview
                runResult.MarketOverviewResultOpt
            Option.iter
                logAssetRecommendations
                runResult.RecommendationResultOpt
            logAssetOrders
                runResult.SellResults runResult.BuyResults
        else
            assert(runResult.MarketOverviewResultOpt.IsNone)
            assert(runResult.RecommendationResultOpt.IsNone)
            assert(runResult.SellResults.Length = 0)
            assert(runResult.BuyResults.Length = 0)
            printfn "Market is closed"

