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

## Token limits and usage

`MaximumOutputTokens` is the initial output allowance sent with each chat generation request. It
defaults to `2000` and accepts values from `128` through `32000`. Administrators manage it, together
with the other Chat limits, in **Configuration > AI Services > AI Chat**. Saved values apply to new
requests immediately and are stored with the AI Services configuration. Configuration-provider
values under `AI:Chat` can optionally override bootstrap defaults before an administrator saves the
first configuration. Higher values can increase response time and provider usage.
The PFB gateway adapter can retry a response that ends at the token limit, progressively increasing
the allowance up to `32000`.

When a provider returns its token-usage metadata, `AiChatResponse.Usage` contains the input, output,
and total token counts. The web chat displays these counts on each response and totals them for the
current browser chat session. Providers that omit usage metadata are shown as not reported; the UI
does not present an estimate as an actual provider charge.

Do not place API keys, bearer tokens, connection strings, or authorization metadata in an
`AiChatSource`.
