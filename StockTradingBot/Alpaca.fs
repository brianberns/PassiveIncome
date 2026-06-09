namespace StockTradingBot

open System
open System.Threading.Tasks

open Microsoft.Extensions.Configuration

open Alpaca.Markets

module Alpaca =

    /// Alpaca API.
    type private Api =
        {
            DataClient : IAlpacaDataClient
            TradingClient : IAlpacaTradingClient
        }

    /// Gets the current portfolio.
    let private getPortfolio api =
        task {
            try
                    // get available cash
                let! account = api.TradingClient.GetAccountAsync()
                let cash = Usd account.TradableCash

                    // get positions
                let! positions =
                    api.TradingClient.ListPositionsAsync()
                let positionMap =
                    Map [
                        for position in positions do
                            let asset = Asset.create position.Symbol
                            let value =
                                let price =
                                    position.AssetCurrentPrice
                                        |> Option.ofNullable
                                        |> Option.defaultValue
                                            position.AverageEntryPrice   // ick, but hopefully won't happen
                                AssetValue.create
                                    position.Quantity
                                    (Usd price)
                            asset, value
                    ]

                return Ok (Portfolio.create cash positionMap)
            with exn ->
                return Error exn
        } |> Async.AwaitTask

    /// Is the market currently open?
    let private isMarketOpen api =
        task {
            try
                let! clock = api.TradingClient.GetClockAsync()
                return Ok clock.IsOpen
            with exn ->
                return Error exn
        } |> Async.AwaitTask

    /// Waits a while (but not forever) for the given order
    /// to fill, and then answers its average fill price
    /// and filled quantity.
    let private awaitOrder api orderId =

        let rec loop n =
            task {
                do! Task.Delay 250
                let! order =
                    api.TradingClient.GetOrderAsync(orderId : Guid)
                match order.OrderStatus with

                        // order succeeded
                    | OrderStatus.Filled as status ->
                        match Option.ofNullable order.AverageFillPrice with
                            | Some price ->
                                return Ok (
                                    FilledOrderDetail.create
                                        (Usd price)
                                        order.FilledQuantity)
                            | None ->
                                return Error (Some status)   // hopefully this can never happen

                        // order failed
                    | OrderStatus.Canceled
                    | OrderStatus.Rejected
                    | OrderStatus.Expired
                    | OrderStatus.Stopped as status ->
                        return Error (Some status)

                        // try again?
                    | _ when n < 25 ->
                        return! loop (n + 1)

                        // give up waiting (e.g. market is closed)
                    | _ ->
                        return Error None
            }

        loop 0

    /// Places the given order.
    let private placeOrder api (order : MarketOrder) =
        task {
            try
                let! posted =
                    api.TradingClient.PostOrderAsync(order)
                match! awaitOrder api posted.OrderId with
                    | Ok detail ->
                        return Ok detail
                    | Error statusOpt ->
                        let msg =
                            statusOpt
                                |> Option.map string
                                |> Option.defaultValue "Unknown"
                        return Error (exn(msg))
            with exn ->
                return Error exn
        } |> Async.AwaitTask

    /// Sells the given quantity of the given asset.
    let private sell api asset quantity =
        async {
            let order =
                MarketOrder.Sell(
                    asset.Symbol,
                    OrderQuantity.Fractional(quantity))
            return! placeOrder api order
        }

    /// Buys the given asset with the given money.
    let private buy api asset (Usd usd) =
        async {
            let usd = truncate (usd * 100m) / 100m   // Alpaca: notional value must be limited to 2 decimal places
            let order =
                MarketOrder.Buy(
                    asset.Symbol,
                    OrderQuantity.Notional(usd))
            return! placeOrder api order
        }

    /// Creates a broker.
    let createBroker (config : IConfiguration) =
        let key =
            let keyId = config["Alpaca:KeyId"]
            let secret = config["Alpaca:Secret"]
            SecretKey(keyId, secret)
        let env = Environments.Paper
        let api =
            {
                DataClient = env.GetAlpacaDataClient(key)
                TradingClient = env.GetAlpacaTradingClient(key)
            }
        {
            GetPortfolio = fun () -> getPortfolio api
            IsMarketOpen = fun () -> isMarketOpen api
            Sell = sell api
            Buy = buy api
        }

module AlpacaDummy =

    /// Creates a broker that is always open.
    let createBroker config =
        let impl = Alpaca.createBroker config
        {
            GetPortfolio =
                impl.GetPortfolio
            IsMarketOpen =
                fun () -> async { return Ok true }
            Sell =
                fun asset quantity ->
                    async {
                        return Ok (
                            FilledOrderDetail.create
                                (Usd 10m)
                                quantity)
                    }
            Buy =
                fun asset spend ->
                    async { return Error (exn "Dummy") }
        }
