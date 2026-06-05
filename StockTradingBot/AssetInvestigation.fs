namespace StockTradingBot

open System
open System.ServiceModel.Syndication

module AssetInvestigation =

    let private getFeed (Symbol symbol) =
        NewsFeed.create
            $"Yahoo {symbol}"
            $"https://feeds.finance.yahoo.com/rss/2.0/headline?s={symbol}&region=US&lang=en-US"
            [ NewsItemFilter.hasSummary ]

    let private getPrompt utcNow assetNews =
        String.concat "\n" [
            "As a stock trader, decide whether to Buy, Sell, or Hold each \
            stock symbol listed below based on its news items."
            for (Symbol symbol, items) in assetNews do
                ""
                "###################"
                ""
                $"Asset: {symbol}"
                for (item : SyndicationItem) in items do
                    ""
                    $"Title: {item.Title.Text}"
                    $"Summary: {item.Summary.Text}"
                    let hours =
                        let age = utcNow - item.PublishDate.UtcDateTime
                        Math.Round(age.TotalHours, 1)
                    $"Publication age: %.1f{hours} hours"
        ]

    let private getAssetResult httpClient asset =
        async {
            let! result =
                getFeed asset
                    |> NewsFeed.getItemsAsync httpClient
            return asset, result
        }

    let getAsync httpClient agent assets =
        async {
                // fetch news items
            let! assetResults =
                assets
                    |> Seq.map (getAssetResult httpClient)
                    |> Async.Parallel

                // handle errors
            let assetItemArrays, assetErrors =
                assetResults
                    |> Array.partitionWith (fun (asset, result) ->
                        match result with
                            | Ok items -> Choice1Of2 (asset, items)
                            | Error error -> Choice2Of2 (asset, error))

            for (Symbol symbol), (feed, exn) in assetErrors do
                printfn $"{symbol}: {exn.Message}"

            let utcNow = DateTime.UtcNow
            let prompt = getPrompt utcNow assetItemArrays
            return Ok prompt
        }
