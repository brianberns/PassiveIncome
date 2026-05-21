namespace TradingBot

open System
open System.Net.Http
open System.ServiceModel.Syndication
open System.Threading.Tasks
open System.Xml

type News = {
    Fetch : Asset list -> Task<NewsItem list>
}

module News =

    /// RSS feeds we pull each cycle. Hardcoded for v1 — move to config later
    /// if we want per-environment feed lists.
    let private defaultFeeds : (string * string) list = [
        "CoinDesk",      "https://www.coindesk.com/arc/outboundfeeds/rss/?outputType=xml"
        "CoinTelegraph", "https://cointelegraph.com/rss"
        "Decrypt",       "https://decrypt.co/feed"
        "TheBlock",      "https://www.theblock.co/rss.xml"
    ]

    let private maxHeadlinesPerCycle  = 30
    let private freshnessWindowHours  = 24.0

    let private fetchFeed (httpClient : HttpClient) (sourceName : string) (url : string) =
        task {
            try
                use! response = httpClient.GetAsync(url)
                response.EnsureSuccessStatusCode() |> ignore
                use! stream = response.Content.ReadAsStreamAsync()
                let settings = XmlReaderSettings(Async = true, DtdProcessing = DtdProcessing.Ignore)
                use xmlReader = XmlReader.Create(stream, settings)
                let feed = SyndicationFeed.Load(xmlReader)
                let items =
                    [ for item in feed.Items ->
                          let title =
                              if isNull item.Title then "" else item.Title.Text
                          let link =
                              item.Links
                              |> Seq.tryHead
                              |> Option.map (fun l ->
                                  if isNull l.Uri then "" else l.Uri.ToString())
                              |> Option.defaultValue ""
                          let id =
                              if String.IsNullOrEmpty item.Id then link else item.Id
                          { Id             = id
                            Source         = sourceName
                            Title          = title
                            Url            = link
                            At             = item.PublishDate
                            VotesPositive  = 0
                            VotesNegative  = 0
                            VotesImportant = 0 } ]
                return items
            with _ ->
                // Per-feed failures are tolerated — one broken feed should not
                // wipe the cycle. The orchestrator will still log "no fresh news"
                // if every feed fails.
                return []
        }

    let create (httpClient : HttpClient) : News =
        {
            Fetch = fun _assets ->
                task {
                    let cutoff = DateTimeOffset.UtcNow.AddHours(-freshnessWindowHours)
                    let! results =
                        defaultFeeds
                        |> List.map (fun (name, url) -> fetchFeed httpClient name url)
                        |> Task.WhenAll
                    return
                        results
                        |> Array.collect List.toArray
                        |> Array.filter (fun n -> n.At >= cutoff)
                        |> Array.sortByDescending (fun n -> n.At)
                        |> Array.truncate maxHeadlinesPerCycle
                        |> Array.toList
                }
        }
