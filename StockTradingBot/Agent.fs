namespace StockTradingBot

open System

open Microsoft.Extensions.AI
open Microsoft.Extensions.Configuration

/// Decision-making agent.
type Agent =
    {
        /// Gemini.
        GoogleClient : Google.GenAI.Client

        /// .NET wrapper.
        ChatClient : IChatClient
    }

    /// Cleanup.
    member this.Dispose() =
        this.ChatClient.Dispose()
        this.GoogleClient.Dispose()

    interface IDisposable with

        /// Cleanup.
        member this.Dispose() = this.Dispose()

module Agent =

    let private modelId = "gemini-2.5-flash-lite"

    /// Creates an agent.
    let create (config : IConfiguration) =
        let googleClient =
            new Google.GenAI.Client(
                apiKey = config["Gemini:ApiKey"])
        let chatClient =
            googleClient.AsIChatClient(modelId)
        {
            GoogleClient = googleClient
            ChatClient = chatClient
        }

    /// Prompts the agent to respond with a specific type
    /// of data.
    let getResultAsync<'t> (prompt : string) agent =
        task {
            try
                let! response =
                    ChatClientStructuredOutputExtensions
                        .GetResponseAsync<'t>(
                            agent.ChatClient, prompt)
                return Ok response.Result
            with exn ->
                return Error exn
        } |> Async.AwaitTask
