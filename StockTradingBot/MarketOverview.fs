namespace StockTradingBot

/// Asset that we might be interested in.
[<NoComparison>]
type Candidate =
    {
        /// Candidate asset.
        Asset : Asset

        /// Reason for interest.
        Reason : string
    }

module Candidate =

    /// Creates a candidate.
    let create asset reason =
        {
            Asset = asset
            Reason = reason
        }

/// Overview of the market.
type MarketOverview =
    {
        /// Overall market trend.
        Trend : string

        /// Candidate assets for trading.
        Candidates : Candidate[]
    }

/// Results possible when determining market overview.
type MarketOverviewResult =

    /// Agent succeeded.
    | Success of NewsItem[] * MarketOverview

    /// News feed errors occurred prior to agent request.
    | FeedErrors of NewsFeedError[]

    /// Agent request failed.
    | AgentError of string (*error message*)

module MarketOverview =

    /// Creates a market overview.
    let create trend candidates =
        {
            Trend = trend
            Candidates = candidates
        }

#if !FABLE_COMPILER

    open System

    /// The given item relates to personal finance, rather than
    /// investing.
    let private isPersonal : NewsItemFilter =
        fun item ->
            item.Title.Text.Split([| ' '; ''' |])   // isolate "I" from "I'm"
                |> Array.contains("I")

    /// General market news feeds.
    let private getFeeds utcNow =
        [
            NewsFeed.create
                "MarketWatch Top Stories"
                "https://feeds.content.dowjones.io/public/rss/mw_topstories"
                [
                    NewsItemFilter.hasSummary
                    NewsItemFilter.isRecent utcNow
                    (isPersonal >> not)   // filter out personal finance stories
                ]
            NewsFeed.create
                "CNBC Top News"
                "https://search.cnbc.com/rs/search/combinedcms/view.xml?partnerId=wrss01&id=100003114"
                [
                    NewsItemFilter.hasSummary
                    NewsItemFilter.isRecent utcNow
                ]
            NewsFeed.create
                "CNBC Finance"
                "https://search.cnbc.com/rs/search/combinedcms/view.xml?partnerId=wrss01&id=10000664"
                [
                    NewsItemFilter.hasSummary
                    NewsItemFilter.isRecent utcNow
                ]
            NewsFeed.create
                "Yahoo S&P 500"
                "https://feeds.finance.yahoo.com/rss/2.0/headline?s=%5EGSPC&region=US&lang=en-US"   // ^GSPC = S&P 500
                [
                    NewsItemFilter.hasSummary
                    NewsItemFilter.isRecent utcNow
                ]
        ]

    /// Creates a prompt for the given news items.
    let private getPrompt utcNow newsItems =
        String.concat "\n" [
            "As a savvy stock trader, scan the news items below for relevant, \
            timely information. Identify a) the broad market/sector trend these \
            items collectively suggest, and b) the specific US stock symbols \
            that are most affected by the news. Return ONLY ticker symbols (not \
            company names) for liquid US equities."
            for item in newsItems do
                ""
                $"Title: {item.Title}"
                $"Summary: {item.Summary}"
                let hours =
                    let age = utcNow - item.PublishDate.UtcDateTime
                    Math.Round(age.TotalHours, 1)
                $"Publication age: %.1f{hours} hours"
        ]

    /// Gets items from the given news feeds.
    let private getNewsItems httpClient feeds =
        async {
                // fetch news items from feeds
            let! results =
                feeds
                    |> Seq.map (NewsFeed.getItemsAsync httpClient)
                    |> Async.Parallel

                // handle errors
            return results
                |> Array.partitionWith (function
                    | Ok items -> Choice1Of2 items
                    | Error error -> Choice2Of2 error)
        }

    /// Determines market overview from the given news items.
    let private getOverview agent utcNow (itemArrays : NewsItem[][]) =
        async {
                // gather news items
            let items =
                itemArrays
                    |> Seq.concat
                    |> Seq.distinctBy _.Id
                    |> Seq.sortByDescending _.PublishDate
                    |> Seq.toArray

                // query agent
            let! result =
                let prompt = getPrompt utcNow items
                Agent.getResultAsync<MarketOverview> prompt agent

                // process result
            match result with
                | Ok overview ->
                    return Success (items, overview)
                | Error message ->
                    return AgentError message
        }

    /// Determines the current market overview:
    ///    1. Fetches general news items from feeds.
    ///    2. Asks agent to identify overall market trend and
    ///       candidate assets from those news items.
    let getAsync httpClient agent =
        async {
                // get news items
            let utcNow = DateTime.UtcNow
            let! itemArrays, errors =
                getNewsItems httpClient (getFeeds utcNow)

                // get overview?
            if errors.Length > 0 then   // to-do: carry on (with limited information) if any of the feeds fail?
                return FeedErrors errors
            else
                return! getOverview agent utcNow itemArrays
        }

#endif
