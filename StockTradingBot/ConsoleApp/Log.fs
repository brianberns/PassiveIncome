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
                        {value.CurrentPrice}/share = {value.Value} (Net change: {value.NetChange})"
                printfn $"   Cash: {portfolio.TradableCash}"
                printfn $"   Total value: {portfolio.TotalValue}"
            | Error message ->
                printfn $"   Error: %s{message}"

    /// Logs a market assessment.
    let private logMarketAssessment result =
        match result with
            | MarketAssessmentResult.Success (newsItems, assessment) ->
                printfn ""
                printfn $"{assessment.State}"
                printfn ""
                printfn $"News items: {newsItems.Length}"
            | FeedErrors errors ->
                for error in errors do
                    printfn $"News feed error: {error.FeedName}: {error.Message}"
            | MarketAssessmentResult.AgentError message ->
                printfn $"Agent error: {message}"

    /// Logs asset orders.
    let private logAssetOrders sellResults buyResults =
        let count =
            Array.length sellResults + Array.length buyResults
        if count > 0 then
            for (sellResult : OrderResult) in sellResults do
                printfn ""
                printfn $"{sellResult.Asset}:"
                printfn $"{sellResult.Reason}"
                match sellResult.Result with
                    | Ok detail ->
                        printfn $"Sold %.3f{detail.FilledQuantity} shares @ {detail.AverageFillPrice}: \
                            {detail.TotalPrice} total"
                    | Error message ->
                        printfn $"Sell error: {message}"
            for (buyResult : OrderResult) in buyResults do
                printfn ""
                printfn $"{buyResult.Asset}:"
                printfn $"{buyResult.Reason}"
                match buyResult.Result with
                    | Ok detail ->
                        printfn $"Bought %.3f{detail.FilledQuantity} shares @ {detail.AverageFillPrice}: \
                            {detail.TotalPrice} total"
                    | Error message ->
                        printfn $"Buy error: {message}"
        else
            printfn "None"

    /// Logs a run.
    let logRun runResult =

        printfn ""
        printfn "-----------------------------------------"
        printfn ""
        printfn $"{runResult.StartTime}"

            // is market open?
        if runResult.IsMarketOpen then
            Option.iter
                logPortfolio
                runResult.PortfolioResultOpt
            Option.iter
                logMarketAssessment
                runResult.MarketAssessmentResultOpt
            logAssetOrders
                runResult.SellResults runResult.BuyResults
        else
            printfn "Market is closed"

        let duration =
            (runResult.EndTime - runResult.StartTime)
                .ToString(@"m\:ss\.ff")
        printfn ""
        printfn $"Duration: {duration} (m:ss)"
