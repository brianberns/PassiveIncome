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

        /// Current price of the asset in the portfolio.
        CurrentPrice : Money
    }

    /// Value of the asset at its current price.
    member this.Value =
        this.CurrentPrice * this.Quantity   // Money on the left so Fable dispatches to Money's (*) operator

    /// Net change in the value of this asset.
    member this.NetChange =
        (this.CurrentPrice - this.AverageEntryPrice) * this.Quantity

module AssetValue =

    /// Creates an asset value.
    let create quantity avgEntryPrice currentPrice =
        {
            Quantity = quantity
            AverageEntryPrice = avgEntryPrice
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
