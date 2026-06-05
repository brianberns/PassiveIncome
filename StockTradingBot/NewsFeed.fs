namespace StockTradingBot

open System
open System.IO
open System.Net.Http
open System.ServiceModel.Syndication
open System.Xml

/// Filters items from a news feed.
type NewsItemFilter = SyndicationItem -> bool

module NewsItemFilter =

    /// The given item has a non-empty summary field?
    let hasSummary : NewsItemFilter =
        fun item ->
            item.Summary <> null
                && not (String.IsNullOrWhiteSpace(
                    item.Summary.Text))

    /// Applies the given filters to the given news items.
    let applyFilters filters item =
        Seq.forall (fun (filter : NewsItemFilter) ->
            filter item) filters

/// News feed (via RSS, for example).
type NewsFeed =
    {
        /// Feed name.
        Name : string

        /// Feed URL.
        Url : string

        /// Filters applicable to this feed.
        Filters : seq<NewsItemFilter>
    }

module NewsFeed =

    /// Creates a news feed.
    let create name url filters =
        {
            Name = name
            Url = url
            Filters = filters
        }

    /// Gets the items currently in the given feed.
    let getItemsAsync (httpClient : HttpClient) newsFeed =
        task {
            try
                let! rssXml = httpClient.GetStringAsync(newsFeed.Url)
                use stringReader = new StringReader(rssXml)
                use xmlReader = XmlReader.Create(stringReader)
                let feed = SyndicationFeed.Load(xmlReader)
                return Ok [|
                    for item in feed.Items do
                        let keep =
                            NewsItemFilter.applyFilters
                                newsFeed.Filters item
                        if keep then
                            item.SourceFeed <- feed   // ick
                            item
                |]
            with exn ->
                return Error (newsFeed, exn)
        } |> Async.AwaitTask
