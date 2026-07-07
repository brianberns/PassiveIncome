namespace StockTradingBot

open System
open System.Net.Http
open System.Collections.Generic

open Microsoft.Extensions.Configuration

open FSharp.Control

open Fable.Remoting.Server
open Fable.Remoting.Suave

module Api =

    let private capacity = 10

    /// Run results in chronological order (i.e. oldest first).
    let private runResults = LinkedList<RunResult>()

    let runLoop context =
        let delay = TimeSpan.FromMinutes(15.0)
        async {
            for result in Run.runLoop context delay do
                lock runResults (fun () ->

                        // collapse redundant results
                    if not result.IsMarketOpen
                        && runResults.Count > 0
                        && not runResults.Last.Value.IsMarketOpen then
                        runResults.RemoveLast() |> ignore

                        // remove oldest result?
                    while runResults.Count >= capacity do
                        runResults.RemoveFirst()

                        // add new result
                    runResults.AddLast(result) |> ignore)
        }

    let runLoopDummy (context : RunContext) =
        async {
            let result =
                RunResult.create
                    DateTimeOffset.Now
                    true
                    (Ok (Portfolio.create (Usd 123.4m) Map.empty))
                    None
                    Array.empty
                    Array.empty
                    DateTimeOffset.Now
            lock runResults (fun () ->
                runResults.AddLast(result) |> ignore)
        }

    /// Settings.
    let settings =
        {|
            UserAgent = "StockTradingBot/0.1 (mailto:brianberns@gmail.com)"
#if DEBUG
            Model = Model.gitHub
            CreateBroker = AlpacaDummy.createBroker
#else
            Model = Model.gemini
            CreateBroker = Alpaca.createBroker
#endif
            Run = runLoop
        |}

    /// Creates a run context, reading secrets from the web part's directory.
    let createContext dir =

        /// HTTP client for fetching news feeds.
        let httpClient =
            let client = new HttpClient()
            client.DefaultRequestHeaders
                .UserAgent
                .ParseAdd(settings.UserAgent)   // default value causes 429 errors from Yahoo
            client

        /// Program configuration.
        let config =
            ConfigurationBuilder()
                .SetBasePath(dir)                               // web part's own directory
                .AddUserSecrets(typeof<RunContext>.Assembly)    // local development
                .AddJsonFile("secrets.json", optional = true)   // hosted deployment (e.g. Everleap)
                .Build()

        /// Decision-making agent.
        let agent = Agent.create config settings.Model

        /// Broker for buying/selling assets.
        let broker = settings.CreateBroker config

        RunContext.create httpClient agent broker

    /// Stock trading bot API.
    let stockTradingBotApi dir =

            // spawn workflow that generates results
        async {
            try
                do! createContext dir
                    |> settings.Run
            with exn ->
                printfn $"{exn.Message}"
        } |> Async.Start
            
        {
            GetResults =
                fun () ->
                    async {
                        return lock runResults (fun () ->
                            Seq.toArray runResults)
                    }
        }

module Remoting =

    /// Build API.
    let webPart dir =
        Remoting.createApi()
            |> Remoting.fromValue (Api.stockTradingBotApi dir)
            |> Remoting.buildWebPart
