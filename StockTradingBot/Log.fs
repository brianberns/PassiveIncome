namespace StockTradingBot

open System

/// Result of selling an asset.
type SellResult =
    {
        /// Asset being sold.
        Asset : Asset

        /// Number of shares to sell.
        Quantity : decimal

        /// Result of sale.
        Result : Result<Money (*avg. fill price*), exn>
    }

module SellResult =

    /// Creates a sell result.
    let create asset quantity result =
        {
            Asset = asset
            Quantity = quantity
            Result = result
        }

/// Result of buying an asset.
type BuyResult =
    {
        /// Asset being bought.
        Asset : Asset

        /// Amount to spend.
        Spend : Money

        /// Result of purchase.
        Result : Result<Money (*avg. fill price*), exn>
    }

module BuyResult =

    /// Creates a buy result.
    let create asset spend result =
        {
            Asset = asset
            Spend = spend
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
        SellResults : SellResult[]

        /// Buy results.
        BuyResults : BuyResult[]
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
            for (sellResult : SellResult) in sellResults do
                let msg =
                    match sellResult.Result with
                        | Ok avgPrice ->
                            $"{sellResult.Quantity * avgPrice} total"
                        | Error exn -> exn.Message
                printfn $"   Sell {sellResult.Quantity} shares of {sellResult.Asset}: {msg}"
            for (buyResult : BuyResult) in buyResults do
                let msg =
                    match buyResult.Result with
                        | Ok _ ->
                            $"{buyResult.Spend} total"
                        | Error exn -> exn.Message
                printfn $"   Buy {buyResult}: {msg}"
        else
            printfn "   None"

    /// Logs a run.
    let logRun runResult =
        printfn ""
        printfn "-----------------------------------------"
        printfn ""
        printfn $"{DateTime.Now}"
        Option.iter logPortfolio runResult.PortfolioResultOpt
        Option.iter logMarketOverview runResult.MarketOverviewResultOpt
        Option.iter logAssetRecommendations runResult.RecommendationResultOpt
        logAssetOrders runResult.SellResults runResult.BuyResults
