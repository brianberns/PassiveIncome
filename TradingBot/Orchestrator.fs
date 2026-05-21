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

    let private serializeDecisions (decisions : Decision list) : string =
        let dtos =
            decisions
            |> List.map (fun d ->
                {| asset      = Asset.value d.Asset
                   action     = TradeAction.toString d.Action
                   sizeUsd    = Usd.value d.SizeUsd
                   confidence = d.Confidence
                   rationale  = d.Rationale |})
        JsonSerializer.Serialize(dtos, Json.options)

    let private serializeFills (fills : Fill list) : string =
        let dtos =
            fills
            |> List.map (fun f ->
                {| asset = Asset.value f.Asset
                   side  = TradeAction.toString f.Side
                   qty   = Qty.value f.Qty
                   price = Usd.value f.Price
                   fee   = Usd.value f.FeeUsd |})
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
                    let assets  = cfg.Assets |> List.map Asset

                    let! priceSnapshots =
                        task {
                            try
                                let! snaps = prices.Fetch assets
                                for s in snaps do db.RecordPrice s
                                return snaps
                            with ex ->
                                errors.Add(sprintf "Prices: %s" ex.Message)
                                return []
                        }

                    let! newsItems =
                        task {
                            try
                                let! items = news.Fetch assets
                                let fresh = items |> List.filter db.TryRecordNews
                                return fresh
                            with ex ->
                                errors.Add(sprintf "News: %s" ex.Message)
                                return []
                        }

                    if List.isEmpty priceSnapshots then
                        return {
                            Started   = started
                            Decisions = []
                            Orders    = []
                            Fills     = []
                            Errors    = "No prices available — skipping cycle" :: List.ofSeq errors
                        }
                    else
                        let portfolio    = db.GetPortfolio ()
                        let recentTrades = db.RecentTrades 24.0

                        let! agentResult = agent.Propose portfolio priceSnapshots newsItems recentTrades
                        match agentResult with
                        | Error e ->
                            errors.Add e
                            return {
                                Started = started; Decisions = []; Orders = []; Fills = []
                                Errors  = List.ofSeq errors
                            }
                        | Ok (decisions, rawJson) ->
                            let outcome =
                                Risk.validateAndSize
                                    cfg.Risk portfolio priceSnapshots
                                    db.LastTradeAt
                                    DateTimeOffset.UtcNow
                                    decisions

                            for r in outcome.Rejected do
                                logger.LogInformation(
                                    sprintf "Rejected %s %s: %s"
                                        (Asset.value r.Decision.Asset)
                                        (TradeAction.toString r.Decision.Action)
                                        r.Reason)

                            let fills = ResizeArray<Fill>()
                            for order in outcome.Orders do
                                let! result = broker.PlaceMarket order.Asset order.Side order.SizeUsd
                                match result with
                                | Ok fill ->
                                    fills.Add fill
                                    logger.LogInformation(
                                        sprintf "Filled %s %s qty=%.8f @ $%.4f (fee $%.4f)"
                                            (TradeAction.toString fill.Side)
                                            (Asset.value fill.Asset)
                                            (Qty.value fill.Qty)
                                            (Usd.value fill.Price)
                                            (Usd.value fill.FeeUsd))
                                | Error e ->
                                    errors.Add(sprintf "%s %s: %s"
                                        (TradeAction.toString order.Side)
                                        (Asset.value order.Asset) e)

                            let fillsList = List.ofSeq fills
                            db.RecordDecisionCycle
                                started rawJson
                                (serializeDecisions decisions)
                                (serializeFills fillsList)

                            return {
                                Started   = started
                                Decisions = decisions
                                Orders    = outcome.Orders
                                Fills     = fillsList
                                Errors    = List.ofSeq errors
                            }
                }
        }

    let runLoop
        (logger : ILogger)
        (orch : Orchestrator)
        (cycleInterval : TimeSpan)
        (cancel : CancellationToken)
        : Task =
        task {
            while not cancel.IsCancellationRequested do
                logger.LogInformation(sprintf "Starting cycle at %s" (DateTimeOffset.UtcNow.ToString("u")))
                try
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
