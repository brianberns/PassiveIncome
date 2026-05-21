namespace TradingBot

open System
open System.Net.Http
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading.Tasks

type Prices = {
    Fetch : Asset list -> Task<PriceSnapshot list>
}

module Prices =

    /// Symbol → CoinGecko coin id. Top-5 hardcoded; extend if AppSettings.Assets grows.
    let private symbolToCoinGeckoId =
        Map.ofList [
            "BTC", "bitcoin"
            "ETH", "ethereum"
            "SOL", "solana"
            "XRP", "ripple"
            "BNB", "binancecoin"
        ]

    [<CLIMutable>]
    type private MarketRow = {
        [<JsonPropertyName("id")>]                                      Id           : string
        [<JsonPropertyName("symbol")>]                                  Symbol       : string
        [<JsonPropertyName("current_price")>]                           CurrentPrice : decimal
        [<JsonPropertyName("price_change_percentage_24h_in_currency")>] Pct24h       : System.Nullable<float>
        [<JsonPropertyName("price_change_percentage_7d_in_currency")>]  Pct7d        : System.Nullable<float>
    }

    let private idToSymbol = symbolToCoinGeckoId |> Map.toSeq |> Seq.map (fun (s, i) -> i, s) |> Map.ofSeq

    let create (httpClient : HttpClient) : Prices =
        {
            Fetch = fun assets ->
                task {
                    let ids =
                        assets
                        |> List.map (fun (Asset s) ->
                            match Map.tryFind s symbolToCoinGeckoId with
                            | Some id -> id
                            | None    -> failwithf "No CoinGecko ID mapping for asset %s" s)
                        |> String.concat ","
                    let url =
                        sprintf "https://api.coingecko.com/api/v3/coins/markets?vs_currency=usd&ids=%s&order=market_cap_desc&per_page=%d&page=1&sparkline=false&price_change_percentage=24h,7d"
                                ids (List.length assets)
                    use! response = httpClient.GetAsync(url)
                    response.EnsureSuccessStatusCode() |> ignore
                    let! content = response.Content.ReadAsStringAsync()
                    let rows =
                        JsonSerializer.Deserialize<MarketRow array>(content, Json.options)
                    let now = DateTimeOffset.UtcNow
                    return
                        rows
                        |> Array.map (fun r ->
                            let symbol =
                                match Map.tryFind r.Id idToSymbol with
                                | Some s -> s
                                | None   -> r.Symbol.ToUpperInvariant()
                            { Asset        = Asset symbol
                              PriceUsd     = Usd r.CurrentPrice
                              Change24hPct = if r.Pct24h.HasValue then r.Pct24h.Value else 0.0
                              Change7dPct  = if r.Pct7d.HasValue  then r.Pct7d.Value  else 0.0
                              At           = now })
                        |> Array.toList
                }
        }
