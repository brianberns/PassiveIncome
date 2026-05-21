namespace TradingBot

open System.Text.Json
open System.Text.Json.Serialization

module Json =

    /// Shared JsonSerializerOptions for all REST-API DTOs.
    /// FSharp.SystemTextJson converter handles option types, records, and DUs.
    let options =
        let o = JsonSerializerOptions(PropertyNameCaseInsensitive = true)
        o.Converters.Add(JsonFSharpConverter(unionEncoding = JsonUnionEncoding.ExternalTag))
        o
