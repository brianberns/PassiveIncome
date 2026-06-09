namespace StockTradingBot

open System
open System.Net.Http

open Microsoft.Extensions.Configuration

open FSharp.Control

module Program =

    let runLoop context =
        let delay = TimeSpan.FromHours(1)
        async {
            for result in Run.runLoop context delay do
                Log.logRun result
        }

    /// Settings.
    let settings =
        {|
            UserAgent = "StockTradingBot/0.1 (mailto:brianberns@gmail.com)"
            Model = Model.gemini
            CreateBroker = AlpacaDummy.createBroker
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

    do
        Console.OutputEncoding <- Text.Encoding.UTF8
        settings.Run context
            |> Async.RunSynchronously
