namespace StockTradingBot

open System
open System.ClientModel
open System.ClientModel.Primitives

open Microsoft.Extensions.AI
open Microsoft.Extensions.Configuration

open OpenAI

/// Chat model.
type Model =
    {
        /// Model name.
        Name : string

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
            Name = "Gemini"
            Id = "gemini-3-flash-preview"
            ApiKeyName = "Gemini:ApiKey"
            Endpoint = "https://generativelanguage.googleapis.com/v1beta/openai/"
            SupportsJsonSchema = true
        }

    /// Groq.
    let groq =
        {
            Name = "Groq"
            Id = "llama-3.3-70b-versatile"
            ApiKeyName = "Groq:ApiKey"
            Endpoint = "https://api.groq.com/openai/v1"
            SupportsJsonSchema = false
        }

    /// OpenRouter.
    let openRouter =
        {
            Name = "OpenRouter"
            Id = "openai/gpt-oss-120b:free"
            ApiKeyName = "OpenRouter:ApiKey"
            Endpoint = "https://openrouter.ai/api/v1"
            SupportsJsonSchema = true
        }

    /// GitHub.
    let gitHub =
        {
            Name = "GitHub"
            Id = "openai/gpt-4.1"
            ApiKeyName = "GitHub:Token"
            Endpoint = "https://models.github.ai/inference"
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
                    Endpoint = Uri(model.Endpoint),
                    NetworkTimeout = TimeSpan.FromMinutes(5.0),
                    RetryPolicy = ClientRetryPolicy(maxRetries = 3)))   // maxRetries+1 tries total + small exponential backoff between retries
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
            try
                let! response =
                    ChatClientStructuredOutputExtensions
                        .GetResponseAsync<'t>(
                            agent.ChatClient,
                            prompt,
                            ChatOptions(Temperature = 0f),   // most deterministic,
                            useJsonSchemaResponseFormat =
                                agent.Model.SupportsJsonSchema)
                return Ok response.Result
            with exn ->
                return Error exn.Message
        } |> Async.AwaitTask
