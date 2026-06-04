namespace StockTradingBot

open System

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
