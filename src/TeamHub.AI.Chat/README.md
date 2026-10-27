# TeamHub AI Chat

`TeamHub.AI.Chat` is a reusable application module for a two-mode AI assistant. It depends only on
`TeamHub.AI` and Microsoft dependency-injection/configuration abstractions; it does not reference the
TeamHub web application, databases, or domain projects.

## Modes

- **TeamHub** calls registered `IAiChatKnowledgeSource` adapters. Each adapter must authorize the user
  before returning source text. The module never queries a host database directly.
- **General** never calls an `IAiChatKnowledgeSource`. It can use only conversation messages and
  explicitly attached text, Markdown, CSV, JSON, PDF, CHM, or image files. The web host extracts
  document text before calling this module and sends supported images as multimodal model inputs.

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

## TeamHub runtime playbook

The host may provide a Markdown playbook through `IAiChatInstructionProvider`. The TeamHub host
keeps this in `AI/TeamHubAI.md` and selects sections by the authorized source IDs returned for the
current question. It does not send the whole file on every request. Source-free ordinary questions
therefore add no playbook text, while a directory question receives only the core and directory
sections, for example.

Keep runtime sections short, independent, and free of background documentation. Shared security,
Markdown, citation, and response-schema rules already belong to the chat module's system prompt and
should not be repeated in the playbook. When adding a new TeamHub knowledge source:

1. Implement authorization and row filtering in `IAiChatKnowledgeSource`.
2. Give the result a stable `teamhub-...` source ID.
3. Add a concise runtime section to `TeamHubAI.md` only when the source needs domain-specific
   interpretation.
4. Map the source ID to that section in `TeamHubAiInstructionFileProvider`.
5. Add selection tests proving unrelated sections are excluded.

Dynamic rows, columns, and tables under the existing custom-tab source do not need new playbook
instructions. Never put credentials, private sample data, deployment notes, or maintenance
checklists in the runtime playbook because included text consumes input tokens.
