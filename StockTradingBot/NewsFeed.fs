namespace StockTradingBot

/// News feed error.
type NewsFeedError =
    {
        /// News feed name.
        FeedName : string

        /// Error message.
        Message : string
    }

module NewsFeedError =

    /// Creates a news feed error.
    let create feedName message =
        {
            FeedName = feedName
            Message = message
        }

#if !FABLE_COMPILER

open System
open System.IO
open System.Net.Http
open System.ServiceModel.Syndication
open System.Xml

[<AutoOpen>]
module ExceptionExt =

    type Exception with

        /// Gets full message, including inner exception.
        member exn.FullMessage =
            String.concat Environment.NewLine [
                exn.Message
                if exn.InnerException <> null then
                    exn.InnerException.FullMessage
            ]

/// Filters items from a news feed.
type NewsItemFilter = SyndicationItem -> bool

module NewsItemFilter =

    /// The given item has a non-empty summary field?
    let hasSummary : NewsItemFilter =
        fun item ->
            item.Summary <> null
                && not (String.IsNullOrWhiteSpace(
                    item.Summary.Text))

    /// The given item was published in the last day?
    let isRecent utcNow : NewsItemFilter =
        let oneDay = TimeSpan.FromDays(1)
        fun item ->
            utcNow - item.PublishDate.UtcDateTime < oneDay

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
                            Seq.forall (fun filter ->
                                filter item) newsFeed.Filters
                        if keep then
                            item.SourceFeed <- feed   // ick
                            item
                |]
            with exn ->
                let error =
                    NewsFeedError.create
                        newsFeed.Name exn.FullMessage
                return Error error
        } |> Async.AwaitTask

#endif
