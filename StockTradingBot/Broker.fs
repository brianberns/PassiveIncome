namespace StockTradingBot

open Microsoft.Extensions.Configuration

open Alpaca.Markets

type Broker =
    {
        DataClient : IAlpacaDataClient
        TradingClient : IAlpacaTradingClient
    }

module Broker =

    let create (config : IConfiguration) =
        let key =
            let keyId = config["Alpaca:KeyId"]
            let secret = config["Alpaca:Secret"]
            SecretKey(keyId, secret)
        let env = Environments.Paper
        {
            DataClient = env.GetAlpacaDataClient(key)
            TradingClient = env.GetAlpacaTradingClient(key)
        }
