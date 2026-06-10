namespace StockTradingBot

open Fable.Remoting.Client

module Remoting =

    /// Prefix routes with /StockTradingBot.
    let routeBuilder typeName methodName = 
        sprintf "/StockTradingBot/%s/%s" typeName methodName

    /// Server API.
    let api =
        Remoting.createApi()
            |> Remoting.withRouteBuilder routeBuilder
            |> Remoting.buildProxy<IStockTradingBotApi>

    let getResults () =
        async {
            match! Async.Catch(api.GetResults ()) with
                | Choice1Of2 results -> return results
                | Choice2Of2 exn -> return failwith exn.Message
        }
