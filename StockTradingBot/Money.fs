namespace StockTradingBot

/// Money, cash, moola...
[<StructuredFormatDisplay("{String}")>]
type Money =

    /// U.S. dollars ($).
    | Usd of decimal

    /// Zero dollars.
    static member Zero = Usd 0m

    /// One dollar.
    static member One = Usd 1m

    /// Addition.
    static member (+)(Usd a, Usd b) =
        Usd (a + b)

    /// Subtraction.
    static member (-)(Usd a, Usd b) =
        Usd (a - b)

    /// Multiplication.
    static member (*)(n : decimal, Usd usd) =
        Usd (n * usd)

    /// Multiplication.
    static member (*)(Usd usd, n : decimal) =
        Usd (usd * n)

    /// Division.
    static member (/)(Usd usd, n : decimal) =
        Usd (usd / n)

    /// Display string.
    member money.String =
        let (Usd usd) = money
        $"$%.2f{usd}"

    /// Display string.
    override money.ToString() =
        money.String
