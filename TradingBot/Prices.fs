namespace TradingBot

open System
open System.Collections.Generic
open System.Threading.Tasks
open Alpaca.Markets

type Prices = {
    /// Batch price lookup (no liquidity context). Used by the paper sim + reports.
    Fetch : Asset list -> Task<PriceSnapshot list>
    /// Single-symbol lookup returning price snapshot + average daily dollar
    /// volume (ADDV) over the fetched sessions. None if the symbol can't be
    /// priced on the IEX feed. Used by the discovery cycle for the liquidity floor.
    FetchOne : Asset -> Task<(PriceSnapshot * decimal) option>
}

module Prices =

    /// Reads daily bars from Alpaca's market-data API (IEX feed). Current price is
    /// the most recent daily bar's close; Change24hPct is vs. the prior session
    /// close, Change7dPct vs. ~5 sessions back. For equities these are
    /// session-relative, not literal 24h/7d.
    let create (dataClient : IAlpacaDataClient) : Prices =

        let fetchBars (symbol : string) =
            task {
                let now      = DateTime.UtcNow
                let lookback = now.AddDays(-12.0)   // ~8 trading sessions of headroom
                let req = HistoricalBarsRequest(symbol, lookback, now, BarTimeFrame.Day)
                req.Feed <- Nullable MarketDataFeed.Iex
                let! page = dataClient.ListHistoricalBarsAsync(req)
                return page.Items |> Seq.toArray
            }

        let snapshotOf (asset : Asset) (bars : IBar array) : (PriceSnapshot * decimal) option =
            if bars.Length = 0 then None
            else
                let price      = bars.[bars.Length - 1].Close
                let priorClose = if bars.Length >= 2 then bars.[bars.Length - 2].Close else price
                let close5     = if bars.Length >= 6 then bars.[bars.Length - 6].Close else priorClose
                let pct (cur : decimal) (prev : decimal) =
                    if prev = 0m then 0.0 else float ((cur - prev) / prev) * 100.0
                // Average daily dollar volume across the fetched sessions.
                let addv =
                    if bars.Length = 0 then 0m
                    else (bars |> Array.sumBy (fun b -> b.Close * b.Volume)) / decimal bars.Length
                let snap =
                    { Asset        = asset
                      PriceUsd     = Usd price
                      Change24hPct = pct price priorClose
                      Change7dPct  = pct price close5
                      At           = DateTimeOffset.UtcNow }
                Some (snap, addv)

        {
            Fetch = fun assets ->
                task {
                    let results = List<PriceSnapshot>()
                    for asset in assets do
                        try
                            let! bars = fetchBars (Asset.value asset)
                            match snapshotOf asset bars with
                            | Some (snap, _) -> results.Add snap
                            | None -> ()
                        with _ -> ()   // skip a symbol we can't price this cycle
                    return List.ofSeq results
                }

            FetchOne = fun asset ->
                task {
                    try
                        let! bars = fetchBars (Asset.value asset)
                        return snapshotOf asset bars
                    with _ ->
                        return None
                }
        }
