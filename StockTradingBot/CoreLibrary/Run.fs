namespace StockTradingBot

open System

/// Result of a single run.
type RunResult =
    {
        /// Start of run.
        StartTime : DateTimeOffset

        /// Indicates whether the market is open.
        IsMarketOpen : bool

        /// Portfolio at start of run.
        PortfolioResult : Result<Portfolio, string (*message*)>

        /// Market assessment.
        MarketAssessmentResultOpt : Option<MarketAssessmentResult>

        /// Sell results.
        SellResults : OrderResult[]

        /// Buy results.
        BuyResults : OrderResult[]

        /// End of run.
        EndTime : DateTimeOffset
    }

module RunResult =

    /// Creates a run result.
    let create
        startTime
        isMarketOpen
        portfolioResult
        marketAssessmentResultOpt
        sellResults
        buyResults
        endTime =
        {
            StartTime = startTime
            IsMarketOpen = isMarketOpen
            PortfolioResult = portfolioResult
            MarketAssessmentResultOpt = marketAssessmentResultOpt
            SellResults = sellResults
            BuyResults = buyResults
            EndTime = endTime
        }

    /// Creates a run result.
    let createWithoutAssessment startTime isMarketOpen portfolioResult endTime =
        create
            startTime
            isMarketOpen
            portfolioResult
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
                    context.HttpClient context.Agent

                // prepare to create result
            let create sellResults buyResults =
                RunResult.create
                    startTime
                    true
                    (Ok portfolio)
                    (Some assessmentResult)
                    sellResults
                    buyResults
                    DateTimeOffset.Now

            match assessmentResult with

                    // place orders based on successful assessment
                | MarketAssessmentResult.Success (_, assessment) ->
                    let! sellResults, buyResults =
                        Order.placeOrders
                            context.Broker portfolio assessment
                    return create sellResults buyResults

                    // assessment failed
                | _ ->
                    return create Array.empty Array.empty
        }

    /// Runs once using the given context.
    let runOne context =
        async {
            let startTime = DateTimeOffset.Now

                // start by getting the current portfolio
            match! context.Broker.GetPortfolio () with
                | Ok portfolio ->

                        // then check if the market is open
                    match! context.Broker.IsMarketOpen () with

                            // perform market assessment
                        | Ok true ->
                            return! runAssessment
                                context startTime portfolio

                            // market is closed
                        | Ok false ->
                            return RunResult.createWithoutAssessment
                                startTime false (Ok portfolio) DateTimeOffset.Now

                            // couldn't determine whether market is open
                        | Error msg ->
                            return RunResult.createWithoutAssessment
                                startTime
                                false         // hack: claim market is closed
                                (Error msg)   // hack: swallow portfolio, report error instead
                                DateTimeOffset.Now

                    // couldn't get portfolio
                | Error msg ->
                    return RunResult.createWithoutAssessment
                        startTime
                        false   // assume market is closed
                        (Error msg)
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
