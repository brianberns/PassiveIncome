namespace StockTradingBot

open System
open System.Net.Http
open System.Reflection

open Microsoft.Extensions.Configuration

module Program =

    let run () =

            // create HTTP client
        use httpClient =
            let client = new HttpClient()
            client.DefaultRequestHeaders
                .UserAgent
                .ParseAdd("StockTradingBot/0.1 (mailto:brianberns@gmail.com)")   // needed to avoid 429 errors from Yahoo
            client

            // create agent
        use agent =
            let config =
                let assembly = Assembly.GetExecutingAssembly()
                ConfigurationBuilder()
                    .AddUserSecrets(assembly)
                    .Build()
            Agent.create config

        task {
            let! overview =
                Agent.getMarketOverviewAsync httpClient agent
            printfn $"Trend: {overview.Trend}"
            for candidate in overview.Candidates do
                printfn $"{candidate.Symbol}: {candidate.Reason}"
        }
            |> Async.AwaitTask
            |> Async.RunSynchronously

    run ()
