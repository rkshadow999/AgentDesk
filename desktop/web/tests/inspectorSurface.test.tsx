import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { InspectorSurface } from "../src/InspectorSurface";
import type {
  DiffViewer,
  InspectorRuntime,
  TerminalViewer
} from "../src/inspectorRuntime";
import type { HostBridge, HostCommand, HostEvent } from "../src/hostBridge";

describe("InspectorSurface", () => {
  it("renders real diff, terminal, and plan tabs in Chinese", () => {
    render(<InspectorSurface bridge={new RecordingBridge()} runtime={new RecordingRuntime()} />);

    expect(screen.getByRole("tab", { name: "更改" })).toBeInTheDocument();
    expect(screen.getByRole("tab", { name: "终端" })).toBeInTheDocument();
    expect(screen.getByRole("tab", { name: "计划" })).toBeInTheDocument();
    expect(screen.getByText("暂无可审阅更改")).toBeInTheDocument();
  });

  it("switches inspector resources when the saved UI language changes", () => {
    const bridge = new RecordingBridge();
    render(<InspectorSurface bridge={bridge} runtime={new RecordingRuntime()} />);

    act(() => bridge.emit({
      type: "ui/preferences/changed",
      language: "en-US",
      composerDraft: "",
      sessionMode: "default",
      executionProfile: "NativeProtected",
      notificationsEnabled: false,
      windowsAutomationEnabled: false,
      backgroundUpdateChecksEnabled: false,
      fullAccessEnabled: false,
      fontScalePercent: 110,
      restartRequired: false
    }));

    expect(screen.getByRole("main", { name: "Inspector" })).toBeInTheDocument();
    expect(screen.getByRole("tab", { name: "Changes" })).toBeInTheDocument();
    expect(screen.getByRole("tab", { name: "Terminal" })).toBeInTheDocument();
    expect(screen.getByRole("tab", { name: "Plan" })).toBeInTheDocument();
    expect(screen.getByText("No changes to review")).toBeInTheDocument();
  });

  it("updates both inspector typography engines without remounting them", async () => {
    const bridge = new RecordingBridge();
    const runtime = new RecordingRuntime();
    render(<InspectorSurface bridge={bridge} runtime={runtime} />);

    act(() => bridge.emit(sessionUpdate("diff_review", {
      sessionUpdate: "diff_review",
      content: [{ type: "diff", path: "src/App.cs", oldText: "old", newText: "new" }]
    })));
    fireEvent.click(screen.getByRole("tab", { name: "终端" }));
    await waitFor(() => expect(runtime.mountTerminal).toHaveBeenCalledTimes(1));

    act(() => bridge.emit({
      type: "ui/preferences/changed",
      language: "zh-CN",
      composerDraft: "",
      sessionMode: "default",
      executionProfile: "NativeProtected",
      notificationsEnabled: false,
      windowsAutomationEnabled: false,
      backgroundUpdateChecksEnabled: false,
      fullAccessEnabled: false,
      fontScalePercent: 140,
      restartRequired: false
    }));

    expect(document.documentElement.style.fontSize).toBe("14px");
    expect(runtime.diffViewer.setFontSize).toHaveBeenCalledWith(16.8);
    expect(runtime.diffViewer.layout).toHaveBeenCalled();
    expect(runtime.terminalViewer.setFontSize).toHaveBeenCalledWith(16.8);
    expect(runtime.terminalViewer.fit).toHaveBeenCalled();
    expect(runtime.mountDiff).toHaveBeenCalledTimes(1);
    expect(runtime.mountTerminal).toHaveBeenCalledTimes(1);
  });

  it("moves between inspector tabs with the keyboard", () => {
    render(<InspectorSurface bridge={new RecordingBridge()} runtime={new RecordingRuntime()} />);
    const changesTab = screen.getByRole("tab", { name: "更改" });
    const terminalTab = screen.getByRole("tab", { name: "终端" });
    changesTab.focus();

    fireEvent.keyDown(changesTab, { key: "ArrowRight" });

    expect(terminalTab).toHaveAttribute("aria-selected", "true");
    expect(terminalTab).toHaveFocus();
  });

  it("hides the changes empty panel when Plan or Terminal is selected", () => {
    // Regression: nesting empty copy inside the changes panel shell so
    // `.inspector-empty { display:grid }` cannot un-hide an inactive tab.
    render(<InspectorSurface bridge={new RecordingBridge()} runtime={new RecordingRuntime()} />);

    const changesPanel = screen.getByTestId("inspector-panel-changes");
    const planPanel = screen.getByTestId("inspector-panel-plan");
    const terminalPanel = screen.getByTestId("inspector-panel-terminal");
    expect(changesPanel).toHaveClass("is-active");
    expect(screen.getByText("暂无可审阅更改")).toBeInTheDocument();

    fireEvent.click(screen.getByRole("tab", { name: "计划" }));
    expect(screen.getByRole("tab", { name: "计划" })).toHaveAttribute("aria-selected", "true");
    expect(planPanel).toHaveClass("is-active");
    expect(changesPanel).not.toHaveClass("is-active");
    expect(changesPanel).toHaveAttribute("aria-hidden", "true");
    expect(planPanel).toHaveAttribute("aria-hidden", "false");
    expect(screen.getByText("当前任务尚未生成计划")).toBeInTheDocument();

    fireEvent.click(screen.getByRole("tab", { name: "终端" }));
    expect(terminalPanel).toHaveClass("is-active");
    expect(planPanel).not.toHaveClass("is-active");
    expect(changesPanel).not.toHaveClass("is-active");
    expect(screen.getByText("暂无终端输出")).toBeInTheDocument();
  });

  it("resets inspector projection when the active session changes", () => {
    const bridge = new RecordingBridge();
    render(<InspectorSurface bridge={bridge} runtime={new RecordingRuntime()} />);

    act(() => bridge.emit(sessionUpdate("diff_review", {
      sessionUpdate: "diff_review",
      content: [{ type: "diff", path: "src/A.cs", oldText: "a", newText: "b" }]
    }, "session-a")));
    expect(screen.getByRole("button", { name: "src/A.cs" })).toBeInTheDocument();

    act(() => bridge.emit({
      type: "session/active/changed",
      sessionId: "session-b",
      workspacePath: "C:\\workspace",
      engineEpoch: 2
    }));

    expect(screen.queryByRole("button", { name: "src/A.cs" })).not.toBeInTheDocument();
    expect(screen.getByText("暂无可审阅更改")).toBeInTheDocument();
  });

  it("keeps terminal and plan tabs visible after the user pins them while diffs stream in", async () => {
    const bridge = new RecordingBridge();
    const runtime = new RecordingRuntime();
    render(<InspectorSurface bridge={bridge} runtime={runtime} />);

    act(() => bridge.emit(sessionUpdate("diff_review", {
      sessionUpdate: "diff_review",
      content: [{ type: "diff", path: "src/A.cs", oldText: "a", newText: "b" }]
    })));
    expect(screen.getByRole("tab", { name: "更改" })).toHaveAttribute("aria-selected", "true");
    expect(screen.getByTestId("inspector-panel-changes")).toHaveClass("is-active", "changes-layout");

    fireEvent.click(screen.getByRole("tab", { name: "终端" }));
    expect(screen.getByRole("tab", { name: "终端" })).toHaveAttribute("aria-selected", "true");
    expect(screen.getByTestId("inspector-panel-terminal")).toHaveClass("is-active");
    expect(screen.getByTestId("inspector-panel-changes")).not.toHaveClass("is-active");

    // New diffs must not force the user off the Terminal tab.
    act(() => bridge.emit(sessionUpdate("diff_review", {
      sessionUpdate: "diff_review",
      content: [{ type: "diff", path: "src/B.cs", oldText: "c", newText: "d" }]
    })));
    expect(screen.getByRole("tab", { name: "终端" })).toHaveAttribute("aria-selected", "true");
    expect(screen.getByTestId("inspector-panel-terminal")).toHaveClass("is-active");

    fireEvent.click(screen.getByRole("tab", { name: "计划" }));
    act(() => bridge.emit(sessionUpdate("plan", {
      sessionUpdate: "plan",
      entries: [
        { content: "检查终端面板", priority: "high", status: "in_progress" }
      ]
    })));
    expect(screen.getByRole("tab", { name: "计划" })).toHaveAttribute("aria-selected", "true");
    expect(screen.getByText("检查终端面板")).toBeInTheDocument();
    expect(screen.getByTestId("inspector-panel-plan")).toHaveClass("is-active");
    expect(screen.getByTestId("inspector-panel-changes")).not.toHaveClass("is-active");
  });

  it("mounts Monaco with the selected real diff", async () => {
    const bridge = new RecordingBridge();
    const runtime = new RecordingRuntime();
    render(<InspectorSurface bridge={bridge} runtime={runtime} />);

    act(() => bridge.emit(sessionUpdate("diff_review", {
      sessionUpdate: "diff_review",
      content: [
        { type: "diff", path: "src/App.cs", oldText: "old", newText: "new" }
      ]
    })));

    expect(screen.getByRole("button", { name: "src/App.cs" })).toBeInTheDocument();
    await waitFor(() => expect(runtime.diffViewer.setDiff).toHaveBeenCalledWith({
      path: "src/App.cs",
      oldText: "old",
      newText: "new"
    }));
  });

  it("writes execution updates into xterm and shows the current ACP plan", async () => {
    const bridge = new RecordingBridge();
    const runtime = new RecordingRuntime();
    render(<InspectorSurface bridge={bridge} runtime={runtime} />);

    act(() => {
      bridge.emit(sessionUpdate("tool_call", {
        sessionUpdate: "tool_call",
        toolCallId: "tool-1",
        title: "运行测试",
        kind: "execute",
        status: "completed",
        rawInput: { command: "npm", args: ["test"] },
        rawOutput: "21 tests passed"
      }));
    });
    fireEvent.click(screen.getByRole("tab", { name: "终端" }));
    await waitFor(() => expect(runtime.terminalViewer.replaceText).toHaveBeenCalledWith(
      expect.stringContaining("21 tests passed")
    ));

    act(() => bridge.emit(sessionUpdate("plan", {
      sessionUpdate: "plan",
      entries: [
        { content: "接入 Inspector", priority: "high", status: "completed" },
        { content: "验证 ARM64", priority: "medium", status: "in_progress" }
      ]
    })));
    fireEvent.click(screen.getByRole("tab", { name: "计划" }));

    expect(screen.getByText("接入 Inspector")).toBeInTheDocument();
    expect(screen.getByText("验证 ARM64")).toBeInTheDocument();
    expect(screen.getByText("已完成")).toBeInTheDocument();
    expect(screen.getByText("进行中")).toBeInTheDocument();
  });

  it("sends only terminal deltas after the initial snapshot", async () => {
    const bridge = new RecordingBridge();
    const runtime = new RecordingRuntime();
    render(<InspectorSurface bridge={bridge} runtime={runtime} />);

    act(() => bridge.emit(sessionUpdate("tool_call", {
      sessionUpdate: "tool_call",
      toolCallId: "tool-stream",
      title: "Run tests",
      kind: "execute",
      status: "in_progress",
      rawInput: { command: "npm", args: ["test"] }
    })));
    act(() => bridge.emit(sessionUpdate("tool_call_update", {
      sessionUpdate: "tool_call_update",
      toolCallId: "tool-stream",
      content: [
        { type: "content", content: { type: "text", text: "first line\n" } }
      ]
    })));
    fireEvent.click(screen.getByRole("tab", { name: "终端" }));
    await waitFor(() => expect(runtime.terminalViewer.replaceText).toHaveBeenCalledWith(
      expect.stringContaining("first line")
    ));
    vi.mocked(runtime.terminalViewer.appendText).mockClear();

    act(() => bridge.emit(sessionUpdate("tool_call_update", {
      sessionUpdate: "tool_call_update",
      toolCallId: "tool-stream",
      status: "completed",
      content: [
        {
          type: "content",
          content: { type: "text", text: "first line\nsecond line\n" }
        }
      ]
    })));

    await waitFor(() => expect(runtime.terminalViewer.appendText).toHaveBeenCalledWith(
      "second line\n[completed]\n"
    ));
    expect(runtime.terminalViewer.appendText).toHaveBeenCalledTimes(1);
  });

  it("replaces the transcript when batched terminal updates skip a revision", async () => {
    const bridge = new RecordingBridge();
    const runtime = new RecordingRuntime();
    render(<InspectorSurface bridge={bridge} runtime={runtime} />);

    act(() => bridge.emit(sessionUpdate("tool_call", {
      sessionUpdate: "tool_call",
      toolCallId: "tool-batched-stream",
      title: "Run batched stream",
      kind: "execute",
      status: "pending",
      rawInput: { command: "batched-stream" }
    })));
    fireEvent.click(screen.getByRole("tab", { name: "终端" }));
    await waitFor(() => expect(runtime.terminalViewer.replaceText).toHaveBeenCalledTimes(1));
    vi.mocked(runtime.terminalViewer.replaceText).mockClear();
    vi.mocked(runtime.terminalViewer.appendText).mockClear();

    act(() => {
      bridge.emit(sessionUpdate("tool_call_update", {
        sessionUpdate: "tool_call_update",
        toolCallId: "tool-batched-stream",
        status: "in_progress",
        content: [
          { type: "content", content: { type: "text", text: "one\n" } }
        ]
      }));
      bridge.emit(sessionUpdate("tool_call_update", {
        sessionUpdate: "tool_call_update",
        toolCallId: "tool-batched-stream",
        status: "in_progress",
        content: [
          { type: "content", content: { type: "text", text: "one\ntwo\n" } }
        ]
      }));
    });

    await waitFor(() => expect(runtime.terminalViewer.replaceText).toHaveBeenCalledTimes(1));
    expect(runtime.terminalViewer.appendText).not.toHaveBeenCalled();
    const snapshot = vi.mocked(runtime.terminalViewer.replaceText).mock.calls[0][0];
    expect(countOccurrences(snapshot, "one\n")).toBe(1);
    expect(countOccurrences(snapshot, "two\n")).toBe(1);
  });

  it("replaces the terminal with the current transcript when an append is rejected", async () => {
    const bridge = new RecordingBridge();
    const runtime = new RecordingRuntime();
    render(<InspectorSurface bridge={bridge} runtime={runtime} />);

    act(() => bridge.emit(sessionUpdate("tool_call", {
      sessionUpdate: "tool_call",
      toolCallId: "tool-backpressure",
      title: "Run buffered command",
      kind: "execute",
      status: "in_progress",
      rawInput: { command: "buffered-command" }
    })));
    act(() => bridge.emit(sessionUpdate("tool_call_update", {
      sessionUpdate: "tool_call_update",
      toolCallId: "tool-backpressure",
      content: [
        { type: "content", content: { type: "text", text: "first line\n" } }
      ]
    })));
    fireEvent.click(screen.getByRole("tab", { name: "终端" }));
    await waitFor(() => expect(runtime.terminalViewer.replaceText).toHaveBeenCalledTimes(1));
    vi.mocked(runtime.terminalViewer.replaceText).mockClear();
    vi.mocked(runtime.terminalViewer.appendText).mockReturnValueOnce(false);

    act(() => bridge.emit(sessionUpdate("tool_call_update", {
      sessionUpdate: "tool_call_update",
      toolCallId: "tool-backpressure",
      content: [
        {
          type: "content",
          content: { type: "text", text: "first line\nsecond line\n" }
        }
      ]
    })));

    await waitFor(() => expect(runtime.terminalViewer.appendText).toHaveBeenCalledWith(
      "second line\n"
    ));
    expect(runtime.terminalViewer.replaceText).toHaveBeenCalledTimes(1);
    const snapshot = vi.mocked(runtime.terminalViewer.replaceText).mock.calls[0][0];
    expect(countOccurrences(snapshot, "first line\n")).toBe(1);
    expect(countOccurrences(snapshot, "second line\n")).toBe(1);
  });

  it("replays the latest snapshot once when terminal mounting is delayed", async () => {
    const bridge = new RecordingBridge();
    const runtime = new DeferredTerminalRuntime();
    render(<InspectorSurface bridge={bridge} runtime={runtime} />);

    act(() => {
      bridge.emit(sessionUpdate("tool_call", {
        sessionUpdate: "tool_call",
        toolCallId: "tool-delayed",
        title: "Run delayed stream",
        kind: "execute",
        status: "pending",
        rawInput: { command: "delayed-stream" }
      }));
      bridge.emit(sessionUpdate("tool_call_update", {
        sessionUpdate: "tool_call_update",
        toolCallId: "tool-delayed",
        status: "in_progress",
        content: [
          { type: "content", content: { type: "text", text: "first\n" } }
        ]
      }));
    });
    fireEvent.click(screen.getByRole("tab", { name: "终端" }));
    await waitFor(() => expect(runtime.mountTerminal).toHaveBeenCalledTimes(1));

    act(() => bridge.emit(sessionUpdate("tool_call_update", {
      sessionUpdate: "tool_call_update",
      toolCallId: "tool-delayed",
      status: "in_progress",
      content: [
        { type: "content", content: { type: "text", text: "first\nsecond\n" } }
      ]
    })));
    const viewer = recordingTerminalViewer();
    await act(async () => runtime.resolveNext(viewer));

    expect(viewer.replaceText).toHaveBeenCalledTimes(1);
    expect(viewer.appendText).not.toHaveBeenCalled();
    const snapshot = vi.mocked(viewer.replaceText).mock.calls[0][0];
    expect(countOccurrences(snapshot, "first\n")).toBe(1);
    expect(countOccurrences(snapshot, "second\n")).toBe(1);
  });

  it("disposes a delayed terminal from the previous session", async () => {
    const bridge = new RecordingBridge();
    const runtime = new DeferredTerminalRuntime();
    render(<InspectorSurface bridge={bridge} runtime={runtime} />);

    act(() => {
      bridge.emit({ type: "engine/status", status: "running", sessionId: "session-old" });
      bridge.emit(sessionUpdate("tool_call", {
        sessionUpdate: "tool_call",
        toolCallId: "tool-old",
        title: "Run old",
        kind: "execute",
        status: "completed",
        rawInput: { command: "old" },
        rawOutput: "old output"
      }, "session-old"));
    });
    fireEvent.click(screen.getByRole("tab", { name: "终端" }));
    await waitFor(() => expect(runtime.mountTerminal).toHaveBeenCalledTimes(1));

    act(() => {
      bridge.emit({ type: "engine/status", status: "starting", sessionId: "session-new" });
      bridge.emit(sessionUpdate("tool_call", {
        sessionUpdate: "tool_call",
        toolCallId: "tool-new",
        title: "Run new",
        kind: "execute",
        status: "completed",
        rawInput: { command: "new" },
        rawOutput: "new output"
      }, "session-new"));
    });
    await waitFor(() => expect(runtime.mountTerminal).toHaveBeenCalledTimes(2));

    const oldViewer = recordingTerminalViewer();
    await act(async () => runtime.resolveNext(oldViewer));
    expect(oldViewer.dispose).toHaveBeenCalledTimes(1);
    expect(oldViewer.replaceText).not.toHaveBeenCalled();
    expect(oldViewer.appendText).not.toHaveBeenCalled();

    const newViewer = recordingTerminalViewer();
    await act(async () => runtime.resolveNext(newViewer));
    expect(newViewer.replaceText).toHaveBeenCalledTimes(1);
    expect(newViewer.replaceText).toHaveBeenCalledWith(expect.stringContaining("new output"));
    expect(newViewer.replaceText).not.toHaveBeenCalledWith(expect.stringContaining("old output"));
  });
});

