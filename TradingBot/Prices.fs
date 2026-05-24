namespace TradingBot

open System
open System.Collections.Generic
open System.Threading.Tasks
open Alpaca.Markets

type Prices = {
    Fetch : Asset list -> Task<PriceSnapshot list>
}

module Prices =

    /// Reads daily bars from Alpaca's market-data API. The current price is the
    /// most recent daily bar's close (today's in-progress bar during RTH);
    /// Change24hPct is vs. the prior session close, Change7dPct vs. ~5 sessions
    /// back. For equities these are session-relative, not literal 24h/7d.
    let create (dataClient : IAlpacaDataClient) : Prices =
        {
            Fetch = fun assets ->
                task {
                    let now    = DateTime.UtcNow
                    let lookback = now.AddDays(-12.0)   // ~8 trading sessions of headroom
                    let results = List<PriceSnapshot>()
                    for asset in assets do
                        let symbol = Asset.value asset
                        try
                            let req = HistoricalBarsRequest(symbol, lookback, now, BarTimeFrame.Day)
                            req.Feed <- Nullable MarketDataFeed.Iex
                            let! page = dataClient.ListHistoricalBarsAsync(req)
                            let bars = page.Items |> Seq.toArray
                            if bars.Length > 0 then
                                let price      = bars.[bars.Length - 1].Close
                                let priorClose =
                                    if bars.Length >= 2 then bars.[bars.Length - 2].Close else price
                                let close5     =
                                    if bars.Length >= 6 then bars.[bars.Length - 6].Close else priorClose
                                let pct (cur : decimal) (prev : decimal) =
                                    if prev = 0m then 0.0 else float ((cur - prev) / prev) * 100.0
                                results.Add
                                    { Asset        = asset
                                      PriceUsd     = Usd price
                                      Change24hPct = pct price priorClose
                                      Change7dPct  = pct price close5
                                      At           = DateTimeOffset.UtcNow }
                        with _ ->
                            ()   // skip a symbol we can't price this cycle
                    return List.ofSeq results
                }
        }
