namespace StockTradingBot

open System
open System.Net.Http
open System.Reflection

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

module Program =

    let run () =

            // create HTTP client
        use httpClient =
            let client = new HttpClient()
            client.DefaultRequestHeaders
                .UserAgent
                .ParseAdd("StockTradingBot/0.1 (mailto:brianberns@gmail.com)")   // needed to avoid 429 errors from Yahoo
            client

            // fetch news items
        let items, errors =
            NewsFeed.feeds
                |> Seq.map (NewsFeed.getItems httpClient)
                |> Async.Parallel
                |> Async.RunSynchronously
                |> Array.partitionWith (function
                    | Ok items -> Choice1Of2 items
                    | Error error -> Choice2Of2 error)

            // log errors
        for feed, ex in errors do
            printfn $"Error in {feed.Name} news feed: {ex.Message}"

        let items =
            let now = DateTime.UtcNow
            let oneDay = TimeSpan.FromDays(1)
            items
                |> Seq.concat
                |> Seq.distinctBy _.Id
                |> Seq.where (fun item ->
                    now - item.PublishDate.UtcDateTime < oneDay)
                |> Seq.sortByDescending _.PublishDate

            // create agent
        use agent =
            let config =
                let assembly = Assembly.GetExecutingAssembly()
                ConfigurationBuilder()
                    .AddUserSecrets(assembly)
                    .Build()
            Agent.create config

        task {
            let prompt =
                String.concat "\n" [
                    "As a stock trader, scan the news items below for timely ideas. \
                    Identify a) the broad market/sector trend they collectively suggest, \
                    and b) the specific US stock symbols that are most directly affected \
                    and worth a closer look. Return ONLY ticker symbols (not company names) \
                    for liquid US equities."
                    for item in items do
                        ""
                        $"Title: {item.Title.Text}"
                        $"Summary: {item.Summary.Text}"
                        let age = DateTime.UtcNow - item.PublishDate.UtcDateTime
                        let hours = Math.Round(age.TotalHours, 1).ToString("F1")
                        $"Publication age: {hours} hours"
                ]
            let! overview =
                Agent.getResultAsync<MarketOverview> agent prompt
            printfn $"Trend: {overview.Trend}"
            for candidate in overview.Candidates do
                printfn $"{candidate.Symbol}: {candidate.Reason}"
        }
            |> Async.AwaitTask
            |> Async.RunSynchronously

    run ()
