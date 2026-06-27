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
        let signStr = if usd < 0m then "-" else ""
        $"{signStr}$%.2f{abs usd}"

    /// Display string.
    override money.ToString() =
        money.String
