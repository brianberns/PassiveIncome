namespace StockTradingBot

open System

module Log =

    /// Logs a portfolio.
    let private logPortfolio result =
        printfn ""
        printfn "Portfolio:"
        match result with
            | Ok portfolio ->
                for (asset, value) in Map.toSeq portfolio.PositionMap do
                    printfn $"   {asset}: %.3f{value.Quantity} shares @ \
                        {value.CurrentPrice}/share = {value.Value}"
                printfn $"   Cash: {portfolio.TradableCash}"
                printfn $"   Total value: {portfolio.TotalValue}"
            | Error message ->
                printfn $"   Error: %s{message}"

    /// Logs a market overview.
    let private logMarketOverview result =
        printfn ""
        printfn "Market overview:"
        match result with
            | MarketOverviewResult.Success (newsItems, overview) ->
                printfn $"Trend: {overview.Trend}"
                let candidates =
                    overview.Candidates
                        |> Seq.map _.Asset.Symbol
                        |> String.concat ", "
                printfn $"Candidates: {candidates}"
                printfn $"News items: {newsItems.Length}"
            | FeedErrors errors ->
                for error in errors do
                    printfn $"News feed error: {error.FeedName}: {error.Message}"
            | MarketOverviewResult.AgentError message ->
                printfn $"Agent error: {message}"

    /// Logs asset recommendations.
    let private logAssetRecommendations result =
        printfn ""
        printfn "Recommendations:"
        match result with
            | AssetRecommendationResult.Success results ->
                for result in results do
                    match result with
                        | Ok (newsItems, reco) ->
                            printfn ""
                            printfn $"{reco.Asset.Symbol}: {reco.Action}"
                            printfn $"{reco.Reason}"
                            printfn $"News items: {newsItems.Length}"
                        | Error (asset : Asset, message) ->
                            printfn ""
                            printfn $"Asset error: {asset}: {message}"
            | AssetRecommendationResult.AgentError message ->
                printfn ""
                printfn $"Agent error: {message}"

    /// Logs asset orders.
    let private logAssetOrders sellResults buyResults =
        printfn ""
        printfn "Orders:"
        let count =
            Array.length sellResults + Array.length buyResults
        if count > 0 then
            for (sellResult : OrderResult) in sellResults do
                match sellResult.Result with
                    | Ok detail ->
                        printfn $"   Sold %.3f{detail.FilledQuantity} shares of \
                            {sellResult.Asset} @ {detail.AverageFillPrice}: \
                            {detail.TotalPrice} total"
                    | Error message ->
                        printfn $"   Sell error: {sellResult.Asset}: {message}"
            for (buyResult : OrderResult) in buyResults do
                match buyResult.Result with
                    | Ok detail ->
                        printfn $"   Bought %.3f{detail.FilledQuantity} shares of \
                            {buyResult.Asset} @ {detail.AverageFillPrice}: \
                            {detail.TotalPrice} total"
                    | Error message ->
                        printfn $"   Buy error: {buyResult.Asset}: {message}"
        else
            printfn "   None"

    /// Logs a run.
    let logRun runResult =

        printfn ""
        printfn "-----------------------------------------"
        printfn ""
        printfn $"{runResult.StartTime}"

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

        let duration =
            (runResult.EndTime - runResult.StartTime)
                .ToString(@"m\:ss\.ff")
        printfn ""
        printfn $"Duration: {duration} (m:ss)"
