namespace StockTradingBot

open System.Net.Http
open System.Reflection

open Microsoft.Extensions.Configuration

module Program =

    let config =
        let assembly = Assembly.GetExecutingAssembly()
        ConfigurationBuilder()
            .AddUserSecrets(assembly)
            .Build()

    let run () =

            // create HTTP client
        use httpClient =
            let client = new HttpClient()
            client.DefaultRequestHeaders
                .UserAgent
                .ParseAdd("StockTradingBot/0.1 (mailto:brianberns@gmail.com)")   // needed to avoid 429 errors from Yahoo
            client

            // create agent
        use agent = Agent.create config

        async {
            match! Agent.getMarketOverviewAsync httpClient agent with
                | Overview overview ->
                    printfn $"Trend: {overview.Trend}"
                    for candidate in overview.Candidates do
                        printfn $"{candidate.Symbol}: {candidate.Reason}"
                | FeedErrors errors ->
                    for feed, exn in errors do
                        printfn $"Error in {feed.Name} news feed: {exn.Message}"
                | ChatError exn ->
                    printfn $"{exn.Message}"
        } |> Async.RunSynchronously

    let test () =
        let broker = Broker.create config
        async {
            let! portfolio = Broker.getPortfolio broker
            printfn "%A" portfolio.TradableCash
        } |> Async.RunSynchronously

    // run ()
    test ()
