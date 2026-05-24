namespace TradingBot

open System
open System.Threading.Tasks
open Alpaca.Markets

/// The broker owns the source of truth for cash + positions. For the paper
/// simulator that's our SQLite; for Alpaca it's the Alpaca account.
type Broker = {
    GetPortfolio : unit -> Task<Portfolio>
    IsMarketOpen : unit -> Task<bool>
    PlaceMarket  : Asset -> TradeAction -> Usd -> Task<Result<Fill, string>>
}

/// Offline simulator: fills against whatever Prices returns, applying a
/// configurable fee/spread/slippage, and keeps cash + positions in SQLite.
/// Markets are always "open". Retained as a no-API fallback.
module PaperBroker =

    let create (db : Persistence) (prices : Prices) (risk : RiskSettings) : Broker =
        let toRate (bps : int) = decimal bps / 10000m
        let feeRate    = toRate risk.FeeBps
        let spreadRate = toRate risk.SpreadBps
        let slipRate   = toRate risk.SlippageBps

        let quoteAsset asset =
            task {
                let! snapshots = prices.Fetch [ asset ]
                match snapshots with
                | s :: _ -> return s.PriceUsd
                | []     -> return failwithf "No price snapshot returned for %s" (Asset.value asset)
            }

        {
            GetPortfolio = fun () -> task { return db.GetPortfolio () }

            IsMarketOpen = fun () -> Task.FromResult true

            PlaceMarket = fun asset side sizeUsd ->
                task {
                    match side with
                    | Hold -> return Error "Cannot place a Hold order"
                    | _ ->
                        let! midPrice = quoteAsset asset
                        let mid = Usd.value midPrice
                        let directionMul = match side with Buy -> 1m | Sell -> -1m | Hold -> 0m
                        let execPrice = mid * (1m + directionMul * (spreadRate + slipRate))
                        let usdSize  = Usd.value sizeUsd
                        let qtyValue = usdSize / execPrice
                        let feeUsd   = usdSize * feeRate
                        let now      = DateTimeOffset.UtcNow

                        let portfolio = db.GetPortfolio ()
                        let currentPos =
                            portfolio.Positions
                            |> Map.tryFind asset
                            |> Option.defaultValue { Asset = asset; Qty = Qty 0m; AvgCostUsd = Usd 0m }

                        let result =
                            match side with
                            | Buy ->
                                let totalOut = usdSize + feeUsd
                                if totalOut > Usd.value portfolio.CashUsd then
                                    Error (sprintf "Insufficient cash: need $%.2f, have $%.2f"
                                                    totalOut (Usd.value portfolio.CashUsd))
                                else
                                    let oldQty = Qty.value currentPos.Qty
                                    let newQty = oldQty + qtyValue
                                    let newAvgCost =
                                        if newQty = 0m then 0m
                                        else (oldQty * Usd.value currentPos.AvgCostUsd + usdSize) / newQty
                                    let newPos =
                                        { Asset = asset; Qty = Qty newQty; AvgCostUsd = Usd newAvgCost }
                                    let newCash = Usd (Usd.value portfolio.CashUsd - totalOut)
                                    Ok (newCash, portfolio.Positions |> Map.add asset newPos)
                            | Sell ->
                                if qtyValue > Qty.value currentPos.Qty then
                                    Error (sprintf "Insufficient %s: need %.8f, have %.8f"
                                                    (Asset.value asset) qtyValue (Qty.value currentPos.Qty))
                                else
                                    let newQty = Qty.value currentPos.Qty - qtyValue
                                    let newPositions =
                                        if newQty <= 0m then portfolio.Positions |> Map.remove asset
                                        else portfolio.Positions |> Map.add asset { currentPos with Qty = Qty newQty }
                                    Ok (Usd (Usd.value portfolio.CashUsd + usdSize - feeUsd), newPositions)
                            | Hold -> Error "unreachable"

                        match result with
                        | Error e -> return Error e
                        | Ok (newCash, newPositions) ->
                            db.SetCashAndPositions newCash newPositions
                            db.RecordTrade {
                                Asset = asset; Side = side; Qty = Qty qtyValue
                                PriceUsd = Usd execPrice; FeeUsd = Usd feeUsd
                                At = now; BrokerOrderId = None
                            }
                            return Ok {
                                Asset = asset; Side = side; Qty = Qty qtyValue
                                Price = Usd execPrice; FeeUsd = Usd feeUsd; At = now
                            }
                }
        }

