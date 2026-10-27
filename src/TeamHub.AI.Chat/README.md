# TeamHub AI Chat

`TeamHub.AI.Chat` is a reusable application module for a two-mode AI assistant. It depends only on
`TeamHub.AI` and Microsoft dependency-injection/configuration abstractions; it does not reference the
TeamHub web application, databases, or domain projects.

## Modes

- **TeamHub** calls registered `IAiChatKnowledgeSource` adapters. Each adapter must authorize the user
  before returning source text. The module never queries a host database directly.
- **General** never calls an `IAiChatKnowledgeSource`. It can use only conversation messages and
  explicitly attached text, Markdown, CSV, or JSON documents.

AI Chat follows the provider selected in AI Services. Ollama and llama.cpp normally run without a
credential; OpenAI-compatible providers use the configured encrypted shared credential or environment
fallback; PFB AI Gateway uses the signed-in user's encrypted token from My Connections. Credentials are
used only in the server-side provider client and are never included in prompts or web responses.

## Reusing in another application

1. Reference `TeamHub.AI` and `TeamHub.AI.Chat`.
2. Register `AddTeamHubAi(configuration)` and `AddAiChat(configuration)`.
3. Register an `IAiProviderCredentialAccessor` for the host's secure credential store.
4. For application-aware chat, register one or more `IAiChatKnowledgeSource` implementations that
   perform module and row-level authorization before returning data.
5. Call `IAiChatService.GetModelsAsync` and `IAiChatService.SendAsync` from the host UI or API.

Do not place API keys, bearer tokens, connection strings, or authorization metadata in an
`AiChatSource`.
