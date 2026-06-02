namespace TradingBot

open System
open System.Globalization
open Microsoft.Data.Sqlite

type Persistence = {
    Init                : decimal -> unit
    GetPortfolio        : unit -> Portfolio
    SetCashAndPositions : Usd -> Map<Asset, Position> -> unit
    RecordTrade         : Trade -> unit
    RecordDecisionCycle : DateTimeOffset -> string -> string -> string -> unit
    RecordPrice         : PriceSnapshot -> unit
    TryRecordNews       : NewsItem -> bool
    LastTradeAt         : Asset -> DateTimeOffset option
    RecentTrades        : float -> Trade list
    SeenNewsIds         : unit -> Set<string>
    RecentDecisionCycles : int -> (DateTimeOffset * string * string * string) list
}

module Persistence =

    let private inv = CultureInfo.InvariantCulture
    let private toText (d : decimal) = d.ToString(inv)
    let private toTs   (dt : DateTimeOffset) = dt.ToString("o", inv)
    let private parseDec (s : string) = Decimal.Parse(s, NumberStyles.Float, inv)
    let private parseTs  (s : string) =
        DateTimeOffset.Parse(s, inv, DateTimeStyles.RoundtripKind)

    let private schemaSql = """
        PRAGMA journal_mode = WAL;
        CREATE TABLE IF NOT EXISTS portfolio (
            id INTEGER PRIMARY KEY,
            cash_usd TEXT NOT NULL,
            updated_at TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS positions (
            asset TEXT PRIMARY KEY,
            qty TEXT NOT NULL,
            avg_cost_usd TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS trades (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            ts TEXT NOT NULL,
            asset TEXT NOT NULL,
            side TEXT NOT NULL,
            qty TEXT NOT NULL,
            price_usd TEXT NOT NULL,
            fee_usd TEXT NOT NULL,
            addv_usd TEXT,
            broker_order_id TEXT
        );
        CREATE INDEX IF NOT EXISTS idx_trades_asset_ts ON trades(asset, ts);
        CREATE TABLE IF NOT EXISTS decisions (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            ts TEXT NOT NULL,
            raw_response TEXT NOT NULL,
            decisions_json TEXT NOT NULL,
            applied_json TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS prices (
            ts TEXT NOT NULL,
            asset TEXT NOT NULL,
            price_usd TEXT NOT NULL,
            change_24h REAL NOT NULL,
            change_7d REAL NOT NULL,
            PRIMARY KEY (ts, asset)
        );
        CREATE TABLE IF NOT EXISTS news_seen (
            id TEXT PRIMARY KEY,
            source TEXT NOT NULL,
            ts TEXT NOT NULL,
            title TEXT NOT NULL,
            url TEXT NOT NULL
        );
    """

    let create (dbPath : string) : Persistence =
        let connStr = sprintf "Data Source=%s" dbPath
        let conn = new SqliteConnection(connStr)
        conn.Open()

        let bind (cmd : SqliteCommand) (parameters : (string * obj) list) =
            for (name, value) in parameters do
                cmd.Parameters.AddWithValue(name, value) |> ignore

        let exec (sql : string) (parameters : (string * obj) list) : int =
            use cmd = conn.CreateCommand()
            cmd.CommandText <- sql
            bind cmd parameters
            cmd.ExecuteNonQuery()

        let query (sql : string) (parameters : (string * obj) list) (read : SqliteDataReader -> 'T) : 'T list =
            use cmd = conn.CreateCommand()
            cmd.CommandText <- sql
            bind cmd parameters
            use reader = cmd.ExecuteReader()
            [ while reader.Read() do yield read reader ]

        let nullableString (n : string option) : obj =
            match n with
            | Some s -> box s
            | None   -> box DBNull.Value

        {
            Init = fun startingCash ->
                exec schemaSql [] |> ignore
                let count =
                    use cmd = conn.CreateCommand()
                    cmd.CommandText <- "SELECT COUNT(*) FROM portfolio"
                    cmd.ExecuteScalar() :?> int64
                if count = 0L then
                    exec "INSERT INTO portfolio (id, cash_usd, updated_at) VALUES (1, $cash, $now)"
                        [ "$cash", box (toText startingCash)
                          "$now",  box (toTs DateTimeOffset.UtcNow) ]
                    |> ignore

            GetPortfolio = fun () ->
                let cashRow =
                    query "SELECT cash_usd, updated_at FROM portfolio WHERE id = 1" []
                          (fun r -> r.GetString(0), r.GetString(1))
                    |> List.head
                let (cashText, updatedText) = cashRow
                let positions =
                    query "SELECT asset, qty, avg_cost_usd FROM positions" []
                          (fun r ->
                              let a = Asset (r.GetString(0))
                              a, { Asset      = a
                                   Qty        = Qty (parseDec (r.GetString(1)))
                                   AvgCostUsd = Usd (parseDec (r.GetString(2))) })
                { CashUsd   = Usd (parseDec cashText)
                  Positions = Map.ofList positions
                  AsOf      = parseTs updatedText }

            SetCashAndPositions = fun (Usd cash) positions ->
                use tx = conn.BeginTransaction()
                let txExec sql parameters =
                    use cmd = conn.CreateCommand()
                    cmd.Transaction <- tx
                    cmd.CommandText <- sql
                    bind cmd parameters
                    cmd.ExecuteNonQuery() |> ignore
                txExec "UPDATE portfolio SET cash_usd = $cash, updated_at = $now WHERE id = 1"
                    [ "$cash", box (toText cash); "$now", box (toTs DateTimeOffset.UtcNow) ]
                txExec "DELETE FROM positions" []
                for KeyValue(asset, pos) in positions do
                    txExec "INSERT INTO positions (asset, qty, avg_cost_usd) VALUES ($a, $q, $c)"
                        [ "$a", box (Asset.value asset)
                          "$q", box (toText (Qty.value pos.Qty))
                          "$c", box (toText (Usd.value pos.AvgCostUsd)) ]
                tx.Commit()

            RecordTrade = fun t ->
                let addvParam : obj =
                    match t.AddvUsd with
                    | Some v -> box (toText v)
                    | None   -> box DBNull.Value
                exec """INSERT INTO trades
                        (ts, asset, side, qty, price_usd, fee_usd, addv_usd, broker_order_id)
                        VALUES ($ts, $a, $s, $q, $p, $f, $addv, $oid)"""
                    [ "$ts",   box (toTs t.At)
                      "$a",    box (Asset.value t.Asset)
                      "$s",    box (TradeAction.toString t.Side)
                      "$q",    box (toText (Qty.value t.Qty))
                      "$p",    box (toText (Usd.value t.PriceUsd))
                      "$f",    box (toText (Usd.value t.FeeUsd))
                      "$addv", addvParam
                      "$oid",  nullableString t.BrokerOrderId ]
                |> ignore

            RecordDecisionCycle = fun ts rawResp decisionsJson appliedJson ->
                exec """INSERT INTO decisions (ts, raw_response, decisions_json, applied_json)
                        VALUES ($ts, $raw, $dj, $aj)"""
                    [ "$ts",  box (toTs ts)
                      "$raw", box rawResp
                      "$dj",  box decisionsJson
                      "$aj",  box appliedJson ]
                |> ignore

            RecordPrice = fun snap ->
                exec """INSERT OR REPLACE INTO prices
                        (ts, asset, price_usd, change_24h, change_7d)
                        VALUES ($ts, $a, $p, $c24, $c7)"""
                    [ "$ts",  box (toTs snap.At)
                      "$a",   box (Asset.value snap.Asset)
                      "$p",   box (toText (Usd.value snap.PriceUsd))
                      "$c24", box snap.Change24hPct
                      "$c7",  box snap.Change7dPct ]
                |> ignore

            TryRecordNews = fun n ->
                let rows =
                    exec """INSERT OR IGNORE INTO news_seen (id, source, ts, title, url)
                            VALUES ($id, $src, $ts, $t, $u)"""
                         [ "$id",  box n.Id
                           "$src", box n.Source
                           "$ts",  box (toTs n.At)
                           "$t",   box n.Title
                           "$u",   box n.Url ]
                rows > 0

            LastTradeAt = fun asset ->
                let rows =
                    query "SELECT ts FROM trades WHERE asset = $a ORDER BY ts DESC LIMIT 1"
                          [ "$a", box (Asset.value asset) ]
                          (fun r -> r.GetString(0))
                match rows with
                | [ ts ] -> Some (parseTs ts)
                | _ -> None

            RecentTrades = fun sinceHours ->
                let cutoff = DateTimeOffset.UtcNow.AddHours(-sinceHours)
                query """SELECT ts, asset, side, qty, price_usd, fee_usd, addv_usd, broker_order_id
                         FROM trades WHERE ts >= $cut ORDER BY ts DESC"""
                    [ "$cut", box (toTs cutoff) ]
                    (fun r ->
                        let side =
                            match TradeAction.tryParse (r.GetString(2)) with
                            | Some a -> a
                            | None   -> Hold
                        let addv = if r.IsDBNull(6) then None else Some (parseDec (r.GetString(6)))
                        let oid  = if r.IsDBNull(7) then None else Some (r.GetString(7))
                        { Asset         = Asset (r.GetString(1))
                          Side          = side
                          Qty           = Qty (parseDec (r.GetString(3)))
                          PriceUsd      = Usd (parseDec (r.GetString(4)))
                          FeeUsd        = Usd (parseDec (r.GetString(5)))
                          AddvUsd       = addv
                          At            = parseTs (r.GetString(0))
                          BrokerOrderId = oid })

            SeenNewsIds = fun () ->
                query "SELECT id FROM news_seen" [] (fun r -> r.GetString(0))
                |> Set.ofList

            RecentDecisionCycles = fun n ->
                query """SELECT ts, raw_response, decisions_json, applied_json
                         FROM decisions ORDER BY id DESC LIMIT $n"""
                      [ "$n", box (int64 n) ]
                      (fun r -> parseTs (r.GetString(0)), r.GetString(1), r.GetString(2), r.GetString(3))
        }