/// Live/paper trading through Alpaca. The Alpaca account is the source of
/// truth for cash + positions; we still record fills to SQLite for analysis.
module AlpacaBroker =

    /// Poll an order to a terminal state. Notional market orders during RTH
    /// fill in well under a second; we give it a few seconds of headroom.
    let private awaitFill (client : IAlpacaTradingClient) (orderId : Guid) =
        task {
            let mutable attempts = 0
            let mutable terminal = None
            while terminal.IsNone && attempts < 25 do
                do! Task.Delay 200
                let! o = client.GetOrderAsync(orderId)
                match o.OrderStatus with
                | OrderStatus.Filled -> terminal <- Some (Ok o)
                | OrderStatus.Canceled
                | OrderStatus.Rejected
                | OrderStatus.Expired
                | OrderStatus.Stopped -> terminal <- Some (Error o)
                | _ -> ()
                attempts <- attempts + 1
            return terminal
        }

    let create (db : Persistence) (tradingClient : IAlpacaTradingClient) : Broker =
        {
            GetPortfolio = fun () ->
                task {
                    let! account   = tradingClient.GetAccountAsync()
                    let! positions = tradingClient.ListPositionsAsync()
                    let posMap =
                        positions
                        |> Seq.map (fun p ->
                            let a = Asset p.Symbol
                            a, { Asset = a; Qty = Qty p.Quantity; AvgCostUsd = Usd p.AverageEntryPrice })
                        |> Map.ofSeq
                    let cash = account.TradableCash
                    return { CashUsd = Usd cash; Positions = posMap; AsOf = DateTimeOffset.UtcNow }
                }

            IsMarketOpen = fun () ->
                task {
                    let! clock = tradingClient.GetClockAsync()
                    return clock.IsOpen
                }

            PlaceMarket = fun asset side sizeUsd ->
                task {
                    match side with
                    | Hold -> return Error "Cannot place a Hold order"
                    | _ ->
                        let symbol = Asset.value asset
                        let usd    = Usd.value sizeUsd
                        try
                            let order : OrderBase =
                                match side with
                                | Buy  -> MarketOrder.Buy(symbol, OrderQuantity.Notional usd)
                                | Sell -> MarketOrder.Sell(symbol, OrderQuantity.Notional usd)
                                | Hold -> failwith "unreachable"
                            let! posted = tradingClient.PostOrderAsync(order)
                            let! outcome = awaitFill tradingClient posted.OrderId
                            match outcome with
                            | None -> return Error "Order did not fill within timeout"
                            | Some (Error o) ->
                                return Error (sprintf "Order %A (%s)" o.OrderStatus symbol)
                            | Some (Ok o) ->
                                let qty   = o.FilledQuantity
                                let price = o.AverageFillPrice.GetValueOrDefault()
                                let now   = o.FilledAtUtc |> Option.ofNullable
                                            |> Option.map (fun d -> DateTimeOffset(d, TimeSpan.Zero))
                                            |> Option.defaultValue DateTimeOffset.UtcNow
                                db.RecordTrade {
                                    Asset = asset; Side = side; Qty = Qty qty
                                    PriceUsd = Usd price; FeeUsd = Usd 0m
                                    At = now; BrokerOrderId = Some (string o.OrderId)
                                }
                                return Ok {
                                    Asset = asset; Side = side; Qty = Qty qty
                                    Price = Usd price; FeeUsd = Usd 0m; At = now
                                }
                        with ex ->
                            return Error (sprintf "Alpaca order failed for %s: %s" symbol ex.Message)
                }
        }
