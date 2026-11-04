# TeamHub AI Chat

`TeamHub.AI.Chat` is a reusable application module for a three-mode AI assistant. It depends only on
`TeamHub.AI` and Microsoft dependency-injection/configuration abstractions; it does not reference the
TeamHub web application, databases, or domain projects.

## Modes

- **TeamHub** calls registered `IAiChatKnowledgeSource` adapters. Each adapter must authorize the user
  before returning source text. The module never queries a host database directly.
- **General** never calls an `IAiChatKnowledgeSource`. It can use only conversation messages and
  explicitly attached text, Markdown, CSV, JSON, PDF, CHM, or image files. The web host extracts
  document text before calling this module and sends supported images as multimodal model inputs.
- **Meeting Notes** also never calls an `IAiChatKnowledgeSource`. It treats pasted text and
  attachments as the transcript, applies trusted host-provided formatting instructions, and must
  not invent missing meeting facts.

AI Chat follows the provider selected in AI Services. Ollama and llama.cpp normally run without a
credential; OpenAI-compatible providers use the configured encrypted shared credential or environment
fallback. PFB AI Gateway uses the signed-in user's encrypted token from My Connections when available
and otherwise uses the configured encrypted static M2M bearer/application key. Requests without a
signed-in user use the M2M key. Credentials are
used only in the server-side provider client and are never included in prompts or web responses.

TeamHub mode supports provider-independent, server-controlled tools through `IAiChatToolProvider`.
The model can request up to four registered tools with structured JSON arguments; TeamHub validates
the signed-in user, applies module authorization, executes the read-only tools, and supplies only the
compact result for a final model response. Host tool providers expose team and studio directories,
studio milestone reviews, internal support planning, Jira support tickets, and Confluence weekly
updates. Aggregated Jira worklog tools provide overall, studio, and employee support-effort views
without returning raw worklog comments. External-system tools require approval for the current question and use the signed-in user's
personal external credential. One planning call and one final-answer call share the same usage
operation regardless of the number of tools. The final ledger invocation records the selected tool
names without creating artificial token-bearing requests for internal tool execution. Each actual
tool execution is also recorded in the separate tool-activity stream with its status and duration;
arguments, returned content and credentials are never recorded.

## Reusing in another application

1. Reference `TeamHub.AI` and `TeamHub.AI.Chat`.
2. Register `AddTeamHubAi(configuration)` and `AddAiChat(configuration)`.
3. Register an `IAiProviderCredentialAccessor` for the host's secure credential store.
4. For application-aware chat, register one or more `IAiChatKnowledgeSource` implementations that
   perform module and row-level authorization before returning data.
5. Call `IAiChatService.GetModelsAsync` and `IAiChatService.SendAsync` from the host UI or API.

## Meeting Notes and Teams

The TeamHub host loads the editable `AI/MeetingNotes.md` file for every Meeting Notes request. Its
path, optional studio instruction uploads, and optional Teams destinations are managed under
**Configuration > AI Services > Meeting Notes**. The user may select an active studio before
generation. When that studio has an uploaded additional-instructions file, the host appends it to the shared file;
otherwise it uses only the shared instructions. Additional files should contain only studio-specific
differences so shared rules are not duplicated and token usage stays low. The uploaded filename and
Markdown content are stored in the database-backed AI configuration. Administrators can replace,
download, or remove a saved additional-instructions file. Legacy server-file paths remain readable during migration.
The page creates one
configuration row for every active studio from Studio Configuration. An
administrator enters the HTTPS URL created by the Teams **Workflows** webhook
template for that channel. Webhook URLs are encrypted and are never displayed again or returned to
the browser. Publishing is a separate user action after generation, and users can copy, edit, or
download the generated `.md` content without publishing it. The host exposes this publishing step
as an optional response action in both Meeting Notes and General modes; it is not part of AI
generation and never posts automatically.

Normal users do not upload the configured instructions. They select a studio and TeamHub resolves
the saved additional instructions automatically. A collapsed, optional multi-file `.md` upload is
available for one-off additional formatting guidance. These files are appended after the default and
studio instructions for that request and cannot change transcript accuracy, source isolation, or security rules.

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
