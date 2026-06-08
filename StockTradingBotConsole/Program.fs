namespace StockTradingBot

open System
open System.Net.Http

open Microsoft.Extensions.Configuration

open FSharp.Control

module Program =

    /// Run context.
    let context =

        /// HTTP client for fetching news feeds.
        let httpClient =
            let client = new HttpClient()
            client.DefaultRequestHeaders
                .UserAgent
                .ParseAdd("StockTradingBot/0.1 (mailto:brianberns@gmail.com)")   // needed to avoid 429 errors from Yahoo
            client

        /// Program configuration.
        let config =
            ConfigurationBuilder()
                .AddUserSecrets(typeof<RunContext>.Assembly)
                .Build()

        /// Decision-making agent.
        let agent = Agent.create config Model.gemini

        /// Broker for buying/selling assets.
        let broker = Broker.create config

        RunContext.create httpClient agent broker

    do
        Console.OutputEncoding <- Text.Encoding.UTF8
        let delay = TimeSpan.FromHours(1)
        async {
            for result in Run.runLoop context delay do
                Log.logRun result
        } |> Async.RunSynchronously
