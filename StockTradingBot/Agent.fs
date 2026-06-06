namespace StockTradingBot

open System
open System.ClientModel

open Microsoft.Extensions.AI
open Microsoft.Extensions.Configuration

open OpenAI

type Model =
    {
        /// Model ID.
        Id : string

        /// Name of API key in config.
        ApiKeyName : string

        /// Modle endpoint.
        Endpoint : string
    }

module Model =

    /// Google Gemini.
    let gemini =
        {
            Id = "gemini-3.5-flash"
            ApiKeyName = "Gemini:ApiKey"
            Endpoint = "https://generativelanguage.googleapis.com/v1beta/openai/"
        }

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

    /// Creates an agent.
    let create (config : IConfiguration) model =
        let openAIClient =
            OpenAIClient(
                ApiKeyCredential(config[model.ApiKeyName]),
                OpenAIClientOptions(
                    Endpoint = Uri(model.Endpoint)))
        let chatClient =
            openAIClient
                .GetChatClient(model.Id)
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
