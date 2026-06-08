namespace StockTradingBot

/// Result of placing an order.
type OrderResult =
    {
        /// Asset traded.
        Asset : Asset

        /// Result of trade.
        Result :
            Result<
                Money (*avg. fill price*)
                    * decimal (*filled quantity*),
                exn>
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
    }

module RunResult =

    /// Creates a run result.
    let create
        portfolioResultOpt
        marketOverviewResultOpt
        recommendationResultOpt
        sellResults
        buyResults =
        {
            PortfolioResultOpt = portfolioResultOpt
            MarketOverviewResultOpt = marketOverviewResultOpt
            RecommendationResultOpt = recommendationResultOpt
            SellResults = sellResults
            BuyResults = buyResults
        }

    /// Creates a run result.
    let createWithoutRecommendation
        portfolioResultOpt
        marketOverviewResultOpt =
        create
            portfolioResultOpt
            marketOverviewResultOpt
            None Array.empty Array.empty

    /// Creates a run result.
    let createWithoutOverview portfolioResultOpt =
        createWithoutRecommendation
            portfolioResultOpt
            None
