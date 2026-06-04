namespace StockTradingBot

open System
open System.Net.Http

open Microsoft.Extensions.Configuration

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
        for item in items do
            printfn ""
            printfn "----------"
            printfn $"{item.Title.Text}"
            printfn $"{item.Summary.Text}"
            printfn $"{item.SourceFeed.Title.Text}"
            printfn $"{DateTime.UtcNow - item.PublishDate.UtcDateTime}"

    let test () =
        let config =
            let assembly = typeof<NewsFeed>.Assembly
            ConfigurationBuilder()
                .AddUserSecrets(assembly, true)
                .Build()
        printfn "%s" config["Gemini:ApiKey"]

    // run ()
    test ()
