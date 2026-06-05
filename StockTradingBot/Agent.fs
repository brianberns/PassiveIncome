namespace StockTradingBot

open System
open System.Text.Json
open System.Text.Json.Serialization
open System.Text.Json.Serialization.Metadata

open Microsoft.Extensions.AI
open Microsoft.Extensions.Configuration

type Agent =
    {
        GoogleClient : Google.GenAI.Client
        ChatClient : IChatClient
    }

    member this.Dispose() =
        this.ChatClient.Dispose()
        this.GoogleClient.Dispose()

    interface IDisposable with
        member this.Dispose() = this.Dispose()

module Agent =

    let private modelId = "gemini-2.5-flash"

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

    // https://gemini.google.com/app/655a2dc4f288270c
    let private options =
        let options =
            JsonSerializerOptions(
                TypeInfoResolver = DefaultJsonTypeInfoResolver())
        options.Converters.Add(JsonFSharpConverter())
        options

    let getResultAsync<'t> (prompt : string) agent =
        task {
            try
                let! response =
                    ChatClientStructuredOutputExtensions
                        .GetResponseAsync<'t>(
                            agent.ChatClient, prompt, options)
                return Ok response.Result
            with exn ->
                return Error exn
        } |> Async.AwaitTask
