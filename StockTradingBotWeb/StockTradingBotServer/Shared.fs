namespace StockTradingBot

type IStockTradingBotApi =
    {
        Ping : int -> Async<int>
    }
