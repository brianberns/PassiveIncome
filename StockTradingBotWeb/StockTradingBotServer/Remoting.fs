namespace StockTradingBot

open System
open System.Net.Http

open Microsoft.Extensions.Configuration

open FSharp.Control

open Fable.Remoting.Server
open Fable.Remoting.Suave

module Api =

    let runResults = ResizeArray<RunResult>()

    let runLoop context =
        let delay = TimeSpan.FromHours(1)
        async {
            for result in Run.runLoop context delay do
                lock runResults (fun () ->
                    printfn $"{result.StartTime}"
                    runResults.Add(result))
        }

    /// Settings.
    let settings =
        {|
            UserAgent = "StockTradingBot/0.1 (mailto:brianberns@gmail.com)"
#if DEBUG
            Model = Model.openRouter
            CreateBroker = AlpacaDummy.createBroker
#else
            Model = Model.gemini
            CreateBroker = Alpaca.createBroker
#endif
            Run = runLoop
        |}

    /// Run context.
    let context =

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
                .AddUserSecrets(typeof<RunContext>.Assembly)
                .Build()

        /// Decision-making agent.
        let agent = Agent.create config settings.Model

        /// Broker for buying/selling assets.
        let broker = settings.CreateBroker config

        RunContext.create httpClient agent broker

    /// Stock trading bot API.
    let stockTradingBotApi (dir : string) =
        runLoop context |> Async.Start
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
