namespace StockTradingBot

open System.IO
open System.Net.Http
open System.ServiceModel.Syndication
open System.Xml

type NewsFeed =
    {
        Name : string
        Url : string
        Filter : string -> bool
    }

module NewsFeed =

    let create name url filter =
        {
            Name = name
            Url = url
            Filter = filter
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
                        if newsFeed.Filter item.Title.Text then
                            item.SourceFeed <- feed   // ick
                            item
                ]
            with ex ->
                return Error (newsFeed, ex)
        } |> Async.AwaitTask

    let private isPersonal (text : string) =
        let tokens =
            text
                .ToLower()
                .Split([| ' '; ''' |])
                |> set
        tokens.Contains("i")
            || tokens.Contains("you")

    let feeds =
        [
            create
                "MarketWatch Top Stories"
                "https://feeds.content.dowjones.io/public/rss/mw_topstories"
                (isPersonal >> not)   // filter out personal finance stories
            create
                "CNBC Top News"
                "https://search.cnbc.com/rs/search/combinedcms/view.xml?partnerId=wrss01&id=100003114"
                (fun _ -> true)
            create
                "CNBC Finance"
                "https://search.cnbc.com/rs/search/combinedcms/view.xml?partnerId=wrss01&id=10000664"
                (fun _ -> true)
            create
                "Yahoo S&P 500"
                "https://feeds.finance.yahoo.com/rss/2.0/headline?s=%5EGSPC&region=US&lang=en-US"   // ^GSPC = S&P 500
                (fun _ -> true)
        ]
