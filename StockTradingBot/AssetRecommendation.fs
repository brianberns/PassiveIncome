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

/// Recommendation result for a collection of assets.
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
    let private getFeed utcNow asset =
        NewsFeed.create
            $"Yahoo {asset.Symbol}"
            $"https://feeds.finance.yahoo.com/rss/2.0/headline?s={asset.Symbol}&region=US&lang=en-US"
            [
                NewsItemFilter.hasSummary
                NewsItemFilter.isRecent utcNow
            ]

    /// Creates a prompt for the given candidate assets.
    let private getPrompt utcNow marketTrend candidateNews =
        String.concat "\n" [
            "As a savvy stock trader, decide whether to Buy, Sell, or Hold \
            each stock symbol listed below based on the its news items and \
            the current overall market trend."
            ""
            $"Trend: %s{marketTrend}"
            for (candidate : Candidate, items : seq<_>) in candidateNews do
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
    let private getCandidateNewsItems
        httpClient utcNow (candidate : Candidate) =
        async {
            let! result =
                getFeed utcNow candidate.Asset
                    |> NewsFeed.getItemsAsync httpClient
            return candidate, result
        }

    /// Gets news items for the given candidate assets.
    let private getNewsItems httpClient utcNow candidates =
        async {
            let! results =
                candidates
                    |> Seq.map (
                        getCandidateNewsItems httpClient utcNow)
                    |> Async.Parallel

                // handle errors
            return results
                |> Array.partitionWith (fun (cand, result) ->
                    match result with
                        | Ok items -> Choice1Of2 (cand, items)
                        | Error error -> Choice2Of2 (cand, error))
        }

    /// Asset recommendation DTO.
    type (*private*) AssetRecommendationDto =
        {
            Symbol : string
            Action : AssetAction
            Reason : string
        }

    /// Determines recommendations for the given candidates.
    let getRecommendationDtos agent utcNow marketTrend candItemArrays =
        async {
            let prompt =
                candItemArrays
                    |> Seq.map (
                        fun (cand : Candidate, items : SyndicationItem[]) ->
                            cand,
                            items |> Seq.sortByDescending _.PublishDate)
                    |> getPrompt utcNow marketTrend
            return!
                Agent.getResultAsync<AssetRecommendationDto[]>
                    prompt agent
        }

    /// Creates an asset recommendation from the given DTO.
    let private ofDto dto =
        create
            (Asset.create dto.Symbol)
            dto.Action
            dto.Reason

    /// Creates recommendations for the given DTOs.
    let private createRecommendations candidates dtos =

            // map assets to generated recommendations
        let recoMap =
            dtos
                |> Seq.map (fun dto ->
                    let reco = ofDto dto
                    reco.Asset, reco)
                |> Map

            // find recommendation for each candidate, if it exists
        [|
            for (cand : Candidate) in candidates do
                match Map.tryFind cand.Asset recoMap with
                    | Some reco -> Ok reco
                    | None ->
                        Error (cand.Asset, exn("Agent ignored"))
        |]

    /// Determines asset recommendations from the given market
    /// overview:
    ///    1. Fetches news items specific to the candidate assets.
    ///    2. Asks agent to recommend an action for each asset.
    let getAsync httpClient agent marketTrend candidates =
        async {
                // get news items
            let utcNow = DateTime.UtcNow
            let! candItemArrays, candErrors =
                getNewsItems
                    httpClient utcNow candidates

                // news feed errors for some assets don't prevent success for other assets
            let feedErrorResults =
                [|
                    for cand, (_, exn) in candErrors do
                        Error (cand.Asset, exn)
                |]

                // get recommendations
            let! dtosResult =
                getRecommendationDtos
                    agent utcNow marketTrend candItemArrays
            match dtosResult with
                | Ok dtos ->
                    let successResults =
                        createRecommendations candidates dtos
                    return Success [|
                        yield! feedErrorResults
                        yield! successResults
                    |]
                | Error exn ->
                    return AgentError exn
        }
