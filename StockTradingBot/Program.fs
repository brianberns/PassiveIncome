open System
open System.IO
open System.Net.Http
open System.ServiceModel.Syndication
open System.Xml

let feedUrls =
    [
        "https://feeds.content.dowjones.io/public/rss/mw_topstories"
    ]

let getItems (httpClient : HttpClient) (feedUrl : string) =
    task {
        let! rssXml = httpClient.GetStringAsync(feedUrl)
        use stringReader = new StringReader(rssXml)
        use xmlReader = XmlReader.Create(stringReader)
        let feed = SyndicationFeed.Load(xmlReader)
        for item in feed.Items do
            item.SourceFeed <- feed   // ick
        return feed.Items
    } |> Async.AwaitTask

do
    use httpClient = new HttpClient()
    let items =
        feedUrls
            |> Seq.map (getItems httpClient)
            |> Async.Parallel
            |> Async.RunSynchronously
            |> Seq.concat
            |> Seq.sortByDescending _.PublishDate
    for item in items do
        printfn ""
        printfn $"{item.SourceFeed.Title.Text}"
        printfn $"{item.Title.Text}"
        printfn $"{DateTime.UtcNow - item.PublishDate.UtcDateTime}"
        if item.Summary <> null then
            printfn $"{item.Summary.Text}"
