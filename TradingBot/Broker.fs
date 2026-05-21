namespace TradingBot

open System
open System.Threading.Tasks

type Broker = {
    GetCash      : unit -> Task<Usd>
    GetPositions : unit -> Task<Map<Asset, Qty>>
    Quote        : Asset -> Task<Usd>
    PlaceMarket  : Asset -> TradeAction -> Usd -> Task<Result<Fill, string>>
}

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
                | [ s ] -> return s.PriceUsd
                | _     -> return failwithf "No price snapshot returned for %s" (Asset.value asset)
            }

        {
            GetCash = fun () ->
                task {
                    let p = db.GetPortfolio ()
                    return p.CashUsd
                }

            GetPositions = fun () ->
                task {
                    let p = db.GetPortfolio ()
                    return p.Positions |> Map.map (fun _ pos -> pos.Qty)
                }

            Quote = quoteAsset

            PlaceMarket = fun asset side sizeUsd ->
                task {
                    match side with
                    | Hold -> return Error "Cannot place a Hold order"
                    | _ ->
                        let! midPrice = quoteAsset asset
                        let mid = Usd.value midPrice
                        let directionMul =
                            match side with
                            | Buy  -> 1m
                            | Sell -> -1m
                            | Hold -> 0m
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
                                        else
                                            let oldBasis = oldQty * Usd.value currentPos.AvgCostUsd
                                            (oldBasis + usdSize) / newQty
                                    let newPos =
                                        { Asset = asset; Qty = Qty newQty; AvgCostUsd = Usd newAvgCost }
                                    let newCash = Usd (Usd.value portfolio.CashUsd - totalOut)
                                    let newPositions = portfolio.Positions |> Map.add asset newPos
                                    Ok (newCash, newPositions)

                            | Sell ->
                                if qtyValue > Qty.value currentPos.Qty then
                                    Error (sprintf "Insufficient %s: need %.8f, have %.8f"
                                                    (Asset.value asset)
                                                    qtyValue
                                                    (Qty.value currentPos.Qty))
                                else
                                    let newQty = Qty.value currentPos.Qty - qtyValue
                                    let newPositions =
                                        if newQty <= 0m then
                                            portfolio.Positions |> Map.remove asset
                                        else
                                            portfolio.Positions
                                            |> Map.add asset { currentPos with Qty = Qty newQty }
                                    let newCash = Usd (Usd.value portfolio.CashUsd + usdSize - feeUsd)
                                    Ok (newCash, newPositions)

                            | Hold -> Error "unreachable"

                        match result with
                        | Error e -> return Error e
                        | Ok (newCash, newPositions) ->
                            db.SetCashAndPositions newCash newPositions
                            db.RecordTrade {
                                Asset         = asset
                                Side          = side
                                Qty           = Qty qtyValue
                                PriceUsd      = Usd execPrice
                                FeeUsd        = Usd feeUsd
                                At            = now
                                BrokerOrderId = None
                            }
                            return Ok {
                                Asset  = asset
                                Side   = side
                                Qty    = Qty qtyValue
                                Price  = Usd execPrice
                                FeeUsd = Usd feeUsd
                                At     = now
                            }
                }
        }
