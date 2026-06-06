namespace StockTradingBot

open System
open System.ClientModel

open Microsoft.Extensions.AI
open Microsoft.Extensions.Configuration

open OpenAI

/// Decision-making agent.
type Agent =
    {
        /// .NET wrapper around chat API.
        ChatClient : IChatClient
    }

    /// Cleanup.
    member this.Dispose() =
        this.ChatClient.Dispose()

    interface IDisposable with

        /// Cleanup.
        member this.Dispose() = this.Dispose()

module Agent =

    let private modelId = "gemini-3.5-flash"
    let private apiKey = "Gemini:ApiKey"
    let private endpoint = "https://generativelanguage.googleapis.com/v1beta/openai/"

    /// Creates an agent.
    let create (config : IConfiguration) =
        let openAIClient =
            OpenAIClient(
                ApiKeyCredential(config[apiKey]),
                OpenAIClientOptions(Endpoint = Uri(endpoint)))
        let chatClient =
            openAIClient
                .GetChatClient(modelId)
                .AsIChatClient()
        {
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
