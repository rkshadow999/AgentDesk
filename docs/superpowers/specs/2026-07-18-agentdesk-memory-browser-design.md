# AgentDesk Versioned Memory Browser Design

## Scope

Add four versioned ACP extension methods without changing Workbench UI:

- `agentdesk/v1/memory/list`
- `agentdesk/v1/memory/read`
- `agentdesk/v1/memory/write`
- `agentdesk/v1/memory/delete`

Every request is scoped by an active `sessionId`. The engine resolves that session's `cwd` and constructs `xai_grok_memory::MemoryStorage` for the matching workspace. Clients never provide filesystem paths.

## Addressing And Bounds

Files use opaque IDs:

- `global` maps only to the global `MEMORY.md`.
- `workspace` maps only to the current workspace `MEMORY.md`.
- `session/<filename>.md` maps only to a direct child of the current workspace `sessions` directory.

Session filenames must be a single ASCII basename containing only letters, digits, `.`, `_`, and `-`, ending in `.md`. The storage layer rejects symlinks, Windows reparse points, non-regular files, escaped canonical paths, and changed targets detected between validation and access.

Responses are bounded to 512 list entries and 64 KiB of UTF-8 content. Metadata exposes ID, scope, display name, byte length, modification time, and mutability, never an absolute path.

## Mutation Approval

`write` and `delete` require `confirmed: true`. With `confirmed: false`, the engine returns `confirmation_required` and does not touch disk. This two-step gate lets a later Workbench change present native approval UI while keeping the protocol safe now. Writes are atomic and replace only the selected file. Session-log writes require an existing listed log; they cannot create attacker-chosen names.

## Desktop Contract

`IEngineClient` gains strongly typed list, read, write, and delete methods. `AcpEngineClient` validates request values before transport and strictly validates response schemas, enum values, counts, content length, and metadata sizes.

## Testing

Rust tests cover opaque-ID parsing, workspace isolation, traversal, symlink/reparse rejection, size limits, atomic writes, confirmed mutation gates, and staging-free deletion. C# transport tests cover exact wire shapes, typed parsing, confirmation responses, oversized input rejection, unknown fields, and unknown enum rejection.
