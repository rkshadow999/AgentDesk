# AgentDesk Plan Mode Design

[English](2026-07-17-agentdesk-plan-mode-design.md) | [简体中文](2026-07-17-agentdesk-plan-mode-design.zh-CN.md)

**Status:** Approved for implementation on 2026-07-17

## Objective and Scope

Deliver the first AgentDesk Beta slice by exposing the upstream ACP Plan Mode through the Windows desktop client. This slice includes capability negotiation, authoritative session-mode state, the approved composer-adjacent segmented control, strict protocol parsing, and end-to-end ordering tests.

Out of scope: session search/history, worktrees, background tasks, multi-agent dashboards, and cloud services. Those remain later Beta milestones.

## User Experience

- The composer footer contains a stable two-option segmented control: `Execute` / `Plan` (`执行` / `计划`).
- The control sits immediately before the execution-profile safety control. Session mode and sandbox profile remain visually and semantically separate.
- The selected mode applies only to the active session and persists for subsequent prompts in that session.
- A mode change is disabled while a prompt is running or while an engine request is pending.
- The UI shows the requested state as pending, then commits the selection only after the host confirms it.
- If the sidecar does not advertise Plan Mode, the Plan segment is disabled with an accessible explanation. The client never simulates Plan Mode locally.
- Native execution risk acknowledgement remains independent. Selecting Plan does not bypass or weaken permission prompts.

## Protocol and Types

- Add `SessionMode` with exact wire values `default` and `plan` in Core and Web protocols.
- `agentdesk/v1/initialize` protocol version 1 adds a backward-compatible `sessionModes` capability list.
- `EngineCapabilities` exposes the supported session modes. Missing capability data means `default` only.
- `IEngineClient.SetSessionModeAsync(SessionId, SessionMode)` maps to ACP `session/set_mode` using the upstream mode identifier.
- Prompt web commands carry a required session mode and continue to carry execution profile, native-risk acknowledgement, and workspace generation.
- Add a host-to-Web `session/mode/changed` event containing the authoritative mode and session ID.
- Unknown, incorrectly cased, whitespace-padded, null, numeric, or unsupported mode values fail closed during JSON parsing.

## State and Data Flow

1. The Web UI starts with desired mode `default`.
2. The user selects a segment. The Web UI sends the desired mode with the next prompt; for an existing idle session it may also request an immediate mode change.
3. On the first prompt, the host starts the sidecar, initializes and authenticates it, creates a session, validates the advertised capability, calls `SetSessionModeAsync`, then calls `PromptAsync`.
4. On later prompts, the host changes mode only when desired mode differs from the last confirmed mode.
5. A successful engine response updates host state and emits `session/mode/changed`. Engine `current_mode_update` notifications are accepted only for the active engine generation and active session.
6. Sidecar restart clears confirmed mode and repeats capability validation before another prompt.

The required ordering is `initialize -> authenticate -> new/load session -> set mode -> prompt`. A prompt is never sent if mode negotiation fails.

## Failure and Race Handling

- Unsupported Plan Mode produces a localized error and leaves the session in `default`.
- Engine failure during mode change does not send the queued prompt.
- Stale mode updates from a previous sidecar generation or session are ignored.
- Workspace changes retain the selected UI preference but require the existing workspace risk acknowledgement again; a newly created session receives its own mode setting.
- Duplicate confirmation events are idempotent.
- Cancellation during a pending mode change leaves no prompt in progress and no optimistic confirmed state.

## Testing

- Rust tests verify the initialize capability payload and backward-compatible protocol version.
- Engine transport tests verify exact ACP request shape, response handling, unsupported-mode behavior, and notification parsing.
- App tests verify first-session ordering, existing-session changes, stale-event rejection, cancellation, restart behavior, and no-prompt-on-failure.
- Web protocol tests reject malformed modes.
- Web component tests verify segmented-control accessibility, Chinese/English labels, pending/running disabled states, keyboard operation, capability fallback, and separation from native risk acknowledgement.
- A real browser check verifies focus, layout, console output, and 125%-200% zoom without clipping.

## Delivery

Plan Mode is developed on a Beta feature branch created from the published Alpha `main`. It is pushed for review only after the focused red-green cycles and the complete Rust/Web/.NET verification suite pass.
