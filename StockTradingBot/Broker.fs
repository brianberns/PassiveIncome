namespace StockTradingBot

open System
open System.Threading.Tasks

open Microsoft.Extensions.Configuration

open Alpaca.Markets

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

    let private awaitOrder orderId broker =

        let rec loop n =
            task {
                do! Task.Delay 250
                let! order =
                    broker.TradingClient.GetOrderAsync(orderId : Guid)
                match order.OrderStatus with
                    | OrderStatus.Filled as status ->
                        match Option.ofNullable order.AverageFillPrice with
                            | Some price ->
                                return Ok (Usd price)
                            | None ->
                                return Error (Some status)
                    | OrderStatus.Canceled
                    | OrderStatus.Rejected
                    | OrderStatus.Expired
                    | OrderStatus.Stopped as status ->
                        return Error (Some status)
                    | _ when n < 25 ->
                        return! loop (n + 1)
                    | _ ->
                        return Error None
            }

        loop 0

    let private placeOrder (order : MarketOrder)  broker =
        task {
            try
                let! posted =
                    broker.TradingClient.PostOrderAsync(order)
                match! awaitOrder posted.OrderId broker with
                    | Ok totalPrice ->
                        return Ok totalPrice
                    | Error statusOpt ->
                        let msg =
                            statusOpt
                                |> Option.map string
                                |> Option.defaultValue "Unknown"
                        return Error (exn(msg))
            with exn ->
                return Error exn
        } |> Async.AwaitTask

    let sell asset quantity broker =
        async {
            let order =
                MarketOrder.Sell(
                    asset.Symbol,
                    OrderQuantity.Fractional(quantity))
            return! placeOrder order broker
        }

    let buy asset (Usd usd) broker =
        async {
            let usd = (usd * 100m) / 100m   // Alpaca: notional value must be limited to 2 decimal places
            let order =
                MarketOrder.Buy(
                    asset.Symbol,
                    OrderQuantity.Notional(usd))
            return! placeOrder order broker
        }
