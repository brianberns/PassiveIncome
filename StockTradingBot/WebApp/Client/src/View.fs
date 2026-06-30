namespace StockTradingBot

open System
open Feliz

module View =

    /// Formats a quantity of shares.
    let private formatQty (quantity : decimal) =
        sprintf "%.3f" (float quantity)

    /// Formats a net change in value, with an explicit sign for
    /// increases (decreases are already signed by Money).
    let private formatNetChange (netChange : Money) =
        let (Usd amount) = netChange
        let sign = if amount > 0m then "+" else ""
        $"{sign}{netChange}"

    /// Formats a timestamp as e.g. "Jun 10, 2026 3:43 PM".
    let private formatTimestamp (time : DateTimeOffset) =
        let date = time.ToString("MMM d, yyyy")
        let hour =
            match time.Hour % 12 with   // .Hour is offset-aware; Fable's "tt" token is not
                | 0 -> 12
                | h -> h
        let minute = sprintf "%02d" time.Minute
        let amPm = if time.Hour < 12 then "AM" else "PM"
        $"{date} {hour}:{minute} {amPm}"

    /// Describes the duration between two times, rounded to the
    /// nearest second.
    let private formatDuration (startTime : DateTimeOffset) (endTime : DateTimeOffset) =
        let totalSeconds =
            (endTime - startTime).TotalSeconds |> round |> int
        let minutes = totalSeconds / 60
        let seconds = totalSeconds % 60
        $"Bot ran for {minutes} min., {seconds} sec."

    /// A titled section within a run card.
    let private section title children =
        Html.div [
            prop.className "section"
            prop.children [
                Html.h3 [
                    prop.className "section-title"
                    prop.text (title : string)
                ]
                yield! children
            ]
        ]

    /// Renders an error message.
    let private errorRow (message : string) =
        Html.div [
            prop.className "error"
            prop.text message
        ]

    /// Renders news items behind a collapsible disclosure. The items
    /// are hidden by default and revealed when the user expands them.
    let private renderNews (newsItems : NewsItem[]) =
        if Array.isEmpty newsItems then
            Html.none
        else
            Html.details [
                prop.className "news"
                prop.children [
                    Html.summary [
                        prop.className "news-summary"
                        prop.text
                            (if newsItems.Length = 1 then "1 news item"
                             else $"{newsItems.Length} news items")
                    ]
                    Html.div [
                        prop.className "news-items"
                        prop.children [
                            for item in newsItems do
                                Html.div [
                                    prop.className "news-item"
                                    prop.children [
                                        Html.div [
                                            prop.className "news-title"
                                            prop.text item.Title
                                        ]
                                        Html.div [
                                            prop.className "news-date"
                                            prop.text (formatTimestamp item.PublishDate)
                                        ]
                                        Html.div [
                                            prop.className "news-item-summary"
                                            prop.text item.Summary
                                        ]
                                    ]
                                ]
                        ]
                    ]
                ]
            ]

    /// Renders a label/value row at the foot of the positions table
    /// (e.g. cash, total). Only the Asset and Value columns are filled.
    let private summaryRow extraClasses label (amount : Money) =
        Html.tr [
            prop.classes [ "summary-row"; yield! extraClasses ]
            prop.children [
                Html.td [
                    prop.className "summary-label"
                    prop.text (label : string)
                ]
                Html.td []
                Html.td []
                Html.td [
                    prop.className "num value"
                    prop.text (string amount)
                ]
                Html.td []
            ]
        ]

    /// Renders a portfolio.
    let private renderPortfolio result =
        section "Portfolio" [
            match result with
                | Ok portfolio ->
                    Html.table [
                        prop.className "positions"
                        prop.children [
                            Html.thead [
                                Html.tr [
                                    Html.th [ prop.text "Asset" ]
                                    Html.th [ prop.className "num"; prop.text "Shares" ]
                                    Html.th [ prop.className "num"; prop.text "Price" ]
                                    Html.th [ prop.className "num"; prop.text "Value" ]
                                    Html.th [ prop.className "num"; prop.text "Change" ]
                                ]
                            ]
                            Html.tbody [
                                for (asset, value) in Map.toSeq portfolio.PositionMap do
                                    Html.tr [
                                        Html.td [
                                            prop.className "symbol"
                                            prop.text (string asset)
                                        ]
                                        Html.td [
                                            prop.className "num"
                                            prop.text (formatQty value.Quantity)
                                        ]
                                        Html.td [
                                            prop.className "num"
                                            prop.text (string value.CurrentPrice)
                                        ]
                                        Html.td [
                                            prop.className "num value"
                                            prop.text (string value.Value)
                                        ]
                                        let (Usd netChange) = value.NetChange
                                        Html.td [
                                            prop.classes [
                                                "num"
                                                "change"
                                                if netChange < 0m then "change-down"
                                            ]
                                            prop.text (formatNetChange value.NetChange)
                                        ]
                                    ]
                                summaryRow [] "Cash" portfolio.TradableCash
                                summaryRow [ "divider"; "total" ] "Total value" portfolio.TotalValue
                            ]
                        ]
                    ]
                | Error message ->
                    errorRow $"Error: %s{message}"
        ]

    /// Renders a market assessment.
    let private renderMarketAssessment result =
        section "Market assessment" [
            match result with
                | MarketAssessmentResult.Success (newsItems, assessment) ->
                    Html.div [
                        prop.className "state"
                        prop.text assessment.State
                    ]
                    renderNews newsItems
                | FeedErrors errors ->
                    for error in errors do
                        errorRow $"News feed error: {error.FeedName}: {error.Message}"
                | MarketAssessmentResult.AgentError message ->
                    errorRow $"Agent error: {message}"
        ]

    /// Renders a single order result. The symbol is prefixed with a
    /// color-coded pill indicating the side (green to buy, red to sell).
    let private renderOrder verb label badgeClass (orderResult : OrderResult) =
        Html.div [
            prop.className "order"
            prop.children [
                Html.div [
                    prop.className "order-head"
                    prop.children [
                        Html.span [
                            prop.className $"badge {badgeClass}"
                            prop.text (label : string)
                        ]
                        Html.span [
                            prop.className "symbol"
                            prop.text (string orderResult.Asset)
                        ]
                    ]
                ]
                Html.div [
                    prop.className "order-reason"
                    prop.text orderResult.Reason
                ]
                match orderResult.Result with
                    | Ok detail ->
                        Html.div [
                            prop.className "order-line"
                            prop.children [
                                Html.span [
                                    prop.text (verb : string)
                                ]
                                Html.span [
                                    prop.text
                                        $"{formatQty detail.FilledQuantity} shares @ {detail.AverageFillPrice}:"
                                ]
                                Html.span [
                                    prop.className "num value"
                                    prop.text $"{detail.TotalPrice} total"
                                ]
                            ]
                        ]
                    | Error message ->
                        errorRow $"{verb} error: {message}"
            ]
        ]

    /// Renders sell and buy orders.
    let private renderOrders sellResults buyResults =
        section "Orders" [
            let count =
                Array.length sellResults + Array.length buyResults
            if count > 0 then
                for sellResult in sellResults do
                    renderOrder "Sold" "Sell" "action-sell" sellResult
                for buyResult in buyResults do
                    renderOrder "Bought" "Buy" "action-buy" buyResult
            else
                Html.div [
                    prop.className "muted"
                    prop.text "None"
                ]
        ]

    /// Renders a single run as a card.
    let private renderRun (runResult : RunResult) =
        Html.div [
            prop.className "run-card"
            prop.children [
                Html.div [
                    prop.className "run-header"
                    prop.children [
                        Html.span [
                            prop.className "run-time"
                            prop.text (formatTimestamp runResult.EndTime)
                        ]
                        Html.span [
                            prop.className "run-duration"
                            prop.text
                                (formatDuration runResult.StartTime runResult.EndTime)
                        ]
                    ]
                ]

                    // is the market open?
                if runResult.IsMarketOpen then
                    match runResult.PortfolioResultOpt with
                        | Some portfolioResult -> renderPortfolio portfolioResult
                        | None -> Html.none
                    match runResult.MarketAssessmentResultOpt with
                        | Some assessmentResult -> renderMarketAssessment assessmentResult
                        | None -> Html.none
                    renderOrders runResult.SellResults runResult.BuyResults
                else
                    Html.div [
                        prop.className "closed"
                        prop.text "Market is closed"
                    ]
            ]
        ]

    let render (state : State) (dispatch : Message -> unit) =
        Html.div [
            prop.className "app"
            prop.children [
                Html.h1 [
                    prop.className "app-title"
                    prop.text "Stock Trading Bot"
                ]
                Html.div [
                    prop.className "source-link"
                    prop.children [
                        Html.a [
                            prop.href "https://github.com/brianberns/PassiveIncome/tree/main/StockTradingBot"
                            prop.target "_blank"
                            prop.rel "noopener noreferrer"
                            prop.text "View source code"
                        ]
                    ]
                ]
                match state with
                    | Ok None -> ()
                    | Ok (Some results) ->
                        if Array.isEmpty results then
                            Html.div [
                                prop.className "muted"
                                prop.text "No results yet."
                            ]
                        else
                            for result in Array.rev results do   // most recent first
                                renderRun result
                    | Error message ->
                        Html.div [
                            prop.className "error"
                            prop.text message
                        ]
            ]
        ]
