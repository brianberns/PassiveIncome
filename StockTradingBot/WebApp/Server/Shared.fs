namespace StockTradingBot

type IStockTradingBotApi =
    {
        GetResults : unit -> Async<RunResult[]>
    }
