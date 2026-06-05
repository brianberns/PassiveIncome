namespace StockTradingBot

open System
open System.ServiceModel.Syndication

type AssetAction = Buy | Sell | Hold

type AssetInvestigation =
    {
        Asset : Asset
        Action : AssetAction
        Reason : string
    }

module AssetInvestigation =

    let create asset action reason =
        {
            Asset = asset
            Action = action
            Reason = reason
        }

    let private getFeed asset =
        NewsFeed.create
            $"Yahoo {asset.Symbol}"
            $"https://feeds.finance.yahoo.com/rss/2.0/headline?s={asset.Symbol}&region=US&lang=en-US"
            [ NewsItemFilter.hasSummary ]

    let private getPrompt utcNow marketTrend candidateNews =
        String.concat "\n" [
            "As a savvy stock trader, decide whether to Buy, Sell, or Hold \
            each stock symbol listed below based on the its news items and \
            the current overall market trend."
            ""
            $"Trend: %s{marketTrend}"
            for (candidate : Candidate, items) in candidateNews do
                ""
                "###################"
                ""
                $"Asset: {candidate.Asset.Symbol}"
                $"Hunch: {candidate.Reason}"
                for (item : SyndicationItem) in items do
                    ""
                    $"Title: {item.Title.Text}"
                    $"Summary: {item.Summary.Text}"
                    let hours =
                        let age = utcNow - item.PublishDate.UtcDateTime
                        Math.Round(age.TotalHours, 1)
                    $"Publication age: %.1f{hours} hours"
        ]

    let private getCandidateResult httpClient (candidate : Candidate) =
        async {
            let! result =
                getFeed candidate.Asset
                    |> NewsFeed.getItemsAsync httpClient
            return candidate, result
        }

    type AssetInvestigationDto =
        {
            Symbol : string
            Action : AssetAction
            Reason : string
        }

    let ofDto dto =
        create
            (Asset.create dto.Symbol)
            dto.Action
            dto.Reason

    let getAsync httpClient agent marketOverview =
        async {
                // fetch news items
            let! candResults =
                marketOverview.Candidates
                    |> Seq.map (getCandidateResult httpClient)
                    |> Async.Parallel

                // handle errors
            let candItemArrays, candErrors =
                candResults
                    |> Array.partitionWith (fun (cand, result) ->
                        match result with
                            | Ok items -> Choice1Of2 (cand, items)
                            | Error error -> Choice2Of2 (cand, error))

            for cand, (feed, exn) in candErrors do
                printfn $"Asset investigation error: {cand.Asset.Symbol}: {exn.Message}"

            let utcNow = DateTime.UtcNow
            let prompt = getPrompt utcNow marketOverview.Trend candItemArrays
            match! Agent.getResultAsync<AssetInvestigationDto[]> prompt agent with
                | Ok dtos ->
                    return Ok (Array.map ofDto dtos)
                | Error exn ->
                    return Error exn
        }
