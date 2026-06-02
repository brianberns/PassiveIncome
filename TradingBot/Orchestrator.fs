namespace TradingBot

open System
open System.Collections.Generic
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging

type CycleSummary = {
    Started   : DateTimeOffset
    Decisions : Decision list
    Orders    : Order list
    Fills     : Fill list
    Errors    : string list
}

type Orchestrator = {
    RunCycle : unit -> Task<CycleSummary>
}

module Orchestrator =

    let private serializeDiscovery (marketTrend : string) (candidates : Candidate list) : string =
        JsonSerializer.Serialize(
            {| marketTrend = marketTrend
               candidates  = candidates |> List.map (fun c ->
                                {| ticker = Asset.value c.Ticker; reason = c.Reason |}) |},
            Json.options)

    let private serializeDecisions (decisions : Decision list) : string =
        let dtos =
            decisions
            |> List.map (fun d ->
                {| asset            = Asset.value d.Asset
                   action           = TradeAction.toString d.Action
                   sizeUsd          = Usd.value d.SizeUsd
                   confidence       = d.Confidence
                   manipulationRisk = ManipulationRisk.toString d.ManipulationRisk
                   rationale        = d.Rationale |})
        JsonSerializer.Serialize(dtos, Json.options)

    let private serializeFills (fills : Fill list) : string =
        let dtos =
            fills
            |> List.map (fun f ->
                {| asset = Asset.value f.Asset
                   side  = TradeAction.toString f.Side
                   qty   = Qty.value f.Qty
                   price = Usd.value f.Price
                   addv  = f.AddvUsd |})
        JsonSerializer.Serialize(dtos, Json.options)

    let create
        (logger : ILogger)
        (cfg : AppSettings)
        (db : Persistence)
        (prices : Prices)
        (news : News)
        (agent : Agent)
        (broker : Broker)
        : Orchestrator =
        {
            RunCycle = fun () ->
                task {
                    let started = DateTimeOffset.UtcNow
                    let errors  = ResizeArray<string>()

                    let! portfolio = broker.GetPortfolio ()

                    // --- Stage 1: discovery from general news ---
                    let! generalNews =
                        task {
                            try
                                let! items = news.FetchGeneral ()
                                items |> List.iter (db.TryRecordNews >> ignore)
                                return items
                            with ex -> errors.Add(sprintf "News(general): %s" ex.Message); return []
                        }

                    let! marketTrend, candidates =
                        task {
                            let! r = agent.IdentifyCandidates generalNews
                            match r with
                            | Ok ((trend, cands), _raw) -> return trend, cands
                            | Error e ->
                                // Discovery failure shouldn't strand holdings — continue with
                                // no trend / no new candidates so we can still evaluate (and
                                // potentially sell) what we already hold.
                                errors.Add e
                                return "", []
                        }

                    let cappedCandidates = candidates |> List.truncate cfg.Discovery.MaxCandidatesPerCycle
                    for c in cappedCandidates do
                        logger.LogInformation(sprintf "Candidate %s — %s" (Asset.value c.Ticker) c.Reason)

                    // --- Build evaluation set: new candidates ∪ current holdings ---
                    let heldAssets = portfolio.Positions |> Map.toList |> List.map fst
                    let candidateAssets = cappedCandidates |> List.map (fun c -> c.Ticker)
                    let evalSet = (candidateAssets @ heldAssets) |> List.distinct

                    // --- Stage 2: validate, price, liquidity-gate, and decide per asset ---
                    let decisions    = ResizeArray<Decision>()
                    let priceSnaps   = ResizeArray<PriceSnapshot>()
                    let addvByAsset  = Dictionary<Asset, decimal>()

                    for asset in evalSet do
                        let symbol = Asset.value asset
                        let isHeld = portfolio.Positions.ContainsKey asset
                        let! info  = broker.GetAssetInfo asset
                        match info with
                        | Some i when not (i.Tradable && i.Fractionable) ->
                            logger.LogInformation(sprintf "Skip %s: not tradable/fractionable" symbol)
                        | None ->
                            logger.LogInformation(sprintf "Skip %s: unknown to broker" symbol)
                        | Some _ ->
                            let! priced = prices.FetchOne asset
                            match priced with
                            | None ->
                                logger.LogInformation(sprintf "Skip %s: no price" symbol)
                            | Some (snap, addv) ->
                                let passesFloor =
                                    Usd.value snap.PriceUsd >= cfg.Discovery.MinSharePrice
                                    && addv >= cfg.Discovery.MinAvgDollarVolume
                                if (not isHeld) && (not passesFloor) then
                                    logger.LogInformation(
                                        sprintf "Skip %s: below liquidity floor (px $%.2f, ADDV $%.0f)"
                                            symbol (Usd.value snap.PriceUsd) addv)
                                else
                                    db.RecordPrice snap
                                    priceSnaps.Add snap
                                    addvByAsset.[asset] <- addv
                                    let! tickerNews =
                                        task {
                                            try
                                                let! items = news.FetchForTicker asset
                                                items |> List.iter (db.TryRecordNews >> ignore)
                                                return items
                                            with _ -> return []
                                        }
                                    let! r = agent.EvaluateAsset marketTrend portfolio snap tickerNews
                                    match r with
                                    | Ok (d, _raw) -> decisions.Add d
                                    | Error e -> errors.Add e

                    let decisionList   = List.ofSeq decisions
                    let priceSnapList  = List.ofSeq priceSnaps

                    // --- Risk overlay ---
                    let outcome =
                        Risk.validateAndSize
                            cfg.Risk portfolio priceSnapList
                            db.LastTradeAt DateTimeOffset.UtcNow decisionList

                    for r in outcome.Rejected do
                        logger.LogInformation(
                            sprintf "Rejected %s %s: %s"
                                (Asset.value r.Decision.Asset)
                                (TradeAction.toString r.Decision.Action) r.Reason)

                    // --- Execute ---
                    let fills = ResizeArray<Fill>()
                    for order in outcome.Orders do
                        let addv =
                            match addvByAsset.TryGetValue order.Asset with
                            | true, v -> Some v
                            | _ -> None
                        let! result = broker.PlaceMarket order.Asset order.Side order.SizeUsd addv
                        match result with
                        | Ok fill ->
                            fills.Add fill
                            logger.LogInformation(
                                sprintf "Filled %s %s qty=%.6f @ $%.4f"
                                    (TradeAction.toString fill.Side) (Asset.value fill.Asset)
                                    (Qty.value fill.Qty) (Usd.value fill.Price))
                        | Error e ->
                            errors.Add(sprintf "%s %s: %s"
                                (TradeAction.toString order.Side) (Asset.value order.Asset) e)

                    let fillsList = List.ofSeq fills
                    db.RecordDecisionCycle
                        started
                        (serializeDiscovery marketTrend cappedCandidates)
                        (serializeDecisions decisionList)
                        (serializeFills fillsList)

                    return {
                        Started   = started
                        Decisions = decisionList
                        Orders    = outcome.Orders
                        Fills     = fillsList
                        Errors    = List.ofSeq errors
                    }
                }
        }

    let runLoop
        (logger : ILogger)
        (isMarketOpen : unit -> Task<bool>)
        (orch : Orchestrator)
        (cycleInterval : TimeSpan)
        (cancel : CancellationToken)
        : Task =
        task {
            while not cancel.IsCancellationRequested do
                try
                    let! marketOpen = isMarketOpen ()
                    if not marketOpen then
                        logger.LogInformation("Market closed — skipping cycle")
                    else
                        logger.LogInformation(sprintf "Starting cycle at %s" (DateTimeOffset.UtcNow.ToString("u")))
                        let! summary = orch.RunCycle ()
                        logger.LogInformation(
                            sprintf "Cycle done: %d decisions, %d orders, %d fills, %d errors"
                                (List.length summary.Decisions)
                                (List.length summary.Orders)
                                (List.length summary.Fills)
                                (List.length summary.Errors))
                        for e in summary.Errors do
                            logger.LogWarning(sprintf "Cycle error: %s" e)
                with ex ->
                    logger.LogError(ex, sprintf "Cycle threw: %s" ex.Message)

                if not cancel.IsCancellationRequested then
                    try
                        do! Task.Delay(cycleInterval, cancel)
                    with :? OperationCanceledException -> ()
        } :> Task
