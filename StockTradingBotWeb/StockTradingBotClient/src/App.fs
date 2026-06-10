namespace StockTradingBot

open Feliz
open Elmish
open Elmish.React

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

    let render (state : State) (dispatch : Msg -> unit) =
        Html.div [
            for result in state do
                Html.div [
                    prop.text $"Result: {result.StartTime}"
                ]
        ]

    Program.mkProgram init update render
        |> Program.withReactSynchronous "elmish-app"
        |> Program.withConsoleTrace
        |> Program.run
