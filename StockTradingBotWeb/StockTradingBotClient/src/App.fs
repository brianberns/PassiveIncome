namespace StockTradingBot

open Feliz
open Elmish
open Elmish.React

module Remoting =

    open Fable.Remoting.Client

    /// Prefix routes with /StockTradingBot.
    let routeBuilder typeName methodName = 
        sprintf "/StockTradingBot/%s/%s" typeName methodName

    /// Server API.
    let api =
        Remoting.createApi()
            |> Remoting.withRouteBuilder routeBuilder
            |> Remoting.buildProxy<IStockTradingBotApi>

    let getResults () =
        async {
            match! Async.Catch(api.GetResults ()) with
                | Choice1Of2 results -> return results
                | Choice2Of2 exn -> return failwith exn.Message
        }

module App =

    type State = RunResult[]

    type Msg =
        | ResultsReceived of RunResult[]

    let init () =
        let cmd =
            Cmd.OfAsync.perform
                Remoting.getResults
                ()
                ResultsReceived
        Array.empty, cmd

    let update msg (state : State) =
        match msg with
            | ResultsReceived results ->
                results, Cmd.none

    let render state (dispatch : Msg -> unit) =
        Html.div [
            for runResult in state do
                Html.div [
                    prop.text "Result"
                ]
        ]

    Program.mkProgram init update render
        |> Program.withReactSynchronous "elmish-app"
        |> Program.withConsoleTrace
        |> Program.run