class RecordingRuntime implements InspectorRuntime {
  readonly diffViewer: DiffViewer = {
    setDiff: vi.fn(),
    setFontSize: vi.fn(),
    layout: vi.fn(),
    dispose: vi.fn()
  };

  readonly terminalViewer: TerminalViewer = {
    appendText: vi.fn(() => true),
    replaceText: vi.fn(),
    setFontSize: vi.fn(),
    fit: vi.fn(),
    dispose: vi.fn()
  };

  mountDiff = vi.fn(async () => this.diffViewer);

  mountTerminal = vi.fn(async () => this.terminalViewer);
}

class RecordingBridge implements HostBridge {
  readonly available = true;
  readonly commands: HostCommand[] = [];
  private readonly listeners = new Set<(event: HostEvent) => void>();

  send(command: HostCommand): void {
    this.commands.push(command);
  }

  subscribe(listener: (event: HostEvent) => void): () => void {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }

  emit(event: HostEvent): void {
    for (const listener of this.listeners) {
      listener(event);
    }
  }
}

class DeferredTerminalRuntime implements InspectorRuntime {
  private readonly terminalResolvers: Array<(viewer: TerminalViewer) => void> = [];

  mountDiff = vi.fn(async () => ({
    setDiff: vi.fn(),
    setFontSize: vi.fn(),
    layout: vi.fn(),
    dispose: vi.fn()
  }));

  mountTerminal = vi.fn(() => new Promise<TerminalViewer>((resolve) => {
    this.terminalResolvers.push(resolve);
  }));

  resolveNext(viewer: TerminalViewer): void {
    const resolve = this.terminalResolvers.shift();
    expect(resolve).toBeDefined();
    resolve?.(viewer);
  }
}

function recordingTerminalViewer(): TerminalViewer {
  return {
    appendText: vi.fn(() => true),
    replaceText: vi.fn(),
    setFontSize: vi.fn(),
    fit: vi.fn(),
    dispose: vi.fn()
  };
}

function countOccurrences(text: string, value: string): number {
  return text.split(value).length - 1;
}

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
