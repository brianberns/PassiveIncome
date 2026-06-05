namespace StockTradingBot

open System
open System.ServiceModel.Syndication

open Microsoft.Extensions.AI
open Microsoft.Extensions.Configuration

type Candidate =
    {
        Symbol : string
        Reason : string
    }

type MarketOverview =
    {
        Trend : string
        Candidates : Candidate[]
    }

type MarketOverviewResult =
    | Overview of MarketOverview
    | FeedErrors of (NewsFeed * exn)[]
    | ChatError of exn

type Agent =
    {
        GoogleClient : Google.GenAI.Client
        ChatClient : IChatClient
    }

    member this.Dispose() =
        this.ChatClient.Dispose()
        this.GoogleClient.Dispose()

    interface IDisposable with
        member this.Dispose() = this.Dispose()

module Agent =

    let private modelId = "gemini-2.5-flash"

    let create (config : IConfiguration) =
        let googleClient =
            new Google.GenAI.Client(
                apiKey = config["Gemini:ApiKey"])
        let chatClient =
            googleClient.AsIChatClient(modelId)
        {
            GoogleClient = googleClient
            ChatClient = chatClient
        }

    let private getResultAsync<'t> (prompt : string) agent =
        task {
            try
                let! response =
                    ChatClientStructuredOutputExtensions
                        .GetResponseAsync<'t>(
                            agent.ChatClient,
                            prompt)
                return Ok response.Result
            with exn ->
                return Error exn
        } |> Async.AwaitTask

    module private MarketOverview =

        let private isPersonal : NewsItemFilter =
            fun item ->
                item.Title.Text.Split([| ' '; ''' |])   // isolate "I" from "I'm"
                    |> Array.contains("I")

        let private feeds =
            [
                NewsFeed.create
                    "MarketWatch Top Stories"
                    "https://feeds.content.dowjones.io/public/rss/mw_topstories"
                    [
                        NewsItemFilter.hasSummary
                        (isPersonal >> not)   // filter out personal finance stories
                    ]
                NewsFeed.create
                    "CNBC Top News"
                    "https://search.cnbc.com/rs/search/combinedcms/view.xml?partnerId=wrss01&id=100003114"
                    [ NewsItemFilter.hasSummary ]
                NewsFeed.create
                    "CNBC Finance"
                    "https://search.cnbc.com/rs/search/combinedcms/view.xml?partnerId=wrss01&id=10000664"
                    [ NewsItemFilter.hasSummary ]
                NewsFeed.create
                    "Yahoo S&P 500"
                    "https://feeds.finance.yahoo.com/rss/2.0/headline?s=%5EGSPC&region=US&lang=en-US"   // ^GSPC = S&P 500
                    [ NewsItemFilter.hasSummary ]
            ]

        let private getPrompt utcNow items =
            String.concat "\n" [
                "As a stock trader, scan the news items below for timely ideas. \
                Identify a) the broad market/sector trend they collectively suggest, \
                and b) the specific US stock symbols that are most directly affected \
                and worth a closer look. Return ONLY ticker symbols (not company names) \
                for liquid US equities."
                for (item : SyndicationItem) in items do
                    ""
                    $"Title: {item.Title.Text}"
                    $"Summary: {item.Summary.Text}"
                    let hours =
                        let age = utcNow - item.PublishDate.UtcDateTime
                        Math.Round(age.TotalHours, 1)
                    $"Publication age: %.1f{hours} hours"
            ]

        let getAsync httpClient agent =
            async {
                    // fetch news items
                let! results =
                    feeds
                        |> Seq.map (NewsFeed.getItemsAsync httpClient)
                        |> Async.Parallel

                    // handle errors
                let items, errors =
                    results
                        |> Array.partitionWith (function
                            | Ok items -> Choice1Of2 items
                            | Error error -> Choice2Of2 error)
                if errors.Length > 0 then
                    return FeedErrors errors
                else
                        // get market overview
                    let prompt =
                        let utcNow = DateTime.UtcNow
                        let oneDay = TimeSpan.FromDays(1)
                        items
                            |> Seq.concat
                            |> Seq.distinctBy _.Id
                            |> Seq.where (fun item ->
                                utcNow - item.PublishDate.UtcDateTime < oneDay)
                            |> Seq.sortByDescending _.PublishDate
                            |> getPrompt utcNow
                    match! getResultAsync<MarketOverview> prompt agent with
                        | Ok overview -> return Overview overview
                        | Error exn ->  return ChatError exn
            }

    let getMarketOverviewAsync = MarketOverview.getAsync
