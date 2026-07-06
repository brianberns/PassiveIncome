namespace StockTradingBot

open System
open System.Threading.Tasks

open Microsoft.Extensions.Configuration

open Alpaca.Markets

module Alpaca =

    /// Alpaca API.
    type private Api =
        {
            /// Data client. (E.g. price history.)
            DataClient : IAlpacaDataClient

            /// Trading client. (E.g. buy/sell assets.)
            TradingClient : IAlpacaTradingClient
        }

    /// Gets the current portfolio.
    let private getPortfolio api =
        task {
            try
                    // get available cash
#if DAY_TRADER_BUG_FIXED
                let! account = api.TradingClient.GetAccountAsync()
                let cash = Usd account.TradableCash
#else
                let cash = Money.One   // bad assumption to work around incompatibility in Alpaca library (see https://docs.alpaca.markets/us/changelog/2026-06-03-pdt-f3c04da)
#endif
                    // get positions
                let! positions =
                    api.TradingClient.ListPositionsAsync()
                let positionMap =
                    Map [
                        for position in positions do
                            let asset = Asset.create position.Symbol
                            let value =
                                let curPrice =
                                    position.AssetCurrentPrice
                                        |> Option.ofNullable
                                        |> Option.defaultValue
                                            position.AverageEntryPrice   // ick, but hopefully won't happen
                                AssetValue.create
                                    position.Quantity
                                    (Usd position.AverageEntryPrice)
                                    (Usd curPrice)
                            asset, value
                    ]

                return Ok (Portfolio.create cash positionMap)
            with exn ->
                return Error exn.Message
        } |> Async.AwaitTask

    /// Is the market currently open?
    let private isMarketOpen api =
        task {
            try
                let! clock = api.TradingClient.GetClockAsync()
                return Ok clock.IsOpen
            with exn ->
                return Error exn.Message
        } |> Async.AwaitTask

    /// Gets recent change in the given asset's price.
    let private getPriceChange api asset =
        task {
            try
                    // get latest price
                let! latest =
                    LatestMarketDataRequest(asset.Symbol)
                        |> api.DataClient.GetLatestTradeAsync

                    // get previous price
                let now = DateTime.UtcNow.AddMinutes(-15.1)   // free API imposes 15 minute delay
                let! page =
                    HistoricalBarsRequest(
                        asset.Symbol,
                        now - TimeSpan.FromHours(1),
                        now,
                        BarTimeFrame.Hour)
                        |> api.DataClient.ListHistoricalBarsAsync

                    // compute percent change
                match Seq.tryHead page.Items with
                    | Some recent ->
                        let change =
                            100.0m * (latest.Price - recent.Close) / recent.Close
                        return Ok (Some change)
                    | None -> return Ok None

            with exn ->
                return Error exn.Message
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
                        return statusOpt
                            |> Option.map string
                            |> Option.defaultValue "Order still in progress"
                            |> Error
            with exn ->
                return Error exn.Message
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
            GetPriceChange = getPriceChange api
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
            GetPriceChange =
                impl.GetPriceChange
            Sell =
                fun asset quantity ->
                    async {
                        return Ok (
                            FilledOrderDetail.create
                                (Usd 100m)
                                quantity)
                    }
            Buy =
                fun asset spend ->
                    async { return Error "Dummy" }
        }
