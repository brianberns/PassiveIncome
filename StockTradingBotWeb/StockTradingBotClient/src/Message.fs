namespace StockTradingBot

open System
open Fable.Core
open Elmish

type State = Result<RunResult[], string>

type Message =

    /// Fetch the latest results from the server.
    | Refresh

    /// Replace the current state with a fetched result.
    | Update of State

module Message =

    /// How often to automatically refresh.
    let refreshInterval = TimeSpan.FromHours(1.0)

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
    let subscribe _state =
        [ [ "refresh" ], timer refreshInterval Refresh ]
