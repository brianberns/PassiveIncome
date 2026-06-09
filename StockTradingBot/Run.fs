namespace StockTradingBot

open System
open System.Net.Http

open FSharp.Control

(*
 * Steps in a run:
 *    1. Check if market is open.
 *    2. Get current portfolio.
 *    3. Get market overview.
 *    4. Create recommendations from market overview.
 *    5. Place trades based on those recommendations.
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

/// Result of placing an order.
type OrderResult =
    {
        /// Asset traded.
        Asset : Asset

        /// Result of trade.
        Result : Result<FilledOrderDetail, exn>
    }

module OrderResult =

    /// Creates an order result.
    let create asset result =
        {
            Asset = asset
            Result = result
        }

/// Result of a single run.
type RunResult =
    {
        /// Start of run.
        StartTime : DateTimeOffset
        
        /// Portfolio at start of run.
        PortfolioResultOpt : Option<Result<Portfolio, exn>>

        /// Market overview.
        MarketOverviewResultOpt : Option<MarketOverviewResult>

        /// Asset recommentation.
        RecommendationResultOpt : Option<AssetRecommendationResult>

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
        portfolioResultOpt
        marketOverviewResultOpt
        recommendationResultOpt
        sellResults
        buyResults
        endTime =
        {
            StartTime = startTime
            PortfolioResultOpt = portfolioResultOpt
            MarketOverviewResultOpt = marketOverviewResultOpt
            RecommendationResultOpt = recommendationResultOpt
            SellResults = sellResults
            BuyResults = buyResults
            EndTime = endTime
        }

    /// Creates a run result.
    let createWithoutRecommendation
        startTime
        portfolioResultOpt
        marketOverviewResultOpt
        endTime =
        create
            startTime
            portfolioResultOpt
            marketOverviewResultOpt
            None Array.empty Array.empty
            endTime

    /// Creates a run result.
    let createWithoutOverview startTime portfolioResultOpt endTime =
        createWithoutRecommendation
            startTime
            portfolioResultOpt
            None
            endTime

module Run =

    /// Gets trade recommendations based on the given market
    /// overview.
    let private getRecommendations context portfolio marketOverview =

        let candidates =
            let portfolioCandidates =
                portfolio.PositionMap.Keys
                    |> Seq.map (fun asset ->
                        Candidate.create asset "In portfolio")
            [
                yield! portfolioCandidates   // always consider selling assets in portfolio
                yield! marketOverview.Candidates
            ] |> Seq.distinctBy _.Asset      // elminate redundant candidates

        AssetRecommendation.getAsync
            context.HttpClient
            context.Agent
            marketOverview.Trend
            candidates

    /// Obtains quantity of each of the given assets in the
    /// given portfolio.
    let private getQuantities portfolio assets =
        Seq.choose (fun asset ->
            portfolio.PositionMap
                |> Map.tryFind asset
                |> Option.map (fun value ->
                    asset, value.Quantity))
            assets

    /// Sells the given asset quantities.
    let private sellAssetQuantities broker assetQuantities =
        assetQuantities
            |> Seq.map (fun (asset, quantity) ->
                async {
                    let! result =
                        broker.Sell asset quantity
                    return OrderResult.create asset result
                })
            |> Async.Sequential   // avoid hammering the broker API

    /// Slush to avoid spending more than we have.
    let private slush = Usd 1m

    /// Gets spendable cash from portfolio and sold assets.
    let private getSpendableCash portfolio sellResults =
        let totalSales =
            sellResults
                |> Seq.sumBy (fun (result : OrderResult) ->
                    match result.Result with
                        | Ok detail -> detail.TotalPrice
                        | Error _ -> Money.Zero)
        portfolio.TradableCash + totalSales - slush

    /// Buys the given assets using the given cash.
    let private buyAssets broker (assets : _[]) (cash : Money) =
        let portion = cash / decimal assets.Length   // amount to spend on each asset
        assets
            |> Seq.map (fun asset ->
                async {
                    let! result =
                        broker.Buy asset portion
                    return OrderResult.create asset result
                })
            |> Async.Sequential   // avoid hammering the broker API

    /// Places orders based on the given recommendations.
    let private placeOrders broker portfolio recommendations =
        async {
                // organize recommendations by action (buy, sell, hold)
            let lookup =
                let actionMap =
                    recommendations
                        |> Array.groupBy _.Action
                        |> Array.map (fun (action, group) ->
                            action, group |> Array.map _.Asset)
                        |> Map
                fun action ->
                    actionMap
                        |> Map.tryFind action
                        |> Option.defaultValue Array.empty
            let buys = lookup AssetAction.Buy
            let sells = lookup AssetAction.Sell
            let holds = lookup AssetAction.Hold

                // sell in-portfolio holds when we want to buy
            let sells =
                if buys.Length > 0 then
                    holds
                        |> Array.where
                            portfolio.PositionMap.ContainsKey
                        |> Array.append sells
                else sells

                // sell first to generate cash
            let! sellResults =
                sells
                    |> getQuantities portfolio   // sell all owned shares
                    |> sellAssetQuantities broker
            let cash = getSpendableCash portfolio sellResults

                // buy assets with cash on hand
            if buys.Length > 0 && cash > slush then   // don't try to spend a trivial amount
                let! buyResults = buyAssets broker buys cash
                return sellResults, buyResults
            else
                return sellResults, Array.empty
        }

    /// Acts on recommendations generated from the given market
    /// overview.
    let private runRecommendations
        context portfolio marketOverview =
        async {
                // get asset recommendations for all candidates
            let! recoResult =
                getRecommendations context portfolio marketOverview

                // make trades based on recommendations
            match recoResult with
                | Success recoDetailResults ->
                    let! sellResults, buyResults =
                        recoDetailResults
                            |> Array.choose (function
                                | Ok reco -> Some reco
                                | _ -> None)
                            |> placeOrders context.Broker portfolio
                    return recoResult, sellResults, buyResults
                | AgentError _ ->
                    return recoResult, Array.empty, Array.empty
        }

    /// Requests and acts on a market overview.
    let private runOverview context startTime portfolio =
        async {
                // request market overview from agent
            let! overviewResult =
                MarketOverview.getAsync
                    context.HttpClient
                    context.Agent

            match overviewResult with
                | MarketOverviewResult.Success overview ->

                        // request and act on recommendations
                    let! recoResult, sellResults, buyResults =
                        runRecommendations context portfolio overview

                    return RunResult.create
                        startTime
                        (Some (Ok portfolio))
                        (Some overviewResult)
                        (Some recoResult)
                        sellResults
                        buyResults
                        DateTimeOffset.Now
                | _ ->
                    return RunResult.createWithoutRecommendation
                        startTime
                        (Some (Ok portfolio))
                        (Some overviewResult)
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
                            return! runOverview
                                context startTime portfolio
                        | Error exn ->
                            return RunResult.createWithoutOverview
                                startTime
                                (Some (Error exn))
                                DateTimeOffset.Now
                | Ok false ->
                    return RunResult.createWithoutOverview
                        startTime None DateTimeOffset.Now
                | Error exn ->
                    return RunResult.createWithoutOverview
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
