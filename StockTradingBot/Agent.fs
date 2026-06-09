namespace StockTradingBot

open System
open System.ClientModel
open System.Threading

open Microsoft.Extensions.AI
open Microsoft.Extensions.Configuration

open OpenAI

/// Chat model.
type Model =
    {
        /// Model ID.
        Id : string

        /// Name of API key in config.
        ApiKeyName : string

        /// Modle endpoint.
        Endpoint : string

        /// Model supports the native `json_schema` response
        /// format? If not, wrapper must fall back to embedding
        /// the schema in the prompt.
        SupportsJsonSchema : bool
    }

module Model =

    /// Google Gemini.
    let gemini =
        {
            Id = "gemini-3.5-flash"
            ApiKeyName = "Gemini:ApiKey"
            Endpoint = "https://generativelanguage.googleapis.com/v1beta/openai/"
            SupportsJsonSchema = true
        }

    /// Groq.
    let groq =
        {
            Id = "llama-3.3-70b-versatile"
            ApiKeyName = "Groq:ApiKey"
            Endpoint = "https://api.groq.com/openai/v1"
            SupportsJsonSchema = false
        }

    /// OpenRouter.
    let openRouter =
        {
            Id = "openrouter/owl-alpha"
            ApiKeyName = "OpenRouter:ApiKey"
            Endpoint = "https://openrouter.ai/api/v1"
            SupportsJsonSchema = true
        }

/// Decision-making agent.
type Agent =
    {
        /// .NET wrapper around model API.
        ChatClient : IChatClient

        /// Model-specifc details.
        Model : Model
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
            Model = model
        }

    /// Prompts the agent to respond with a specific type
    /// of data.
    let getResultAsync<'t> (prompt : string) agent =
        task {
            use cts =
                new CancellationTokenSource(
                    TimeSpan.FromMinutes(5.0))
            try
                let! response =
                    ChatClientStructuredOutputExtensions
                        .GetResponseAsync<'t>(
                            agent.ChatClient,
                            prompt,
                            useJsonSchemaResponseFormat =
                                agent.Model.SupportsJsonSchema,
                            cancellationToken = cts.Token)
                return Ok response.Result
            with exn ->
                return Error exn
        } |> Async.AwaitTask
