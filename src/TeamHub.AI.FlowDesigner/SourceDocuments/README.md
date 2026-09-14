# Source documents

The first source processor accepts in-memory `text/plain`, `text/markdown`, and `text/x-markdown` content. It normalizes line endings and enforces document-count, prompt-length, media-type, empty-content, duplicate-ID, and combined-size limits before inference. Complete source content is not retained by this module.

PDF, Word, and approved Confluence adapters remain future work. Those adapters must preserve stable page, heading, or paragraph locations for evidence references.