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
        detail.FilledQuantity * detail.AverageFillPrice

module FilledOrderDetail =

    /// Creates a filled order detail.
    let create averageFillPrice filledQuantity =
        {
            AverageFillPrice = averageFillPrice
            FilledQuantity = filledQuantity
        }

/// Broker for buying/selling assets.
type Broker =
    {
        /// Gets the current portfolio.
        GetPortfolio : unit -> Async<Result<Portfolio, string (*message*)>>

        /// Is the market currently open?
        IsMarketOpen : unit -> Async<Result<bool, string (*message*)>>

        /// Sells the given quantity of the given asset.
        Sell :
            Asset -> decimal (*quantity*)
                -> Async<Result<FilledOrderDetail, string (*message*)>>

        /// Buys the given asset with the given money.
        Buy :
            Asset -> Money (*total spend*)
                -> Async<Result<FilledOrderDetail, string (*message*)>>
    }
