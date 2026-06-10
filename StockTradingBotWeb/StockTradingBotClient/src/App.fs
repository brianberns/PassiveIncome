namespace StockTradingBot

open System

open Fable.Core

open Feliz
open Elmish
open Elmish.React

module App =

    /// How often to automatically refresh the page.
    let private refreshInterval = TimeSpan.FromHours(1.0)

    type State = Result<RunResult[], string>

    type Msg =

        /// Fetch the latest results from the server.
        | Refresh

        /// Replace the current state with a fetched result.
        | Update of State

    /// Command that fetches the latest results.
    let private fetchResults =
        Cmd.OfAsync.perform
            Remoting.getResults
            ()
            Update

    let init () =
        Ok (Array.empty), fetchResults

    let update msg (state : State) =
        match msg with
            | Refresh -> state, fetchResults
            | Update state -> state, Cmd.none

    /// Subscription that dispatches the given message on a timer.
    let private timer (interval : TimeSpan) msg =
        fun dispatch ->
            let intervalId =
                JS.setInterval
                    (fun () -> dispatch msg)
                    (int interval.TotalMilliseconds)
            { new IDisposable with
                member _.Dispose() = JS.clearInterval intervalId }

    /// Periodically refreshes the results.
    let private subscribe _state =
        [ [ "refresh" ], timer refreshInterval Refresh ]

    /// Formats a quantity of shares.
    let private formatQty (quantity : decimal) =
        sprintf "%.3f" (float quantity)

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
        $"Bot ran for {minutes} minutes, {seconds} seconds"

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

    /// Renders a portfolio.
    let private renderPortfolio result =
        section "Portfolio" [
            match result with
                | Ok portfolio ->
                    Html.table [
                        prop.className "positions"
                        prop.children [
                            Html.tbody [
                                for (asset, value) in Map.toSeq portfolio.PositionMap do
                                    Html.tr [
                                        Html.td [
                                            prop.className "symbol"
                                            prop.text (string asset)
                                        ]
                                        Html.td [
                                            prop.className "num"
                                            prop.text $"{formatQty value.Quantity} shares"
                                        ]
                                        Html.td [
                                            prop.className "num"
                                            prop.text $"@ {value.CurrentPrice}"
                                        ]
                                        Html.td [
                                            prop.className "num value"
                                            prop.text (string value.Value)
                                        ]
                                    ]
                            ]
                        ]
                    ]
                    Html.div [
                        prop.className "summary"
                        prop.children [
                            Html.span [
                                prop.children [
                                    Html.text "Cash: "
                                    Html.strong (string portfolio.TradableCash)
                                ]
                            ]
                            Html.span [
                                prop.children [
                                    Html.text "Total value: "
                                    Html.strong (string portfolio.TotalValue)
                                ]
                            ]
                        ]
                    ]
                | Error message ->
                    errorRow $"Error: %s{message}"
        ]

    /// Renders a market overview.
    let private renderMarketOverview result =
        section "Market overview" [
            match result with
                | MarketOverviewResult.Success overview ->
                    Html.div [
                        prop.className "trend"
                        prop.children [
                            Html.span [
                                prop.className "trend-label"
                                prop.text "Trend: "
                            ]
                            Html.span overview.Trend
                        ]
                    ]
                    Html.div [
                        prop.className "candidates"
                        prop.children [
                            Html.span [
                                prop.className "candidates-label"
                                prop.text "Candidates: "
                            ]
                            for candidate in overview.Candidates do
                                Html.span [
                                    prop.className "chip"
                                    prop.title candidate.Reason
                                    prop.text candidate.Asset.Symbol
                                ]
                        ]
                    ]
                | FeedErrors errors ->
                    for error in errors do
                        errorRow $"News feed error: {error.FeedName}: {error.Message}"
                | MarketOverviewResult.AgentError message ->
                    errorRow $"Agent error: {message}"
        ]

    /// Label and CSS class for an asset action.
    let private actionInfo (action : AssetAction) =
        match action with
            | AssetAction.Buy -> "Buy", "action-buy"
            | AssetAction.Sell -> "Sell", "action-sell"
            | AssetAction.Hold -> "Hold", "action-hold"
            | _ -> string action, "action-hold"

    /// Renders asset recommendations.
    let private renderRecommendations result =
        section "Recommendations" [
            match result with
                | AssetRecommendationResult.Success results ->
                    for result in results do
                        match result with
                            | Ok reco ->
                                let label, actionClass = actionInfo reco.Action
                                Html.div [
                                    prop.className "reco"
                                    prop.children [
                                        Html.div [
                                            prop.className "reco-head"
                                            prop.children [
                                                Html.span [
                                                    prop.className "symbol"
                                                    prop.text reco.Asset.Symbol
                                                ]
                                                Html.span [
                                                    prop.className $"badge {actionClass}"
                                                    prop.text label
                                                ]
                                            ]
                                        ]
                                        Html.div [
                                            prop.className "reco-reason"
                                            prop.text reco.Reason
                                        ]
                                    ]
                                ]
                            | Error (asset : Asset, message : string) ->
                                errorRow $"Asset error: {asset}: {message}"
                | AssetRecommendationResult.AgentError message ->
                    errorRow $"Agent error: {message}"
        ]

    /// Renders a single order result.
    let private renderOrder verb (orderResult : OrderResult) =
        match orderResult.Result with
            | Ok detail ->
                Html.div [
                    prop.className "order"
                    prop.children [
                        Html.span [
                            prop.className "order-verb"
                            prop.text (verb : string)
                        ]
                        Html.span [
                            prop.text
                                $"{formatQty detail.FilledQuantity} shares of "
                        ]
                        Html.span [
                            prop.className "symbol"
                            prop.text (string orderResult.Asset)
                        ]
                        Html.span [
                            prop.text $" @ {detail.AverageFillPrice}"
                        ]
                        Html.span [
                            prop.className "num value"
                            prop.text $"{detail.TotalPrice} total"
                        ]
                    ]
                ]
            | Error message ->
                errorRow $"{verb} error: {orderResult.Asset}: {message}"

    /// Renders sell and buy orders.
    let private renderOrders sellResults buyResults =
        section "Orders" [
            let count =
                Array.length sellResults + Array.length buyResults
            if count > 0 then
                for sellResult in sellResults do
                    renderOrder "Sold" sellResult
                for buyResult in buyResults do
                    renderOrder "Bought" buyResult
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
                            prop.text (formatTimestamp runResult.StartTime)
                        ]
                        Html.span [
                            prop.className "run-duration"
                            prop.text
                                (formatDuration runResult.StartTime runResult.EndTime)
                        ]
                    ]
                ]

                    // is the market open? (hacky signal, as in the console)
                let marketIsOpen = runResult.PortfolioResultOpt.IsSome
                if marketIsOpen then
                    match runResult.PortfolioResultOpt with
                        | Some portfolioResult -> renderPortfolio portfolioResult
                        | None -> Html.none
                    match runResult.MarketOverviewResultOpt with
                        | Some overviewResult -> renderMarketOverview overviewResult
                        | None -> Html.none
                    match runResult.RecommendationResultOpt with
                        | Some recoResult -> renderRecommendations recoResult
                        | None -> Html.none
                    renderOrders runResult.SellResults runResult.BuyResults
                else
                    Html.div [
                        prop.className "closed"
                        prop.text "Market is closed"
                    ]
            ]
        ]

    let render (state : State) (dispatch : Msg -> unit) =
        Html.div [
            prop.className "app"
            prop.children [
                Html.h1 [
                    prop.className "app-title"
                    prop.text "Stock Trading Bot"
                ]
                Html.div [
                    prop.className "refresh-note"
                    prop.text
                        $"Results refresh automatically every \
                            {refreshInterval.TotalHours} hour(s)."
                ]
                match state with
                    | Ok results ->
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

    Program.mkProgram init update render
        |> Program.withSubscription subscribe
        |> Program.withReactSynchronous "elmish-app"
        |> Program.withConsoleTrace
        |> Program.run
