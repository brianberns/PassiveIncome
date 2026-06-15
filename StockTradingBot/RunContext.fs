namespace StockTradingBot

open System.Net.Http

/// Context need to run.
type RunContext =
    {
        /// HTTP client for fetching news feeds.
        HttpClient : HttpClient

        /// Decision-making agent.
        Agent : Agent

        /// Alpaca API.
        AlpacaApi : AlpacaApi

        /// Broker for buying/selling assets.
        Broker : Broker
    }

module RunContext =

    /// Creates a run context.
    let create httpClient agent alpacaApi broker =
        {
            HttpClient = httpClient
            Agent = agent
            AlpacaApi = alpacaApi
            Broker = broker
        }
