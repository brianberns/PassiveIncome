namespace StockTradingBot

open Fable.Remoting.Server
open Fable.Remoting.Suave

module Api =

    /// Stock trading bot API.
    let stockTradingBotApi (dir : string) =
        {
            Ping =
                fun n ->
                    async {
                        return n
                    }
        }

module Remoting =

    /// Build API.
    let webPart dir =
        Remoting.createApi()
            |> Remoting.fromValue (Api.stockTradingBotApi dir)
            |> Remoting.buildWebPart
