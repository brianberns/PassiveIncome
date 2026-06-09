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

        /// Current price of the asset in the portfolio.
        CurrentPrice : Money
    }

    /// Value of the asset at its current price.
    member this.Value =
        this.Quantity * this.CurrentPrice

module AssetValue =

    /// Creates an asset value.
    let create quantity currentPrice =
        {
            Quantity = quantity
            CurrentPrice = currentPrice
        }

/// An investment portfolio.
type Portfolio =
    {
        /// Tradable cash in the portfolio.
        TradableCash : Money

        /// Assets in the portfolio.
        PositionMap : Map<Asset, AssetValue>
    }

    /// Total value of this portfolio: assets + cash.
    member portfolio.TotalValue =
        (portfolio.PositionMap.Values
            |> Seq.sumBy _.Value)
            + portfolio.TradableCash

module Portfolio =

    /// Creates a portfolio.
    let create tradableCash positionMap =
        {
            TradableCash = tradableCash
            PositionMap = positionMap
        }
