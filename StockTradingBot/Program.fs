open System
open System.IO
open System.Net.Http
open System.ServiceModel.Syndication
open System.Xml

let feedUrls =
    [
        "https://feeds.content.dowjones.io/public/rss/mw_topstories"                             // "MarketWatch.com - Top Stories"
        "https://search.cnbc.com/rs/search/combinedcms/view.xml?partnerId=wrss01&id=100003114"   // CNBC: "US Top News and Analysis"
        "https://search.cnbc.com/rs/search/combinedcms/view.xml?partnerId=wrss01&id=10000664"    // CNBC: "Finance"
        "https://feeds.finance.yahoo.com/rss/2.0/headline?s=%5EGSPC&region=US&lang=en-US"        // "Yahoo! Finance: ^GSPC News" (S&P 500)
    ]

let getItems (httpClient : HttpClient) (feedUrl : string) =
    task {
        try
            let! rssXml = httpClient.GetStringAsync(feedUrl)
            use stringReader = new StringReader(rssXml)
            use xmlReader = XmlReader.Create(stringReader)
            let feed = SyndicationFeed.Load(xmlReader)
            for item in feed.Items do
                item.SourceFeed <- feed   // ick
            return Ok feed.Items
        with ex ->
            return Error $"{feedUrl}: {ex.Message}"
    } |> Async.AwaitTask

do
        // create HTTP client
    use httpClient =
        let client = new HttpClient()
        client.DefaultRequestHeaders
            .UserAgent
            .ParseAdd("StockTradingBot/0.1 (mailto:brianberns@gmail.com)")   // needed to avoid 429 errors from Yahoo
        client

        // fetch news items
    let items, errors =
        feedUrls
            |> Seq.map (getItems httpClient)
            |> Async.Parallel
            |> Async.RunSynchronously
            |> Array.partitionWith (function
                | Ok items -> Choice1Of2 items
                | Error message -> Choice2Of2 message)

        // log errors
    for error in errors do
        printfn $"Error: {error}"

    let items =
        items
            |> Seq.concat
            |> Seq.distinctBy _.Id
            |> Seq.sortByDescending _.PublishDate
    for item in items do
        printfn ""
        printfn $"{item.SourceFeed.Title.Text}"
        printfn $"{item.Title.Text}"
        printfn $"{DateTime.UtcNow - item.PublishDate.UtcDateTime}"
        if item.Summary <> null then
            printfn $"{item.Summary.Text}"
