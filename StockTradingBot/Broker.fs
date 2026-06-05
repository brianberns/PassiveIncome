namespace StockTradingBot

open Microsoft.Extensions.Configuration
open Alpaca.Markets

/// Money, cash, moola...
[<StructuredFormatDisplay("{String}")>]
type Money =

    /// U.S. dollars ($).
    | Usd of decimal

    /// Display string.
    member money.String =
        let (Usd usd) = money
        $"${usd}"

    /// Display string.
    override money.ToString() =
        money.String

/// An investment asset, such as stock in a company.
type Asset =
    {
        /// Asset symbol. E.g. Apple = "AAPL".
        Symbol : string
    }

module Asset =

    /// Creates an asset.
    let create symbol =
        { Symbol = symbol }

/// Value of an asset in a portfolio.
type AssetValue =
    {
        /// Amount of asset in the portfolio.
        Quantity : decimal

        /// Average entry price of the asset in the portfolio.
        AverageEntryPrice : Money
    }

module AssetValue =

    /// Creates an asset value.
    let create quantity averageEntryPrice =
        {
            Quantity = quantity
            AverageEntryPrice = averageEntryPrice
        }

/// An investment portfolio.
type Portfolio =
    {
        /// Tradable cash in the portfolio.
        TradableCash : Money

        /// Assets in the portfolio.
        PositionMap : Map<Asset, AssetValue>
    }

module Portfolio =

    /// Creates a portfolio.
    let create tradableCash positionMap =
        {
            TradableCash = tradableCash
            PositionMap = positionMap
        }

/// Broker for buying/selling assets.
type Broker =
    {
        /// Data client.
        DataClient : IAlpacaDataClient

        /// Trading client.
        TradingClient : IAlpacaTradingClient
    }

module Broker =

    /// Creates a broker.
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

    /// Gets the current portfolio at the given broker.
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

    /// Is the given broker's market currently open?
    let isMarketOpen broker =
        task {
            try
                let! clock = broker.TradingClient.GetClockAsync()
                return Ok clock.IsOpen
            with exn ->
                return Error exn
        } |> Async.AwaitTask
