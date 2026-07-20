# AgentDesk Full Access, Resizable Inspector, and Font Scaling Design

## Goal

Add three persistent Windows desktop capabilities: a native-confirmed Full Access mode that automatically answers engine tool approvals, a resizable inspector whose width survives restarts, and font scaling shared by the workbench, terminal, and diff viewer.

## Security and ownership

- Full Access is off by default and can only be enabled after a native WinUI `ContentDialog`. A WebView may request the change but cannot bypass native confirmation.
- The host selects `AllowOnce` for ACP tool requests while enabled. It never installs a permanent engine rule, so disabling the setting restores interactive approval immediately.
- Extension installation, MCP management, Windows UI Automation, credentials, restore operations, and insecure transport keep their independent safety gates.
- Missing allow options, rejected responses, stale sessions, and response failures fall back to the normal approval dialog and never become a global engine error.
- The setting is stored in `%LOCALAPPDATA%\AgentDesk\ui-settings.json`; old schemas migrate with Full Access disabled.

## Inspector layout

- The native content Grid becomes `* / 8 / Inspector` and uses a WinUI `Thumb` for mouse, touch, and keyboard resizing.
- Width defaults to 360 DIP, is at least 320 DIP and at most 960 DIP, while preserving at least 560 DIP for the workbench. Temporary window-size clamping does not overwrite the preferred width.
- Left grows, Right shrinks, Shift accelerates, and Enter, Space, or double-click resets to 360 DIP. Modal UI disables the splitter.
- `%LOCALAPPDATA%\AgentDesk\window-layout.json` stores the preferred width independently. Corrupt or unwritable layout state falls back silently and does not affect engine status.

## Font scaling

- Settings expose 90%, 100%, 110%, 125%, and 140%; new installs and migrated settings default to 110%.
- CSS font sizes use a 10px rem baseline. Both WebViews apply the authoritative preference event immediately.
- Monaco and xterm update their canvas font options from the same percentage and then relayout or fit.
- Text scales independently from viewport width. Stable control dimensions remain in place and are checked for Chinese text, keyboard use, high DPI, and narrow layouts.

## Protocol and flow

`UiPreferences` schema 4 adds `fullAccessEnabled` and `fontScalePercent`. The strict C# and TypeScript contracts add matching fields. The workbench persists a debounced full snapshot; the host validates it, performs native confirmation, saves it, and broadcasts the authoritative snapshot to both surfaces.

## Acceptance

- Interactive approval remains when Full Access is off, ordinary `execute` calls continue automatically after one native confirmation, and disabling restores prompts.
- Auto-response failures remain manually actionable without an engine-failed banner.
- Inspector drag, restart persistence, preferred-width restoration, keyboard control, and reset all work.
- Every font level immediately affects chat, composer, settings, navigation, terminal, and diff and survives restart.
- Web, .NET, XAML policy, real sidecar, real provider, and packaged EXE checks pass.
