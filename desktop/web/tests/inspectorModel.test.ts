import { describe, expect, it } from "vitest";
import {
  createInitialInspectorState,
  getTerminalSnapshot,
  TERMINAL_TRANSCRIPT_MAX_CHARS,
  reduceInspectorEvent
} from "../src/inspectorModel";
import type { HostEvent } from "../src/hostBridge";

describe("inspector model", () => {
  it("projects xAI diff review updates into selectable file diffs", () => {
    const state = reduceInspectorEvent(
      createInitialInspectorState(),
      sessionUpdate("diff_review", {
        sessionUpdate: "diff_review",
        content: [
          {
            type: "diff",
            path: "desktop/src/AgentDesk.App/MainWindow.xaml",
            oldText: "<Grid />",
            newText: "<Grid><WebView2 /></Grid>"
          }
        ]
      })
    );

    expect(state.diffs).toEqual([
      {
        path: "desktop/src/AgentDesk.App/MainWindow.xaml",
        oldText: "<Grid />",
        newText: "<Grid><WebView2 /></Grid>"
      }
    ]);
    expect(state.selectedPath).toBe("desktop/src/AgentDesk.App/MainWindow.xaml");
  });

  it("merges tool call updates and produces terminal output from real execution events", () => {
    const created = reduceInspectorEvent(
      createInitialInspectorState(),
      sessionUpdate("tool_call", {
        sessionUpdate: "tool_call",
        toolCallId: "tool-1",
        title: "运行测试",
        kind: "execute",
        status: "in_progress",
        rawInput: { command: "dotnet", args: ["test", "AgentDesk.sln"] }
      })
    );
    const completed = reduceInspectorEvent(
      created,
      sessionUpdate("tool_call_update", {
        sessionUpdate: "tool_call_update",
        toolCallId: "tool-1",
        status: "completed",
        content: [
          { type: "content", content: { type: "text", text: "Passed: 58" } }
        ]
      })
    );

    const terminal = getTerminalSnapshot(completed.terminalTranscript);
    expect(terminal).toContain("> dotnet test AgentDesk.sln");
    expect(terminal).toContain("Passed: 58");
    expect(terminal).toContain("[completed]");
  });

  it("merges an orphan tool update when its base tool call arrives later", () => {
    const orphaned = reduceInspectorEvent(
      createInitialInspectorState(),
      sessionUpdate("tool_call_update", {
        sessionUpdate: "tool_call_update",
        toolCallId: "tool-race",
        status: "completed",
        content: [
          { type: "content", content: { type: "text", text: "Passed: 117" } }
        ]
      })
    );
    const merged = reduceInspectorEvent(
      orphaned,
      sessionUpdate("tool_call", {
        sessionUpdate: "tool_call",
        toolCallId: "tool-race",
        title: "运行桌面测试",
        kind: "execute",
        status: "in_progress",
        rawInput: { command: "dotnet", args: ["test", "AgentDesk.sln"] }
      })
    );

    const terminal = getTerminalSnapshot(merged.terminalTranscript);
    expect(terminal).toContain("> dotnet test AgentDesk.sln");
    expect(terminal).toContain("Passed: 117");
    expect(terminal).toContain("[completed]");
  });

  it("emits only the newly streamed execution suffix", () => {
    const created = reduceInspectorEvent(
      createInitialInspectorState(),
      sessionUpdate("tool_call", {
        sessionUpdate: "tool_call",
        toolCallId: "tool-stream",
        title: "Run build",
        kind: "execute",
        status: "in_progress",
        rawInput: { command: "npm", args: ["run", "build"] }
      })
    );
    const first = reduceInspectorEvent(
      created,
      sessionUpdate("tool_call_update", {
        sessionUpdate: "tool_call_update",
        toolCallId: "tool-stream",
        content: [
          { type: "content", content: { type: "text", text: "first line\n" } }
        ]
      })
    );
    const second = reduceInspectorEvent(
      first,
      sessionUpdate("tool_call_update", {
        sessionUpdate: "tool_call_update",
        toolCallId: "tool-stream",
        status: "completed",
        content: [
          {
            type: "content",
            content: { type: "text", text: "first line\nsecond line\n" }
          }
        ]
      })
    );

    expect(first.terminalAppend).toBe("first line\n");
    expect(second.terminalAppend).toBe("second line\n[completed]\n");
    expect(second.terminalRevision).toBe(first.terminalRevision + 1);
    expect(getTerminalSnapshot(second.terminalTranscript)).toContain(
      "first line\nsecond line\n[completed]"
    );
  });

  it("does not treat the base tool description as streamed output", () => {
    const created = reduceInspectorEvent(
      createInitialInspectorState(),
      sessionUpdate("tool_call", {
        sessionUpdate: "tool_call",
        toolCallId: "tool-described",
        title: "Run script",
        kind: "execute",
        status: "pending",
        rawInput: { command: "node", args: ["script.js"] },
        content: [
          {
            type: "content",
            content: { type: "text", text: "Running JavaScript script" }
          }
        ]
      })
    );
    const streamed = reduceInspectorEvent(
      created,
      sessionUpdate("tool_call_update", {
        sessionUpdate: "tool_call_update",
        toolCallId: "tool-described",
        status: "in_progress",
        content: [
          { type: "content", content: { type: "text", text: "step one\n" } }
        ]
      })
    );

    expect(getTerminalSnapshot(created.terminalTranscript)).not.toContain(
      "Running JavaScript script"
    );
    expect(streamed.terminalAppend).toBe("step one\n");
    expect(getTerminalSnapshot(streamed.terminalTranscript)).toContain("step one\n");
  });

  it("does not emit a base description on a status-only completion", () => {
    const created = reduceInspectorEvent(
      createInitialInspectorState(),
      sessionUpdate("tool_call", {
        sessionUpdate: "tool_call",
        toolCallId: "tool-status-only",
        title: "Run script",
        kind: "execute",
        status: "pending",
        rawInput: { command: "node", args: ["script.js"] },
        content: [
          {
            type: "content",
            content: { type: "text", text: "Running JavaScript script" }
          }
        ]
      })
    );
    const completed = reduceInspectorEvent(
      created,
      sessionUpdate("tool_call_update", {
        sessionUpdate: "tool_call_update",
        toolCallId: "tool-status-only",
        status: "completed"
      })
    );

    const terminal = getTerminalSnapshot(completed.terminalTranscript);
    expect(terminal).not.toContain("Running JavaScript script");
    expect(terminal).toContain("[completed]");
  });

  it("streams cumulative Bash raw output when content is not repeated", () => {
    const created = reduceInspectorEvent(
      createInitialInspectorState(),
      sessionUpdate("tool_call", {
        sessionUpdate: "tool_call",
        toolCallId: "tool-raw",
        title: "Run script",
        kind: "execute",
        status: "pending",
        rawInput: { command: "node", args: ["script.js"] }
      })
    );
    const first = reduceInspectorEvent(
      created,
      sessionUpdate("tool_call_update", {
        sessionUpdate: "tool_call_update",
        toolCallId: "tool-raw",
        status: "in_progress",
        rawOutput: { type: "Bash", output_for_prompt: "step one\n" }
      })
    );
    const completed = reduceInspectorEvent(
      first,
      sessionUpdate("tool_call_update", {
        sessionUpdate: "tool_call_update",
        toolCallId: "tool-raw",
        status: "completed",
        rawOutput: {
          type: "Bash",
          output_for_prompt: "step one\nstep two\n"
        }
      })
    );

    expect(first.terminalAppend).toBe("step one\n");
    expect(completed.terminalAppend).toBe("step two\n[completed]\n");
  });

  it("prefers exact terminal content over prompt-formatted Bash output", () => {
    const created = reduceInspectorEvent(
      createInitialInspectorState(),
      sessionUpdate("tool_call", {
        sessionUpdate: "tool_call",
        toolCallId: "tool-ansi",
        title: "Run colors",
        kind: "execute",
        status: "pending",
        rawInput: { command: "colors" }
      })
    );
    const streamed = reduceInspectorEvent(
      created,
      sessionUpdate("tool_call_update", {
        sessionUpdate: "tool_call_update",
        toolCallId: "tool-ansi",
        status: "in_progress",
        content: [
          {
            type: "content",
            content: { type: "text", text: "\u001b[31mred\u001b[0m\n" }
          }
        ],
        rawOutput: {
          type: "Bash",
          output_for_prompt: "red\n"
        }
      })
    );

    expect(streamed.terminalAppend).toBe("\u001b[31mred\u001b[0m\n");
  });

  it("uses an explicit Bash delta when the rolling buffer length is unchanged", () => {
    const created = reduceInspectorEvent(
      createInitialInspectorState(),
      sessionUpdate("tool_call", {
        sessionUpdate: "tool_call",
        toolCallId: "tool-rolling",
        title: "Run long task",
        kind: "execute",
        status: "pending",
        rawInput: { command: "long-task" }
      })
    );
    const first = reduceInspectorEvent(
      created,
      sessionUpdate("tool_call_update", {
        sessionUpdate: "tool_call_update",
        toolCallId: "tool-rolling",
        status: "in_progress",
        rawOutput: {
          type: "Bash",
          output_for_prompt: "aaaa",
          output_delta: [97, 97, 97, 97]
        }
      })
    );
    const rolled = reduceInspectorEvent(
      first,
      sessionUpdate("tool_call_update", {
        sessionUpdate: "tool_call_update",
        toolCallId: "tool-rolling",
        status: "in_progress",
        rawOutput: {
          type: "Bash",
          output_for_prompt: "aaab",
          output_delta: [98]
        }
      })
    );

    expect(first.terminalAppend).toBe("aaaa");
    expect(rolled.terminalAppend).toBe("b");
  });

  it("does not replay retained Bash output after a truncation gap", () => {
    const marker = "\n\n... (output truncated; 7 bytes unavailable) ...\n\n";
    const created = reduceInspectorEvent(
      createInitialInspectorState(),
      sessionUpdate("tool_call", {
        sessionUpdate: "tool_call",
        toolCallId: "tool-gap",
        title: "Run long output",
        kind: "execute",
        status: "pending",
        rawInput: { command: "long-output" }
      })
    );
    const first = reduceInspectorEvent(
      created,
      sessionUpdate("tool_call_update", {
        sessionUpdate: "tool_call_update",
        toolCallId: "tool-gap",
        status: "in_progress",
        rawOutput: {
          type: "Bash",
          output: [],
          output_delta: asciiBytes("abcdefghij"),
          total_bytes: 10,
          truncated: false
        }
      })
    );
    const shrunk = reduceInspectorEvent(
      first,
      sessionUpdate("tool_call_update", {
        sessionUpdate: "tool_call_update",
        toolCallId: "tool-gap",
        status: "in_progress",
        rawOutput: {
          type: "Bash",
          output: [],
          output_delta: asciiBytes("k"),
          total_bytes: 11,
          truncated: true
        }
      })
    );
    const gapped = reduceInspectorEvent(
      shrunk,
      sessionUpdate("tool_call_update", {
        sessionUpdate: "tool_call_update",
        toolCallId: "tool-gap",
        status: "in_progress",
        rawOutput: {
          type: "Bash",
          output: [],
          output_delta: asciiBytes(`${marker}yz`),
          total_bytes: 20,
          truncated: true
        }
      })
    );
    const completed = reduceInspectorEvent(
      gapped,
      sessionUpdate("tool_call_update", {
        sessionUpdate: "tool_call_update",
        toolCallId: "tool-gap",
        status: "completed",
        rawOutput: {
          type: "Bash",
          output: asciiBytes("abcde\n\n... (output truncated) ...\n\nyz"),
          output_for_prompt: "abcde\n\n... (output truncated) ...\n\nyz",
          total_bytes: 20,
          truncated: true
        }
      })
    );

    expect(first.terminalAppend).toBe("abcdefghij");
    expect(shrunk.terminalAppend).toBe("k");
    expect(gapped.terminalAppend).toBe(`${marker}yz`);
    expect(completed.terminalAppend).toBe("\n[completed]\n");
    const transcript = getTerminalSnapshot(completed.terminalTranscript);
    expect(countOccurrences(transcript, "abcde")).toBe(1);
    expect(countOccurrences(transcript, "yz")).toBe(1);
    expect(countOccurrences(transcript, marker)).toBe(1);
  });

  it("appends only final bytes produced after a truncation gap", () => {
    const marker = "\n\n... (output truncated; 96 bytes unavailable) ...\n\n";
    const created = reduceInspectorEvent(
      createInitialInspectorState(),
      sessionUpdate("tool_call", {
        sessionUpdate: "tool_call",
        toolCallId: "tool-gap-final-tail",
        title: "Run long output",
        kind: "execute",
        status: "pending",
        rawInput: { command: "long-output" }
      })
    );
    const gapped = reduceInspectorEvent(
      created,
      sessionUpdate("tool_call_update", {
        sessionUpdate: "tool_call_update",
        toolCallId: "tool-gap-final-tail",
        status: "in_progress",
        rawOutput: {
          type: "Bash",
          output: [],
          output_delta: asciiBytes(`${marker}tail`),
          total_bytes: 100,
          truncated: true
        }
      })
    );
    const completed = reduceInspectorEvent(
      gapped,
      sessionUpdate("tool_call_update", {
        sessionUpdate: "tool_call_update",
        toolCallId: "tool-gap-final-tail",
        status: "completed",
        rawOutput: {
          type: "Bash",
          output: asciiBytes("head\n\n... (output truncated) ...\n\ntail++"),
          output_for_prompt: "head\n\n... (output truncated) ...\n\ntail++",
          total_bytes: 102,
          truncated: true
        }
      })
    );

    expect(gapped.terminalAppend).toBe(`${marker}tail`);
    expect(completed.terminalAppend).toBe("++\n[completed]\n");
    const transcript = getTerminalSnapshot(completed.terminalTranscript);
    expect(countOccurrences(transcript, "tail")).toBe(1);
    expect(transcript).toContain("tail++\n[completed]");
  });

  it("does not replay a truncated final snapshot after lossless deltas", () => {
    const created = reduceInspectorEvent(
      createInitialInspectorState(),
      sessionUpdate("tool_call", {
        sessionUpdate: "tool_call",
        toolCallId: "tool-lossless-truncated",
        title: "Run retained output",
        kind: "execute",
        status: "pending",
        rawInput: { command: "retained-output" }
      })
    );
    const first = reduceInspectorEvent(
      created,
      sessionUpdate("tool_call_update", {
        sessionUpdate: "tool_call_update",
        toolCallId: "tool-lossless-truncated",
        status: "in_progress",
        rawOutput: {
          type: "Bash",
          output: [],
          output_delta: asciiBytes("abcdefghij"),
          total_bytes: 10,
          truncated: false
        }
      })
    );
    const shrunk = reduceInspectorEvent(
      first,
      sessionUpdate("tool_call_update", {
        sessionUpdate: "tool_call_update",
        toolCallId: "tool-lossless-truncated",
        status: "in_progress",
        rawOutput: {
          type: "Bash",
          output: [],
          output_delta: asciiBytes("k"),
          total_bytes: 11,
          truncated: true
        }
      })
    );
    const completed = reduceInspectorEvent(
      shrunk,
      sessionUpdate("tool_call_update", {
        sessionUpdate: "tool_call_update",
        toolCallId: "tool-lossless-truncated",
        status: "completed",
        rawOutput: {
          type: "Bash",
          output: asciiBytes("abcde\n\n... (output truncated) ...\n\nfghijk"),
          output_for_prompt: "abcde\n\n... (output truncated) ...\n\nfghijk",
          total_bytes: 11,
          truncated: true
        }
      })
    );

    expect(completed.terminalAppend).toBe("\n[completed]\n");
    const transcript = getTerminalSnapshot(completed.terminalTranscript);
    expect(countOccurrences(transcript, "abcde")).toBe(1);
    expect(countOccurrences(transcript, "fghijk")).toBe(1);
    expect(transcript).toContain("abcdefghijk\n[completed]");
  });

  it("marks a second gap when final output cannot cover the byte advance", () => {
    const firstMarker = "\n\n... (output truncated; 96 bytes unavailable) ...\n\n";
    const secondMarker = "\n\n... (output truncated; 100 bytes unavailable) ...\n\n";
    const created = reduceInspectorEvent(
      createInitialInspectorState(),
      sessionUpdate("tool_call", {
        sessionUpdate: "tool_call",
        toolCallId: "tool-second-gap",
        title: "Run burst output",
        kind: "execute",
        status: "pending",
        rawInput: { command: "burst-output" }
      })
    );
    const firstGap = reduceInspectorEvent(
      created,
      sessionUpdate("tool_call_update", {
        sessionUpdate: "tool_call_update",
        toolCallId: "tool-second-gap",
        status: "in_progress",
        rawOutput: {
          type: "Bash",
          output: [],
          output_delta: asciiBytes(`${firstMarker}tail`),
          total_bytes: 100,
          truncated: true
        }
      })
    );
    const completed = reduceInspectorEvent(
      firstGap,
      sessionUpdate("tool_call_update", {
        sessionUpdate: "tool_call_update",
        toolCallId: "tool-second-gap",
        status: "completed",
        rawOutput: {
          type: "Bash",
          output: asciiBytes("last"),
          output_for_prompt: "last",
          total_bytes: 200,
          truncated: true
        }
      })
    );

    expect(completed.terminalAppend).toBe(`${secondMarker}[completed]\n`);
    const transcript = getTerminalSnapshot(completed.terminalTranscript);
    expect(countOccurrences(transcript, "tail")).toBe(1);
    expect(countOccurrences(transcript, "last")).toBe(0);
    expect(countOccurrences(transcript, firstMarker)).toBe(1);
    expect(countOccurrences(transcript, secondMarker)).toBe(1);
  });

  it("ignores an empty Bash delta whose monotonic total regresses", () => {
    const created = reduceInspectorEvent(
      createInitialInspectorState(),
      sessionUpdate("tool_call", {
        sessionUpdate: "tool_call",
        toolCallId: "tool-regressed-total",
        title: "Run ordered output",
        kind: "execute",
        status: "pending",
        rawInput: { command: "ordered-output" }
      })
    );
    const first = reduceInspectorEvent(
      created,
      sessionUpdate("tool_call_update", {
        sessionUpdate: "tool_call_update",
        toolCallId: "tool-regressed-total",
        status: "in_progress",
        rawOutput: {
          type: "Bash",
          output: [],
          output_delta: asciiBytes("newest"),
          total_bytes: 10,
          truncated: false
        }
      })
    );
    const regressed = reduceInspectorEvent(
      first,
      sessionUpdate("tool_call_update", {
        sessionUpdate: "tool_call_update",
        toolCallId: "tool-regressed-total",
        status: "in_progress",
        content: [{ type: "content", content: { type: "text", text: "older" } }],
        rawOutput: {
          type: "Bash",
          output: [],
          output_delta: [],
          total_bytes: 9,
          truncated: false
        }
      })
    );
    const resumed = reduceInspectorEvent(
      regressed,
      sessionUpdate("tool_call_update", {
        sessionUpdate: "tool_call_update",
        toolCallId: "tool-regressed-total",
        status: "in_progress",
        rawOutput: {
          type: "Bash",
          output: [],
          output_delta: asciiBytes("!"),
          total_bytes: 11,
          truncated: false
        }
      })
    );

    expect(regressed.terminalRevision).toBe(first.terminalRevision);
    expect(getTerminalSnapshot(regressed.terminalTranscript)).not.toContain("older");
    expect(resumed.terminalAppend).toBe("!");
    expect(getTerminalSnapshot(resumed.terminalTranscript)).toContain("newest!");
  });

  it("falls back to a changed cumulative snapshot when an empty delta loses a rewrite", () => {
    const created = reduceInspectorEvent(
      createInitialInspectorState(),
      sessionUpdate("tool_call", {
        sessionUpdate: "tool_call",
        toolCallId: "tool-empty-delta",
        title: "Run rolling output",
        kind: "execute",
        status: "pending",
        rawInput: { command: "rolling-output" }
      })
    );
    const first = reduceInspectorEvent(
      created,
      sessionUpdate("tool_call_update", {
        sessionUpdate: "tool_call_update",
        toolCallId: "tool-empty-delta",
        status: "in_progress",
        rawOutput: {
          type: "Bash",
          output_for_prompt: "aaaa",
          output_delta: asciiBytes("aaaa")
        }
      })
    );
    const rewritten = reduceInspectorEvent(
      first,
      sessionUpdate("tool_call_update", {
        sessionUpdate: "tool_call_update",
        toolCallId: "tool-empty-delta",
        status: "in_progress",
        content: [
          { type: "content", content: { type: "text", text: "aaab" } }
        ],
        rawOutput: {
          type: "Bash",
          output_for_prompt: "aaab",
          output_delta: []
        }
      })
    );
    const finalSnapshot = reduceInspectorEvent(
      rewritten,
      sessionUpdate("tool_call_update", {
        sessionUpdate: "tool_call_update",
        toolCallId: "tool-empty-delta",
        status: "in_progress",
        content: [
          { type: "content", content: { type: "text", text: "aaab" } }
        ]
      })
    );

    expect(rewritten.terminalAppend).toBe("\naaab");
    expect(finalSnapshot.terminalRevision).toBe(rewritten.terminalRevision);
    expect(countOccurrences(
      getTerminalSnapshot(finalSnapshot.terminalTranscript),
      "aaab"
    )).toBe(1);
  });

  it("records a same-length cumulative rewrite instead of ignoring it", () => {
    const created = reduceInspectorEvent(
      createInitialInspectorState(),
      sessionUpdate("tool_call", {
        sessionUpdate: "tool_call",
        toolCallId: "tool-full-rewrite",
        title: "Run full snapshots",
        kind: "execute",
        status: "in_progress",
        rawInput: { command: "full-snapshots" },
        rawOutput: "aaaa"
      })
    );
    const rewritten = reduceInspectorEvent(
      created,
      sessionUpdate("tool_call_update", {
        sessionUpdate: "tool_call_update",
        toolCallId: "tool-full-rewrite",
        status: "in_progress",
        rawOutput: "aaab"
      })
    );

    expect(rewritten.terminalAppend).toBe("\naaab");
  });

  it("resets when a cumulative snapshot rewrites the prefix but repeats the old tail", () => {
    const repeatedTail = "tail".repeat(256);
    const oldOutput = `${"A".repeat(2_048)}${repeatedTail}`;
    const newOutput = `${"B".repeat(2_048)}${repeatedTail}Z`;
    const created = reduceInspectorEvent(
      createInitialInspectorState(),
      sessionUpdate("tool_call", {
        sessionUpdate: "tool_call",
        toolCallId: "tool-prefix-rewrite",
        title: "Run prefix rewrite",
        kind: "execute",
        status: "in_progress",
        rawInput: { command: "prefix-rewrite" },
        rawOutput: oldOutput
      })
    );
    const rewritten = reduceInspectorEvent(
      created,
      sessionUpdate("tool_call_update", {
        sessionUpdate: "tool_call_update",
        toolCallId: "tool-prefix-rewrite",
        status: "in_progress",
        rawOutput: newOutput
      })
    );

    expect(rewritten.terminalAppend).toBe(`\n${newOutput}`);
    expect(JSON.stringify(rewritten.toolCalls)).not.toContain(oldOutput);
    expect(JSON.stringify(rewritten.toolCalls)).not.toContain(newOutput);
  });

  it("records a same-length prefix rewrite when the old tail is unchanged", () => {
    const repeatedTail = "tail".repeat(256);
    const oldOutput = `${"A".repeat(2_048)}${repeatedTail}`;
    const newOutput = `${"B".repeat(2_048)}${repeatedTail}`;
    const created = reduceInspectorEvent(
      createInitialInspectorState(),
      sessionUpdate("tool_call", {
        sessionUpdate: "tool_call",
        toolCallId: "tool-same-length-prefix-rewrite",
        title: "Run same-length prefix rewrite",
        kind: "execute",
        status: "in_progress",
        rawInput: { command: "same-length-prefix-rewrite" },
        rawOutput: oldOutput
      })
    );
    const rewritten = reduceInspectorEvent(
      created,
      sessionUpdate("tool_call_update", {
        sessionUpdate: "tool_call_update",
        toolCallId: "tool-same-length-prefix-rewrite",
        status: "in_progress",
        rawOutput: newOutput
      })
    );

    expect(rewritten.terminalAppend).toBe(`\n${newOutput}`);
  });

  it("keeps raw delta offsets when a final full Bash buffer arrives", () => {
    const ansi = "\u001b[31mred\u001b[0m";
    const created = reduceInspectorEvent(
      createInitialInspectorState(),
      sessionUpdate("tool_call", {
        sessionUpdate: "tool_call",
        toolCallId: "tool-delta-final",
        title: "Run colors",
        kind: "execute",
        status: "pending",
        rawInput: { command: "colors" }
      })
    );
    const first = reduceInspectorEvent(
      created,
      sessionUpdate("tool_call_update", {
        sessionUpdate: "tool_call_update",
        toolCallId: "tool-delta-final",
        status: "in_progress",
        rawOutput: {
          type: "Bash",
          output: [],
          output_for_prompt: "red",
          output_delta: asciiBytes(ansi)
        }
      })
    );
    const completed = reduceInspectorEvent(
      first,
      sessionUpdate("tool_call_update", {
        sessionUpdate: "tool_call_update",
        toolCallId: "tool-delta-final",
        status: "completed",
        rawOutput: {
          type: "Bash",
          output: asciiBytes(`${ansi}done`),
          output_for_prompt: "reddone"
        }
      })
    );

    expect(first.terminalAppend).toBe(ansi);
    expect(completed.terminalAppend).toBe("done\n[completed]\n");
  });

  it("decodes a UTF-8 character split across Bash deltas", () => {
    const created = reduceInspectorEvent(
      createInitialInspectorState(),
      sessionUpdate("tool_call", {
        sessionUpdate: "tool_call",
        toolCallId: "tool-utf8",
        title: "Run UTF-8 output",
        kind: "execute",
        status: "pending",
        rawInput: { command: "utf8-output" }
      })
    );
    const first = reduceInspectorEvent(
      created,
      sessionUpdate("tool_call_update", {
        sessionUpdate: "tool_call_update",
        toolCallId: "tool-utf8",
        status: "in_progress",
        rawOutput: {
          type: "Bash",
          output_for_prompt: "",
          output_delta: [0xe4, 0xbd]
        }
      })
    );
    const second = reduceInspectorEvent(
      first,
      sessionUpdate("tool_call_update", {
        sessionUpdate: "tool_call_update",
        toolCallId: "tool-utf8",
        status: "completed",
        rawOutput: {
          type: "Bash",
          output_for_prompt: "你",
          output_delta: [0xa0]
        }
      })
    );

    expect(first.terminalRevision).toBe(created.terminalRevision);
    expect(getTerminalSnapshot(first.terminalTranscript)).not.toContain("�");
    expect(second.terminalAppend).toBe("你\n[completed]\n");
    expect(getTerminalSnapshot(second.terminalTranscript)).not.toContain("�");
  });

  it("flushes an incomplete UTF-8 sequence when execution completes", () => {
    const created = reduceInspectorEvent(
      createInitialInspectorState(),
      sessionUpdate("tool_call", {
        sessionUpdate: "tool_call",
        toolCallId: "tool-incomplete-utf8",
        title: "Run truncated UTF-8",
        kind: "execute",
        status: "pending",
        rawInput: { command: "truncated-utf8" }
      })
    );
    const partial = reduceInspectorEvent(
      created,
      sessionUpdate("tool_call_update", {
        sessionUpdate: "tool_call_update",
        toolCallId: "tool-incomplete-utf8",
        status: "in_progress",
        rawOutput: {
          type: "Bash",
          output_for_prompt: "",
          output_delta: [0xe4, 0xbd]
        }
      })
    );
    const completed = reduceInspectorEvent(
      partial,
      sessionUpdate("tool_call_update", {
        sessionUpdate: "tool_call_update",
        toolCallId: "tool-incomplete-utf8",
        status: "completed"
      })
    );

    expect(completed.terminalAppend).toBe("�\n[completed]\n");
    expect(completed.toolCalls["tool-incomplete-utf8"].pendingUtf8Bytes).toEqual([]);
  });

  it("preserves multiple orphan Bash deltas until the base tool arrives", () => {
    const firstOrphan = reduceInspectorEvent(
      createInitialInspectorState(),
      sessionUpdate("tool_call_update", {
        sessionUpdate: "tool_call_update",
        toolCallId: "tool-orphan-stream",
        status: "in_progress",
        rawOutput: {
          type: "Bash",
          output_for_prompt: "a",
          output_delta: [97]
        }
      })
    );
    const secondOrphan = reduceInspectorEvent(
      firstOrphan,
      sessionUpdate("tool_call_update", {
        sessionUpdate: "tool_call_update",
        toolCallId: "tool-orphan-stream",
        status: "completed",
        rawOutput: {
          type: "Bash",
          output_for_prompt: "ab",
          output_delta: [98]
        }
      })
    );
    const merged = reduceInspectorEvent(
      secondOrphan,
      sessionUpdate("tool_call", {
        sessionUpdate: "tool_call",
        toolCallId: "tool-orphan-stream",
        title: "Run orphan stream",
        kind: "execute",
        status: "pending",
        rawInput: { command: "orphan-stream" }
      })
    );

    const terminal = getTerminalSnapshot(merged.terminalTranscript);
    expect(terminal).toContain("> orphan-stream\nab\n[completed]");
  });

  it("separates output after an execution buffer shrinks", () => {
    const created = reduceInspectorEvent(
      createInitialInspectorState(),
      sessionUpdate("tool_call", {
        sessionUpdate: "tool_call",
        toolCallId: "tool-reset",
        title: "Run watch",
        kind: "execute",
        status: "in_progress",
        rawInput: { command: "watch" }
      })
    );
    const streamed = reduceInspectorEvent(
      created,
      sessionUpdate("tool_call_update", {
        sessionUpdate: "tool_call_update",
        toolCallId: "tool-reset",
        content: [
          { type: "content", content: { type: "text", text: "old buffer" } }
        ]
      })
    );
    const reset = reduceInspectorEvent(
      streamed,
      sessionUpdate("tool_call_update", {
        sessionUpdate: "tool_call_update",
        toolCallId: "tool-reset",
        status: "completed",
        content: [
          { type: "content", content: { type: "text", text: "new" } }
        ]
      })
    );

    expect(reset.terminalAppend).toBe("\nnew\n[completed]\n");
    expect(getTerminalSnapshot(reset.terminalTranscript)).toContain(
      "old buffer\nnew\n[completed]"
    );
  });

  it("keeps a bounded paged terminal snapshot", () => {
    let state = createInitialInspectorState();
    const output = "x".repeat(65_536);

    for (let index = 0; index < 80; index += 1) {
      state = reduceInspectorEvent(
        state,
        sessionUpdate("tool_call", {
          sessionUpdate: "tool_call",
          toolCallId: `tool-${index}`,
          title: `Run ${index}`,
          kind: "execute",
          status: "completed",
          rawInput: { command: "echo", args: [index] },
          rawOutput: output
        })
      );
    }

    expect(state.terminalTranscript.characterCount).toBeLessThanOrEqual(
      TERMINAL_TRANSCRIPT_MAX_CHARS
    );
    expect(getTerminalSnapshot(state.terminalTranscript).length).toBe(
      state.terminalTranscript.characterCount
    );
    expect(state.terminalTranscript.pages.length).toBeLessThanOrEqual(130);
  });

  it("bounds one oversized append and the complete terminal state", () => {
    const oversized = "z".repeat(TERMINAL_TRANSCRIPT_MAX_CHARS * 3);
    const state = reduceInspectorEvent(
      createInitialInspectorState(),
      sessionUpdate("tool_call", {
        sessionUpdate: "tool_call",
        toolCallId: "tool-oversized",
        title: "Run oversized output",
        kind: "execute",
        status: "in_progress",
        rawInput: { command: "oversized-output" },
        rawOutput: oversized
      })
    );

    expect(state.terminalAppend.length).toBeLessThanOrEqual(
      TERMINAL_TRANSCRIPT_MAX_CHARS
    );
    expect(state.terminalTranscript.characterCount).toBeLessThanOrEqual(
      TERMINAL_TRANSCRIPT_MAX_CHARS
    );
    expect(JSON.stringify(state).length).toBeLessThan(
      TERMINAL_TRANSCRIPT_MAX_CHARS * 2 + 16_384
    );
  });

  it("shares one bounded transcript budget across orphan tools", () => {
    let state = createInitialInspectorState();
    const chunk = "o".repeat(70_000);
    for (let index = 0; index < 80; index += 1) {
      state = reduceInspectorEvent(
        state,
        sessionUpdate("tool_call_update", {
          sessionUpdate: "tool_call_update",
          toolCallId: `orphan-${index}`,
          status: "in_progress",
          rawOutput: {
            type: "Bash",
            output_for_prompt: chunk,
            output_delta: asciiBytes(chunk)
          }
        })
      );
    }

    const orphans = Object.values(state.orphanToolUpdates);
    expect(orphans.length).toBeLessThanOrEqual(64);
    expect(orphans.reduce(
      (total, orphan) => total + orphan.terminalTranscript.characterCount,
      0
    )).toBeLessThanOrEqual(TERMINAL_TRANSCRIPT_MAX_CHARS);
  });

  it("whitelists and bounds fields buffered for an orphan tool update", () => {
    const oversized = "private-marker-".repeat(600_000);
    const state = reduceInspectorEvent(
      createInitialInspectorState(),
      sessionUpdate("tool_call_update", {
        sessionUpdate: "tool_call_update",
        toolCallId: "orphan-untrusted-fields",
        title: "T".repeat(100_000),
        kind: "execute",
        status: "in_progress",
        rawInput: { command: oversized },
        arbitrary: { nested: oversized }
      })
    );

    const fields = state.orphanToolUpdates["orphan-untrusted-fields"]?.fields;
    expect(fields).toBeDefined();
    expect(Object.keys(fields ?? {}).sort()).toEqual(["kind", "status", "title"]);
    expect(String(fields?.title).length).toBeLessThan(100_000);
    expect(JSON.stringify(state.orphanToolUpdates)).not.toContain("private-marker-");
  });

  it("shares one complete four MiB budget across orphan transcripts and diffs", () => {
    let state = createInitialInspectorState();
    const transcript = "output".repeat(180_000);
    const diffText = "change".repeat(160_000);

    for (let index = 0; index < 2; index += 1) {
      state = reduceInspectorEvent(
        state,
        sessionUpdate("tool_call_update", {
          sessionUpdate: "tool_call_update",
          toolCallId: `orphan-complete-budget-${index}`,
          title: `Orphan ${index}`,
          status: "in_progress",
          rawOutput: {
            type: "Bash",
            output_for_prompt: transcript,
            output_delta: asciiBytes(transcript)
          },
          content: [
            {
              type: "diff",
              path: `src/large-${index}.txt`,
              oldText: diffText,
              newText: diffText
            }
          ]
        })
      );
    }

    expect(JSON.stringify(state.orphanToolUpdates).length).toBeLessThanOrEqual(
      TERMINAL_TRANSCRIPT_MAX_CHARS
    );
  });

  it("does not retain one orphan diff larger than the global budget", () => {
    const oversizedDiff = "diff-marker-".repeat(750_000);
    const state = reduceInspectorEvent(
      createInitialInspectorState(),
      sessionUpdate("tool_call_update", {
        sessionUpdate: "tool_call_update",
        toolCallId: "orphan-oversized-diff",
        status: "in_progress",
        content: [
          {
            type: "diff",
            path: "src/oversized.txt",
            oldText: oversizedDiff,
            newText: oversizedDiff
          }
        ]
      })
    );

    expect(JSON.stringify(state.orphanToolUpdates)).not.toContain("diff-marker-");
    expect(JSON.stringify(state.orphanToolUpdates).length).toBeLessThanOrEqual(
      TERMINAL_TRANSCRIPT_MAX_CHARS
    );
  });

  it("keeps one hundred thousand cumulative lines incremental and bounded", () => {
    let state = reduceInspectorEvent(
      createInitialInspectorState(),
      sessionUpdate("tool_call", {
        sessionUpdate: "tool_call",
        toolCallId: "tool-large-stream",
        title: "Run large stream",
        kind: "execute",
        status: "pending",
        rawInput: { command: "large-stream" }
      })
    );
    const chunk = `${"x".repeat(48)}\n`.repeat(5_000);
    let cumulative = "";

    for (let batch = 0; batch < 20; batch += 1) {
      cumulative += chunk;
      state = reduceInspectorEvent(
        state,
        sessionUpdate("tool_call_update", {
          sessionUpdate: "tool_call_update",
          toolCallId: "tool-large-stream",
          status: "in_progress",
          content: [
            { type: "content", content: { type: "text", text: cumulative } }
          ]
        })
      );
      expect(state.terminalAppend).toBe(chunk);
    }

    expect(state.terminalRevision).toBe(21);
    expect(state.terminalTranscript.characterCount).toBe(
      TERMINAL_TRANSCRIPT_MAX_CHARS
    );
    expect(state.terminalTranscript.pages.length).toBeLessThanOrEqual(130);
    expect(JSON.stringify(state.toolCalls).length).toBeLessThan(2_048);
    expect(state.toolCalls["tool-large-stream"]).not.toHaveProperty("content");
    expect(state.toolCalls["tool-large-stream"]).not.toHaveProperty("rawOutput");
    expect(state.toolCalls["tool-large-stream"]).not.toHaveProperty("terminalOutput");
  });

  it("replaces the complete ACP plan and ignores updates from another session", () => {
    const running = reduceInspectorEvent(
      createInitialInspectorState(),
      {
        type: "engine/status",
        status: "running",
        sessionId: "session-42"
      }
    );
    const planned = reduceInspectorEvent(
      running,
      sessionUpdate("plan", {
        sessionUpdate: "plan",
        entries: [
          { content: "建立双 WebView 壳", priority: "high", status: "in_progress" },
          { content: "运行双架构构建", priority: "medium", status: "pending" }
        ]
      })
    );
    const ignored = reduceInspectorEvent(
      planned,
      sessionUpdate("plan", {
        sessionUpdate: "plan",
        entries: [{ content: "不应显示", priority: "low", status: "completed" }]
      }, "session-other")
    );

    expect(ignored.plan).toEqual([
      { content: "建立双 WebView 壳", priority: "high", status: "in_progress" },
      { content: "运行双架构构建", priority: "medium", status: "pending" }
    ]);
  });
});

function sessionUpdate(
  updateKind: string,
  update: unknown,
  sessionId = "session-42"
): HostEvent {
  return {
    type: "session/update",
    sessionId,
    updateKind,
    engineEpoch: 0,
    update
  };
}

function asciiBytes(text: string): number[] {
  return Array.from(text, (character) => character.charCodeAt(0));
}

function countOccurrences(text: string, value: string): number {
  return text.split(value).length - 1;
}
