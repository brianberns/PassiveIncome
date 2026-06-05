namespace StockTradingBot

open System
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

type Asset =
    {
        Symbol : string
    }

module Asset =

    let create symbol =
        { Symbol = symbol }

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
            try
                    // get available cash
                let! account = broker.TradingClient.GetAccountAsync()
                let cash = Usd account.TradableCash

                    // get positions
                let! positions =
                    broker.TradingClient.ListPositionsAsync()
                let positionMap =
                    Map [
                        for position in positions do
                            let asset = Asset.create position.Symbol
                            let value =
                                AssetValue.create
                                    position.Quantity
                                    (Usd position.AverageEntryPrice)
                            asset, value
                    ]

                return Ok (Portfolio.create cash positionMap)
            with exn ->
                return Error exn
        } |> Async.AwaitTask

    let isMarketOpen broker =
        task {
            try
                let! clock = broker.TradingClient.GetClockAsync()
                return Ok clock.IsOpen
            with exn ->
                return Error exn
        } |> Async.AwaitTask

    let getBars asset broker =
        task {
            try
                let! page =
                    let utcNow = DateTime.UtcNow
                    let dtStart = utcNow - TimeSpan.FromDays(14)
                    let dtEnd = utcNow - TimeSpan.FromMinutes(15.1)   // most recent bars not available for free
                    HistoricalBarsRequest(
                        asset.Symbol, dtStart, dtEnd, BarTimeFrame.Day)
                        |> broker.DataClient.ListHistoricalBarsAsync
                return Ok (Seq.toArray page.Items)   // assume one page of results is sufficent
            with exn ->
                return Error exn
        } |> Async.AwaitTask
