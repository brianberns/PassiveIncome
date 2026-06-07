namespace StockTradingBot

open System
open System.Net.Http
open System.Reflection

open Microsoft.Extensions.Configuration

module Program =

    /// Run context.
    let context =

        /// Program configuration.
        let config =
            let assembly = Assembly.GetExecutingAssembly()
            ConfigurationBuilder()
                .AddUserSecrets(assembly)
                .Build()

        /// Decision-making agent.
        let agent = Agent.create config Model.gemini

        /// Broker for buying/selling assets.
        let broker = Broker.create config

        /// HTTP client for fetching news feeds.
        let httpClient =
            let client = new HttpClient()
            client.DefaultRequestHeaders
                .UserAgent
                .ParseAdd("StockTradingBot/0.1 (mailto:brianberns@gmail.com)")   // needed to avoid 429 errors from Yahoo
            client

        RunContext.create agent broker httpClient

    do
        Console.OutputEncoding <- Text.Encoding.UTF8
        Run.runLoop context |> Async.RunSynchronously
