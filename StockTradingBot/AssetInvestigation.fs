namespace StockTradingBot

open System
open System.ServiceModel.Syndication

module AssetInvestigation =

    let private getFeed (Symbol symbol) =
        NewsFeed.create
            $"Yahoo {symbol}"
            $"https://feeds.finance.yahoo.com/rss/2.0/headline?s={symbol}&region=US&lang=en-US"
            [ NewsItemFilter.hasSummary ]

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

    let getAsync httpClient agent asset =
        async {
                // fetch news items
            let! result =
                getFeed asset
                    |> NewsFeed.getItemsAsync httpClient

            match result with
                | Ok items -> return Ok items
                | Error error -> return Error error
        }
