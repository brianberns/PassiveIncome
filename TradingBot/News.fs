namespace TradingBot

open System
open System.Net
open System.Net.Http
open System.ServiceModel.Syndication
open System.Text.RegularExpressions
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
    let private maxSummaryChars       = 400

    /// RSS <description> fields often contain HTML and boilerplate. Strip tags,
    /// decode entities, collapse whitespace, and bound the length so we feed the
    /// LLM clean prose without blowing up token usage.
    let private cleanSummary (raw : string) : string =
        if String.IsNullOrWhiteSpace raw then ""
        else
            let noTags    = Regex.Replace(raw, "<[^>]+>", " ")
            let decoded   = WebUtility.HtmlDecode noTags
            let collapsed = Regex.Replace(decoded, @"\s+", " ").Trim()
            if collapsed.Length > maxSummaryChars then
                collapsed.Substring(0, maxSummaryChars).TrimEnd() + "…"
            else collapsed

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
                          let summary =
                              if isNull item.Summary then "" else cleanSummary item.Summary.Text
                          { Id      = id
                            Source  = sourceName
                            Title   = title
                            Summary = summary
                            Url     = link
                            At      = item.PublishDate } ]
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
