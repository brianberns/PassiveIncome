namespace TradingBot

open System

type RiskRejection = {
    Decision : Decision
    Reason   : string
}

type RiskOutcome = {
    Orders   : Order list
    Rejected : RiskRejection list
}

module Risk =

    let private portfolioValue (portfolio : Portfolio) (priceMap : Map<Asset, Usd>) : decimal =
        portfolio.Positions
        |> Map.fold (fun acc asset pos ->
            let price =
                Map.tryFind asset priceMap
                |> Option.map Usd.value
                |> Option.defaultValue (Usd.value pos.AvgCostUsd)
            acc + Qty.value pos.Qty * price) (Usd.value portfolio.CashUsd)

    let private positionUsd (portfolio : Portfolio) (priceMap : Map<Asset, Usd>) (asset : Asset) : decimal =
        match Map.tryFind asset portfolio.Positions with
        | None -> 0m
        | Some pos ->
            let price =
                Map.tryFind asset priceMap
                |> Option.map Usd.value
                |> Option.defaultValue (Usd.value pos.AvgCostUsd)
            Qty.value pos.Qty * price

    let private cooldownActive
        (lastTrade : Asset -> DateTimeOffset option)
        (asset : Asset)
        (now : DateTimeOffset)
        (cooldownHours : float) : bool =
        match lastTrade asset with
        | Some t -> (now - t).TotalHours < cooldownHours
        | None -> false

    /// Pure validation + sizing. Buys are evaluated independently against the
    /// snapshot cash/position state; if multiple buys collectively exceed cash,
    /// the broker will reject the surplus at execution time. This keeps Risk a
    /// pure function of inputs.
    let validateAndSize
        (settings    : RiskSettings)
        (portfolio   : Portfolio)
        (prices      : PriceSnapshot list)
        (lastTrade   : Asset -> DateTimeOffset option)
        (now         : DateTimeOffset)
        (decisions   : Decision list)
        : RiskOutcome =

        let priceMap =
            prices |> List.map (fun p -> p.Asset, p.PriceUsd) |> Map.ofList
        let totalValue   = portfolioValue portfolio priceMap
        let maxPosUsd    = totalValue * decimal settings.MaxPositionPct
        let cashAvail    = Usd.value portfolio.CashUsd * (1m - decimal settings.CashReservePct)
        let roundTripBps = settings.FeeBps + settings.SpreadBps + settings.SlippageBps
        let roundTripRt  = decimal roundTripBps * 2m / 10000m

        let evaluate (decision : Decision) : Choice<Order, RiskRejection> =
            let reject reason = Choice2Of2 { Decision = decision; Reason = reason }
            let asset = decision.Asset
            let size  = Usd.value decision.SizeUsd

            if decision.Action = Hold then
                reject "Hold (no-op)"
            elif cooldownActive lastTrade asset now settings.PerAssetCooldownHours then
                reject (sprintf "Cooldown active (within %g h)" settings.PerAssetCooldownHours)
            elif size < settings.MinTradeUsd then
                reject (sprintf "Proposed $%.2f below min $%.2f" size settings.MinTradeUsd)
            else
                let clampedSize = min size settings.MaxTradeUsd
                match decision.Action with
                | Buy ->
                    let posRemaining = max 0m (maxPosUsd - positionUsd portfolio priceMap asset)
                    let finalSize    = clampedSize |> min posRemaining |> min cashAvail
                    if finalSize < settings.MinTradeUsd then
                        if posRemaining < settings.MinTradeUsd then
                            reject (sprintf "Would exceed %.0f%% position cap"
                                            (settings.MaxPositionPct * 100.0))
                        else
                            reject "Insufficient cash after reserve"
                    elif decimal decision.Confidence < roundTripRt then
                        reject (sprintf "Confidence %.3f below round-trip cost %.4f"
                                        decision.Confidence (float roundTripRt))
                    else
                        Choice1Of2 {
                            Asset = asset; Side = Buy
                            SizeUsd = Usd finalSize
                            SourceDecision = decision
                        }
                | Sell ->
                    let posUsd = positionUsd portfolio priceMap asset
                    if posUsd <= 0m then
                        reject "No position to sell"
                    else
                        let finalSize = min clampedSize posUsd
                        if finalSize < settings.MinTradeUsd then
                            reject "Position too small to sell"
                        else
                            Choice1Of2 {
                                Asset = asset; Side = Sell
                                SizeUsd = Usd finalSize
                                SourceDecision = decision
                            }
                | Hold -> reject "Hold (no-op)"

        let orders, rejected =
            decisions
            |> List.fold (fun (orders, rejs) d ->
                match evaluate d with
                | Choice1Of2 o -> o :: orders, rejs
                | Choice2Of2 r -> orders, r :: rejs) ([], [])

        { Orders   = List.rev orders
          Rejected = List.rev rejected }
