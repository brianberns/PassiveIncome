namespace StockTradingBot

/// Information about a filled order.
type FilledOrderDetail =
    {
        /// Average fill price.
        AverageFillPrice : Money

        /// Filled quantity.
        FilledQuantity : decimal
    }

    /// Total price.
    member detail.TotalPrice =
        detail.AverageFillPrice * detail.FilledQuantity   // Money on the left so Fable dispatches to Money's (*) operator

module FilledOrderDetail =

    /// Creates a filled order detail.
    let create averageFillPrice filledQuantity =
        {
            AverageFillPrice = averageFillPrice
            FilledQuantity = filledQuantity
        }

/// Resullt from buying/selling an asset.
type TradeResult =
    Result<FilledOrderDetail, string (*message*)>

/// Broker for buying/selling assets.
type Broker =
    {
        /// Gets the current portfolio.
        GetPortfolio :
            unit -> Async<Result<Portfolio, string (*message*)>>

        /// Is the market currently open?
        IsMarketOpen :
            unit -> Async<Result<bool, string (*message*)>>

        /// Gets recent change in the given asset's value.
        GetTrend :
            Asset -> Async<
                Result<Option<decimal> (*change*),
                string (*message*)>>

        /// Sells the given quantity of the given asset.
        Sell :
            Asset -> decimal (*quantity*) -> Async<TradeResult>

        /// Buys the given asset with the given money.
        Buy :
            Asset -> Money (*total spend*) -> Async<TradeResult>
}
