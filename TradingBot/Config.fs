namespace TradingBot

open System
open System.IO
open Microsoft.Extensions.Configuration

type RiskSettings = {
    MinTradeUsd           : decimal
    MaxTradeUsd           : decimal
    MaxPositionPct        : float
    CashReservePct        : float
    PerAssetCooldownHours : float
    FeeBps                : int
    SpreadBps             : int
    SlippageBps           : int
}

type LlmSettings = {
    Model    : string
    Endpoint : string
    ApiKey   : string         // secret, never in appsettings.json
}

type AlpacaSettings = {
    KeyId  : string           // secret
    Secret : string           // secret
    Paper  : bool             // true = paper-api, false = live-api
}

type AppSettings = {
    StartingCashUsd    : decimal
    CycleIntervalHours : float
    Assets             : string list
    Broker             : string
    DatabasePath       : string
    Llm                : LlmSettings
    Alpaca             : AlpacaSettings
    Risk               : RiskSettings
}

module Config =

    let private require (cfg : IConfiguration) (key : string) =
        match cfg.[key] with
        | null | "" -> failwithf "Missing required configuration value: %s" key
        | v -> v

    let private optional (cfg : IConfiguration) (key : string) (defaultValue : string) =
        match cfg.[key] with
        | null | "" -> defaultValue
        | v -> v

    let private requireDecimal cfg key = require cfg key |> decimal
    let private requireFloat   cfg key = require cfg key |> float
    let private requireInt     cfg key = require cfg key |> int
    let private optionalBool (cfg : IConfiguration) key (defaultValue : bool) =
        match cfg.[key] with
        | null | "" -> defaultValue
        | v -> (let ok, b = Boolean.TryParse v in if ok then b else defaultValue)

    let private readAssets (cfg : IConfiguration) =
        let section = cfg.GetSection("Assets")
        [
            for child in section.GetChildren() do
                match child.Value with
                | null | "" -> ()
                | v -> yield v.Trim()
        ]

    let load () : AppSettings =
        let thisAssembly = typeof<AppSettings>.Assembly
        let builder =
            ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional = false, reloadOnChange = false)
                .AddEnvironmentVariables(prefix = "TRADINGBOT_")
                .AddUserSecrets(thisAssembly, true)
        let cfg : IConfiguration = builder.Build()

        let assets = readAssets cfg
        if assets.IsEmpty then
            failwith "Configuration must specify a non-empty Assets array"

        {
            StartingCashUsd    = requireDecimal cfg "StartingCashUsd"
            CycleIntervalHours = requireFloat   cfg "CycleIntervalHours"
            Assets             = assets
            Broker             = require        cfg "Broker"
            DatabasePath       = require        cfg "DatabasePath"
            Llm = {
                Model    = require  cfg "Llm:Model"
                Endpoint = require  cfg "Llm:Endpoint"
                ApiKey   = optional cfg "Llm:ApiKey" ""
            }
            Alpaca = {
                KeyId  = optional    cfg "Alpaca:KeyId" ""
                Secret = optional    cfg "Alpaca:Secret" ""
                Paper  = optionalBool cfg "Alpaca:Paper" true
            }
            Risk = {
                MinTradeUsd           = requireDecimal cfg "Risk:MinTradeUsd"
                MaxTradeUsd           = requireDecimal cfg "Risk:MaxTradeUsd"
                MaxPositionPct        = requireFloat   cfg "Risk:MaxPositionPct"
                CashReservePct        = requireFloat   cfg "Risk:CashReservePct"
                PerAssetCooldownHours = requireFloat   cfg "Risk:PerAssetCooldownHours"
                FeeBps                = requireInt     cfg "Risk:FeeBps"
                SpreadBps             = requireInt     cfg "Risk:SpreadBps"
                SlippageBps           = requireInt     cfg "Risk:SlippageBps"
            }
        }
