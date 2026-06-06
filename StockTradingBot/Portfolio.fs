namespace StockTradingBot

/// An investment asset, such as stock in a company.
[<StructuredFormatDisplay("{Symbol}")>]
type Asset =
    {
        /// Asset symbol. E.g. Apple = "AAPL".
        Symbol : string
    }

    /// Display string.
    override asset.ToString() =
        asset.Symbol

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
