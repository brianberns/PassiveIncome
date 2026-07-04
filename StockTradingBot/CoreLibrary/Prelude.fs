namespace StockTradingBot

module Array =

    /// Not provided in older versions of FSharp.Core.
    let partitionWith (partitioner : _ -> Choice<_, _>) array =
        let array1, array2 =
            array
                |> Array.map partitioner
                |> Array.partition _.IsChoice1Of2
        array1
            |> Array.map (function
                | Choice1Of2 value -> value
                | Choice2Of2 _ -> failwith "Unexpected"),
        array2
            |> Array.map (function
                | Choice1Of2 _ -> failwith "Unexpected"
                | Choice2Of2 value -> value)

module Async =

    /// Maps over the given async computation.
    let map mapping computation =
        async {
            let! x = computation
            return mapping x
        }
