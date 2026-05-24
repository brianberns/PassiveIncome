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

    let private maxHeadlinesPerCycle = 30
    let private freshnessWindowHours = 36.0   // equities: span weekends/overnight gaps
    let private maxSummaryChars      = 400

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

    /// Feeds for this cycle: one ticker-scoped Yahoo Finance feed built from the
    /// asset list, plus a general market feed for macro context.
    let private feedsFor (assets : Asset list) : (string * string) list =
        let tickers = assets |> List.map Asset.value |> String.concat ","
        [ "Yahoo",       sprintf "https://feeds.finance.yahoo.com/rss/2.0/headline?s=%s&region=US&lang=en-US" tickers
          "MarketWatch", "http://feeds.marketwatch.com/marketwatch/topstories/" ]

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
                          let title = if isNull item.Title then "" else item.Title.Text
                          let link =
                              item.Links
                              |> Seq.tryHead
                              |> Option.map (fun l -> if isNull l.Uri then "" else l.Uri.ToString())
                              |> Option.defaultValue ""
                          let id = if String.IsNullOrEmpty item.Id then link else item.Id
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
                return []   // tolerate a broken/unavailable feed
        }

    let create (httpClient : HttpClient) : News =
        {
            Fetch = fun assets ->
                task {
                    let cutoff = DateTimeOffset.UtcNow.AddHours(-freshnessWindowHours)
                    let! results =
                        feedsFor assets
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
