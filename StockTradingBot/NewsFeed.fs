namespace StockTradingBot

open System
open System.IO
open System.Net.Http
open System.ServiceModel.Syndication
open System.Xml

type NewsItemFilter = SyndicationItem -> bool

module NewsItemFilter =

    let hasSummary : NewsItemFilter =
        fun item ->
            item.Summary <> null
                && not (String.IsNullOrWhiteSpace(
                    item.Summary.Text))

    let applyFilters filters item =
        Seq.forall (fun (filter : NewsItemFilter) ->
            filter item) filters

type NewsFeed =
    {
        Name : string
        Url : string
        Filters : seq<NewsItemFilter>
    }

module NewsFeed =

    let create name url filters =
        {
            Name = name
            Url = url
            Filters = filters
        }

    let getItemsAsync (httpClient : HttpClient) newsFeed =
        task {
            try
                let! rssXml = httpClient.GetStringAsync(newsFeed.Url)
                use stringReader = new StringReader(rssXml)
                use xmlReader = XmlReader.Create(stringReader)
                let feed = SyndicationFeed.Load(xmlReader)
                return Ok [|
                    for item in feed.Items do
                        if NewsItemFilter.applyFilters newsFeed.Filters item then
                            item.SourceFeed <- feed   // ick
                            item
                |]
            with exn ->
                return Error (newsFeed, exn)
        } |> Async.AwaitTask
