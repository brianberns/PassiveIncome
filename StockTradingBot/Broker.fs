namespace StockTradingBot

open Microsoft.Extensions.Configuration

open Alpaca.Markets

[<StructuredFormatDisplay("{String}")>]
type Money =
    | Usd of decimal

    member money.String =
        let (Usd usd) = money
        $"${usd}"

    override money.ToString() =
        money.String

type Asset = Symbol of string

type AssetValue =
    {
        Quantity : decimal
        AverageEntryPrice : Money
    }

module AssetValue =

    let create quantity averageEntryPrice =
        {
            Quantity = quantity
            AverageEntryPrice = averageEntryPrice
        }

type Portfolio =
    {
        TradableCash : Money
        PositionMap : Map<Asset, AssetValue>
    }

module Portfolio =

    let create tradableCash positionMap =
        {
            TradableCash = tradableCash
            PositionMap = positionMap
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
                // get available cash
            let! account = broker.TradingClient.GetAccountAsync()
            let cash = Usd account.TradableCash

                // get positions
            let! positions =
                broker.TradingClient.ListPositionsAsync()
            let positionMap =
                Map [
                    for position in positions do
                        let asset = Symbol position.Symbol
                        let value =
                            AssetValue.create
                                position.Quantity
                                (Usd position.AverageEntryPrice)
                        asset, value
                ]

            return Portfolio.create cash positionMap
        } |> Async.AwaitTask

    let isMarketOpen broker =
        task {
            let! clock = broker.TradingClient.GetClockAsync()
            return clock.IsOpen
        } |> Async.AwaitTask
