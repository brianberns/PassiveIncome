namespace StockTradingBot

open System.IO
open System.Net.Http
open System.ServiceModel.Syndication
open System.Xml

type NewsFeed =
    {
        Name : string
        Url : string
    }

module NewsFeed =

    let create name url =
        {
            Name = name
            Url = url
        }

    let getItems (httpClient : HttpClient) feed =
        task {
            try
                let! rssXml = httpClient.GetStringAsync(feed.Url)
                use stringReader = new StringReader(rssXml)
                use xmlReader = XmlReader.Create(stringReader)
                let feed = SyndicationFeed.Load(xmlReader)
                for item in feed.Items do
                    item.SourceFeed <- feed   // ick
                return Ok feed.Items
            with ex ->
                return Error (feed, ex)
        } |> Async.AwaitTask

    let feeds =
        [
            create
                "MarketWatch Top Stories"
                "https://feeds.content.dowjones.io/public/rss/mw_topstories"
            create
                "CNBC Top News"
                "https://search.cnbc.com/rs/search/combinedcms/view.xml?partnerId=wrss01&id=100003114"
            create
                "CNBC Finance"
                "https://search.cnbc.com/rs/search/combinedcms/view.xml?partnerId=wrss01&id=10000664"
            create
                "Yahoo S&P 500"
                "https://feeds.finance.yahoo.com/rss/2.0/headline?s=%5EGSPC&region=US&lang=en-US"   // ^GSPC = S&P 500
        ]
