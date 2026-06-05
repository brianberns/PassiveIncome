namespace StockTradingBot

open System
open System.ServiceModel.Syndication

/// Actions we can take on an asset.
type AssetAction =
    | Buy = 0   // must be a .NET enum for serialization
    | Sell = 1
    | Hold = 2

/// Recommended action for an asset.
type AssetRecommendation =
    {
        /// Asset in question.
        Asset : Asset

        /// Recommended action.
        Action : AssetAction

        /// Reason for recommendation.
        Reason : string
    }

type AssetRecommendationResult =

    /// Agent succeeded, but some assets might have a problem.
    | Success of Result<AssetRecommendation, (Asset * exn)>[]

    /// Agent request failed.
    | AgentError of exn

module AssetRecommendation =

    /// Creates an asset recommendation.
    let create asset action reason =
        {
            Asset = asset
            Action = action
            Reason = reason
        }

    /// Gets a news feed specific to the given asset.
    let private getFeed asset =
        NewsFeed.create
            $"Yahoo {asset.Symbol}"
            $"https://feeds.finance.yahoo.com/rss/2.0/headline?s={asset.Symbol}&region=US&lang=en-US"
            [ NewsItemFilter.hasSummary ]

    /// Creates a prompt for the given candidate assets.
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

    /// Gets news items for the given candidate asset.
    let private getCandidateNewsItems httpClient (candidate : Candidate) =
        async {
            let! result =
                getFeed candidate.Asset
                    |> NewsFeed.getItemsAsync httpClient
            return candidate, result
        }

    /// Asset recommendation DTO.
    type (*private*) AssetRecommendationDto =
        {
            Symbol : string
            Action : AssetAction
            Reason : string
        }

    /// Creates an asset recommendation from the given DTO.
    let private ofDto dto =
        create
            (Asset.create dto.Symbol)
            dto.Action
            dto.Reason

    /// Determines asset recommendations from the given market
    /// overview.
    let getAsync httpClient agent marketOverview : Async<AssetRecommendationResult> =
        async {
                // fetch news items
            let! candResults =
                marketOverview.Candidates
                    |> Seq.map (getCandidateNewsItems httpClient)
                    |> Async.Parallel

                // handle errors
            let candItemArrays, candErrors =
                candResults
                    |> Array.partitionWith (fun (cand, result) ->
                        match result with
                            | Ok items -> Choice1Of2 (cand, items)
                            | Error error -> Choice2Of2 (cand, error))

            for cand, (feed, exn) in candErrors do
                printfn $"Asset recommendation error: {cand.Asset.Symbol}: {exn.Message}"

            let utcNow = DateTime.UtcNow
            let prompt = getPrompt utcNow marketOverview.Trend candItemArrays
            let! dtosResult =
                Agent.getResultAsync<AssetRecommendationDto[]> prompt agent
            match dtosResult with
                | Ok dtos ->
                    let recoMap =
                        dtos
                            |> Seq.map (fun dto ->
                                let reco = ofDto dto
                                reco.Asset, reco)
                            |> Map
                    return Success [|
                        for cand in marketOverview.Candidates do
                            match Map.tryFind cand.Asset recoMap with
                                | Some reco -> Ok reco
                                | None ->
                                    Error (cand.Asset, exn("Agent ignored"))
                    |]

                | Error exn ->
                    return AgentError exn
        }
