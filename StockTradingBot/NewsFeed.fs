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

    let getItems (httpClient : HttpClient) newsFeed =
        task {
            try
                let! rssXml = httpClient.GetStringAsync(newsFeed.Url)
                use stringReader = new StringReader(rssXml)
                use xmlReader = XmlReader.Create(stringReader)
                let feed = SyndicationFeed.Load(xmlReader)
                return Ok [
                    for item in feed.Items do
                        if NewsItemFilter.applyFilters newsFeed.Filters item then
                            item.SourceFeed <- feed   // ick
                            item
                ]
            with ex ->
                return Error (newsFeed, ex)
        } |> Async.AwaitTask

    let private isPersonal : NewsItemFilter =
        fun item ->
            item.Title.Text.Split([| ' '; ''' |])
                |> Array.contains("I")

    let feeds =
        [
            create
                "MarketWatch Top Stories"
                "https://feeds.content.dowjones.io/public/rss/mw_topstories"
                [
                    NewsItemFilter.hasSummary
                    (isPersonal >> not)   // filter out personal finance stories
                ]
            create
                "CNBC Top News"
                "https://search.cnbc.com/rs/search/combinedcms/view.xml?partnerId=wrss01&id=100003114"
                [ NewsItemFilter.hasSummary ]
            create
                "CNBC Finance"
                "https://search.cnbc.com/rs/search/combinedcms/view.xml?partnerId=wrss01&id=10000664"
                [ NewsItemFilter.hasSummary ]
            create
                "Yahoo S&P 500"
                "https://feeds.finance.yahoo.com/rss/2.0/headline?s=%5EGSPC&region=US&lang=en-US"   // ^GSPC = S&P 500
                [ NewsItemFilter.hasSummary ]
        ]
