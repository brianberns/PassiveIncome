namespace TradingBot

open System

[<Struct>]
type Asset = Asset of string

[<Struct>]
type Usd = Usd of decimal

[<Struct>]
type Qty = Qty of decimal

type TradeAction =
    | Buy
    | Sell
    | Hold

module Asset =
    let value (Asset s) = s

module Usd =
    let value (Usd d) = d
    let zero = Usd 0m
    let add (Usd a) (Usd b) = Usd (a + b)
    let sub (Usd a) (Usd b) = Usd (a - b)
    let mul (Usd a) (k : decimal) = Usd (a * k)

module Qty =
    let value (Qty d) = d
    let zero = Qty 0m
    let add (Qty a) (Qty b) = Qty (a + b)
    let sub (Qty a) (Qty b) = Qty (a - b)

module TradeAction =
    let parse (s : string) =
        match s.Trim() with
        | "Buy"  -> Some Buy
        | "Sell" -> Some Sell
        | "Hold" -> Some Hold
        | _      -> None
    let toString = function
        | Buy -> "Buy" | Sell -> "Sell" | Hold -> "Hold"

type NewsItem = {
    Id             : string
    Source         : string
    Title          : string
    Url            : string
    At             : DateTimeOffset
    VotesPositive  : int
    VotesNegative  : int
    VotesImportant : int
}

type PriceSnapshot = {
    Asset        : Asset
    PriceUsd     : Usd
    Change24hPct : float
    Change7dPct  : float
    At           : DateTimeOffset
}

type Position = {
    Asset      : Asset
    Qty        : Qty
    AvgCostUsd : Usd
}

type Portfolio = {
    CashUsd   : Usd
    Positions : Map<Asset, Position>
    AsOf      : DateTimeOffset
}

type Decision = {
    Asset      : Asset
    Action     : TradeAction
    SizeUsd    : Usd
    Confidence : float
    Rationale  : string
}

type Order = {
    Asset          : Asset
    Side           : TradeAction  // Buy or Sell only (Hold filtered out)
    SizeUsd        : Usd
    SourceDecision : Decision
}

type Fill = {
    Asset  : Asset
    Side   : TradeAction
    Qty    : Qty
    Price  : Usd                  // execution price after slippage
    FeeUsd : Usd
    At     : DateTimeOffset
}

type Trade = {
    Asset         : Asset
    Side          : TradeAction
    Qty           : Qty
    PriceUsd      : Usd
    FeeUsd        : Usd
    At            : DateTimeOffset
    BrokerOrderId : string option
}
