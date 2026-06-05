namespace StockTradingBot

open Microsoft.Extensions.Configuration

open Alpaca.Markets

[<StructuredFormatDisplay("{String}")>]
type Cash =
    | Usd of decimal

    member cash.String =
        let (Usd usd) = cash
        $"${usd}"

    override cash.ToString() =
        cash.String

type Portfolio =
    {
        TradableCash : Cash
    }

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

    let getPortfolio broker =
        task {
            let! account = broker.TradingClient.GetAccountAsync()
            return {
                TradableCash = Usd account.TradableCash
            }
        } |> Async.AwaitTask
