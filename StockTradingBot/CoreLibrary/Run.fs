namespace StockTradingBot

open System

/// Result of a single run.
type RunResult =
    {
        /// Start of run.
        StartTime : DateTimeOffset
        
        /// Portfolio at start of run.
        PortfolioResultOpt : Option<Result<Portfolio, string>>

        /// Market assessment.
        MarketAssessmentResultOpt : Option<MarketAssessmentResult>

        /// Sell results.
        SellResults : OrderResult[]

        /// Buy results.
        BuyResults : OrderResult[]

        /// End of run.
        EndTime : DateTimeOffset
    }

    /// Indicates whether the market is open.
    member result.IsMarketOpen =
        assert(result.PortfolioResultOpt.IsSome
            || (result.MarketAssessmentResultOpt.IsNone
                && result.SellResults.Length = 0
                && result.BuyResults.Length = 0))
        result.PortfolioResultOpt.IsSome   // in lieu of an actual flag, this is good enough

module RunResult =

    /// Creates a run result.
    let create
        startTime
        portfolioResultOpt
        marketAssessmentResultOpt
        sellResults
        buyResults
        endTime =
        {
            StartTime = startTime
            PortfolioResultOpt = portfolioResultOpt
            MarketAssessmentResultOpt = marketAssessmentResultOpt
            SellResults = sellResults
            BuyResults = buyResults
            EndTime = endTime
        }

    /// Creates a run result.
    let createWithoutAssessment startTime portfolioResultOpt endTime =
        create
            startTime
            portfolioResultOpt
            None
            Array.empty
            Array.empty
            endTime

#if !FABLE_COMPILER

open System.Net.Http
open FSharp.Control

(*
 * Steps in a run:
 *    1. Check if market is open.
 *    2. Get current portfolio.
 *    3. Get market assessment.
 *    4. Place orders based on the assessment.
 *)

/// Context need to run.
type RunContext =
    {
        /// HTTP client for fetching news feeds.
        HttpClient : HttpClient

        /// Decision-making agent.
        Agent : Agent

        /// Broker for buying/selling assets.
        Broker : Broker
    }

module RunContext =

    /// Creates a run context.
    let create httpClient agent broker =
        {
            HttpClient = httpClient
            Agent = agent
            Broker = broker
        }

module Run =

    /// Requests and acts on a market assessment.
    let private runAssessment context startTime portfolio =
        async {
                // request market assessment from agent
            let! assessmentResult =
                MarketAssessment.getAsync
                    context.HttpClient
                    context.Agent

            match assessmentResult with
                | MarketAssessmentResult.Success (_, assessment) ->

                        // place orders based on assessment
                    let! sellResults, buyResults =
                        Order.placeOrders context.Broker portfolio assessment

                    return RunResult.create
                        startTime
                        (Some (Ok portfolio))
                        (Some assessmentResult)
                        sellResults
                        buyResults
                        DateTimeOffset.Now
                | _ ->
                    return RunResult.createWithoutAssessment
                        startTime
                        (Some (Ok portfolio))
                        DateTimeOffset.Now
        }

    /// Runs once using the given context.
    let runOne context =
        async {
            let startTime = DateTimeOffset.Now
            match! context.Broker.IsMarketOpen () with
                | Ok true ->
                    match! context.Broker.GetPortfolio () with
                        | Ok portfolio ->
                            return! runAssessment
                                context startTime portfolio
                        | Error exn ->
                            return RunResult.createWithoutAssessment
                                startTime
                                (Some (Error exn))
                                DateTimeOffset.Now
                | Ok false ->
                    return RunResult.createWithoutAssessment
                        startTime None DateTimeOffset.Now
                | Error exn ->
                    return RunResult.createWithoutAssessment
                        startTime
                        (Some (Error exn))
                        DateTimeOffset.Now
        }

    /// Runs in an infinite loop using the given context.
    let runLoop context delay =
        asyncSeq {
            while true do
                let! result = runOne context
                yield result
                do! Async.Sleep(delay : TimeSpan)
        }

#endif
