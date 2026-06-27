namespace StockTradingBot

/// Asset trend.
type Trend =
    | Positive = 0   // must be a .NET enum for serialization
    | Negative = 1

/// Asset assessment.
type AssetAssessment =
    {
        /// Asset in question.
        Asset : Asset

        /// Likely asset trend.
        Trend : Trend

        /// Reason behind this assessment.
        Reason : string
    }

/// Assessment of the market.
type MarketAssessment =
    {
        /// Overall market state.
        State : string

        /// Asset assessments.
        AssetAssessments : AssetAssessment[]
    }

/// Results possible when assessing the market.
type MarketAssessmentResult =

    /// Agent succeeded.
    | Success of NewsItem[] * MarketAssessment

    /// News feed errors occurred prior to agent request.
    | FeedErrors of NewsFeedError[]

    /// Agent request failed.
    | AgentError of string (*error message*)

module MarketAssessment =

    /// Creates a market assessment.
    let create state assessments =
        {
            State = state
            AssetAssessments = assessments
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
            "As a savvy stock trader, scan the news items below for \
            robust trends that are likely to persist over a period of \
            hours or days. Assess the overall state of the market and \
            then identify the specific US companies that are likely to \
            trend positive or negative in the market and explain why. \
            Return ONLY ticker symbols (not company names) for liquid \
            US equities."
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

    /// Tries to find an error in the given assessment.
    let tryFindError assessment =

            // check for duplicate asset assessments
        let nTotal =
            assessment.AssetAssessments.Length
        let nDistinct =
            assessment.AssetAssessments
                |> Array.distinctBy _.Asset
                |> Array.length
        assert(nDistinct <= nTotal)
        if nDistinct < nTotal then Some "Duplicate asset assessments"
        else None

    /// Assesses market from the given news items.
    let private getAssessment agent utcNow (itemArrays : NewsItem[][]) =
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
                Agent.getResultAsync<MarketAssessment> prompt agent

                // process result
            match result with
                | Ok assessment ->
                    match tryFindError assessment with
                        | Some message ->
                            return AgentError message
                        | None ->
                            return Success (items, assessment)
                | Error message ->
                    return AgentError message
        }

    /// Assesses the market:
    ///    1. Fetches general news items from feeds.
    ///    2. Asks agent to identify overall market trend and
    ///       specific assets from those news items.
    let getAsync httpClient agent =
        async {
                // get news items
            let utcNow = DateTime.UtcNow
            let! itemArrays, errors =
                getNewsItems httpClient (getFeeds utcNow)

                // get assessment?
            if errors.Length > 0 then   // to-do: carry on (with limited information) if any of the feeds fail?
                return FeedErrors errors
            else
                return! getAssessment agent utcNow itemArrays
        }

#endif
