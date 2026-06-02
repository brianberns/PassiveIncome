namespace TradingBot

open System
open System.Net
open System.Net.Http
open System.ServiceModel.Syndication
open System.Text.RegularExpressions
open System.Threading.Tasks
open System.Xml

type News = {
    /// World/financial top-stories feeds — drives stage-1 discovery.
    FetchGeneral : unit -> Task<NewsItem list>
    /// Headlines for a single ticker — drives stage-2 per-asset evaluation.
    FetchForTicker : Asset -> Task<NewsItem list>
}

module News =

    let private maxHeadlinesPerCycle = 30
    let private maxTickerHeadlines   = 12
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

    /// General world/financial feeds for stage-1 discovery (no ticker scope).
    let private generalFeeds : (string * string) list =
        [ "MarketWatch", "http://feeds.marketwatch.com/marketwatch/topstories/"
          "YahooTop",    "https://finance.yahoo.com/news/rssindex"
          "CNBC",        "https://search.cnbc.com/rs/search/combinedcms/view.xml?partnerId=wrss01&id=100003114" ]

    /// Per-ticker Yahoo Finance headline feed for stage-2 evaluation.
    let private tickerFeed (asset : Asset) : string * string =
        "Yahoo",
        sprintf "https://feeds.finance.yahoo.com/rss/2.0/headline?s=%s&region=US&lang=en-US"
                (Asset.value asset)

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

    let private gather (httpClient : HttpClient) (feeds : (string * string) list) (limit : int) =
        task {
            let cutoff = DateTimeOffset.UtcNow.AddHours(-freshnessWindowHours)
            let! results =
                feeds
                |> List.map (fun (name, url) -> fetchFeed httpClient name url)
                |> Task.WhenAll
            return
                results
                |> Array.collect List.toArray
                |> Array.filter (fun n -> n.At >= cutoff)
                |> Array.sortByDescending (fun n -> n.At)
                |> Array.truncate limit
                |> Array.toList
        }

    let create (httpClient : HttpClient) : News =
        {
            FetchGeneral = fun () ->
                gather httpClient generalFeeds maxHeadlinesPerCycle

            FetchForTicker = fun asset ->
                gather httpClient [ tickerFeed asset ] maxTickerHeadlines
        }
