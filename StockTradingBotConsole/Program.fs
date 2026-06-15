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

    let runOne context =
        async {
            let! result = Run.runOne context
            Log.logRun result
        }

    /// Settings.
    let settings =
        {|
            UserAgent = "StockTradingBot/0.1 (mailto:brianberns@gmail.com)"
#if DEBUG
            Model = Model.openRouter
            CreateBroker = AlpacaDummy.createBroker
            Run = runOne
#else
            Model = Model.gemini
            CreateBroker = Alpaca.createBroker
            Run = runLoop
#endif
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
        let alpacaApi = Alpaca.getApi config
        let broker = settings.CreateBroker alpacaApi

        RunContext.create httpClient agent alpacaApi broker

    do
        Console.OutputEncoding <- Text.Encoding.UTF8
        settings.Run context
            |> Async.RunSynchronously
