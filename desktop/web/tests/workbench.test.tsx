import { act, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { Workbench } from "../src/Workbench";
import type { HostBridge, HostCommand, HostEvent } from "../src/hostBridge";
import zhCn from "../src/locales/zh-CN.json";
import { VirtualizedList } from "../src/VirtualizedList";

async function waitForFocusRestore() {
  await act(async () => {
    await Promise.resolve();
  });
}

const maintenanceRequestIdPattern =
  /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/u;

describe("Workbench", () => {
  it("renders the real session navigation without preview fixtures", () => {
    render(<Workbench />);

    expect(screen.getByLabelText("AgentDesk")).toBeInTheDocument();
    expect(screen.getByRole("navigation", { name: "会话导航" })).toBeInTheDocument();
    expect(screen.getByRole("main", { name: "对话" })).toBeInTheDocument();
    expect(screen.queryByRole("complementary", { name: "检查器" })).not.toBeInTheDocument();
    expect(screen.getByPlaceholderText("向 AgentDesk 描述任务")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "本机兼容（非沙箱）" })).toHaveAttribute(
      "aria-pressed",
      "true"
    );
    expect(screen.getByRole("button", { name: "WSL2 严格模式" })).toBeEnabled();
    expect(screen.queryByRole("button", { name: "新建任务" })).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "智能体" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "自动化" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "通知" })).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "附加图片" })).toBeEnabled();
    expect(screen.queryByText("全部任务")).not.toBeInTheDocument();
    expect(screen.getByText("没有会话")).toBeInTheDocument();
    expect(screen.queryByText("实现 Windows 登录窗口")).not.toBeInTheDocument();
    expect(screen.queryByText("补充 ARM64 构建流程")).not.toBeInTheDocument();
  });

  it("settles the session center to an empty state in preview mode", () => {
    render(<Workbench />);

    expect(screen.getByText("没有会话")).toBeInTheDocument();
    expect(screen.queryByText("正在加载会话")).not.toBeInTheDocument();
  });

  it("opens the runtime dashboard and controls authoritative tasks and subagents", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);
    act(() => bridge.emit({
      type: "engine/status",
      status: "ready",
      sessionId: "session-42"
    }));

    fireEvent.click(screen.getByRole("button", { name: "智能体" }));

    expect(screen.getByRole("main", { name: "智能体活动" })).toBeInTheDocument();
    expect(bridge.commands).toContainEqual({
      type: "runtime/dashboard/refresh",
      sessionId: "session-42"
    });

    act(() => bridge.emit(runtimeDashboardChanged()));

    expect(screen.getByRole("heading", { name: "后台任务" })).toBeInTheDocument();
    expect(screen.getByText("dotnet test")).toBeInTheDocument();
    expect(screen.getByText("运行桌面测试")).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "终止后台任务：dotnet test" }));
    expect(bridge.commands).toContainEqual({
      type: "runtime/task/kill",
      sessionId: "session-42",
      taskId: "task-7"
    });

    fireEvent.click(screen.getByRole("button", { name: "查看智能体：运行桌面测试" }));
    expect(bridge.commands).toContainEqual({
      type: "runtime/subagent/get",
      sessionId: "session-42",
      subagentId: "subagent-7"
    });

    fireEvent.click(screen.getByRole("button", { name: "取消智能体：运行桌面测试" }));
    expect(bridge.commands).toContainEqual({
      type: "runtime/subagent/cancel",
      sessionId: "session-42",
      subagentId: "subagent-7"
    });
  });

  it("keeps runtime polling single-flight until the host responds", () => {
    vi.useFakeTimers();
    try {
      const bridge = new RecordingBridge();
      render(<Workbench bridge={bridge} />);
      act(() => bridge.emit({
        type: "engine/status",
        status: "ready",
        sessionId: "session-42"
      }));
      fireEvent.click(screen.getByRole("button", { name: "智能体" }));

      act(() => vi.advanceTimersByTime(6000));
      expect(bridge.commands.filter(
        (command) => command.type === "runtime/dashboard/refresh"
      )).toHaveLength(1);

      act(() => bridge.emit(runtimeDashboardChanged()));
      act(() => vi.advanceTimersByTime(2000));
      expect(bridge.commands.filter(
        (command) => command.type === "runtime/dashboard/refresh"
      )).toHaveLength(2);
    }
    finally {
      vi.useRealTimers();
    }
  });

  it("keeps pending runtime actions disabled across unrelated dashboard snapshots", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);
    act(() => bridge.emit({
      type: "engine/status",
      status: "ready",
      sessionId: "session-42"
    }));
    fireEvent.click(screen.getByRole("button", { name: "智能体" }));
    act(() => bridge.emit(runtimeDashboardChanged()));

    const kill = screen.getByRole("button", { name: "终止后台任务：dotnet test" });
    const cancel = screen.getByRole("button", { name: "取消智能体：运行桌面测试" });
    fireEvent.click(kill);
    fireEvent.click(cancel);
    expect(kill).toBeDisabled();
    expect(cancel).toBeDisabled();

    act(() => bridge.emit(runtimeDashboardChanged()));
    expect(kill).toBeDisabled();
    expect(cancel).toBeDisabled();
  });

  it("re-enables only the background task action reported as failed", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);
    act(() => bridge.emit({
      type: "engine/status",
      status: "ready",
      sessionId: "session-42"
    }));
    fireEvent.click(screen.getByRole("button", { name: "智能体" }));
    act(() => bridge.emit(runtimeDashboardChanged()));

    const kill = screen.getByRole("button", { name: "终止后台任务：dotnet test" });
    const cancel = screen.getByRole("button", { name: "取消智能体：运行桌面测试" });
    fireEvent.click(kill);
    fireEvent.click(cancel);

    act(() => bridge.emit({
      type: "runtime/dashboard/error",
      sessionId: "session-42",
      message: "无法终止后台任务。",
      operation: "task_kill",
      itemId: "task-7"
    }));

    expect(kill).toBeEnabled();
    expect(cancel).toBeDisabled();
  });

  it("re-enables only the subagent action reported as failed", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);
    act(() => bridge.emit({
      type: "engine/status",
      status: "ready",
      sessionId: "session-42"
    }));
    fireEvent.click(screen.getByRole("button", { name: "智能体" }));
    act(() => bridge.emit(runtimeDashboardChanged()));

    const kill = screen.getByRole("button", { name: "终止后台任务：dotnet test" });
    const cancel = screen.getByRole("button", { name: "取消智能体：运行桌面测试" });
    fireEvent.click(kill);
    fireEvent.click(cancel);

    act(() => bridge.emit({
      type: "runtime/dashboard/error",
      sessionId: "session-42",
      message: "无法取消智能体。",
      operation: "subagent_cancel",
      itemId: "subagent-7"
    }));

    expect(kill).toBeDisabled();
    expect(cancel).toBeEnabled();
  });

  it("clears a selected running agent when it leaves the authoritative list", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);
    act(() => bridge.emit({
      type: "engine/status",
      status: "ready",
      sessionId: "session-42"
    }));
    fireEvent.click(screen.getByRole("button", { name: "智能体" }));
    const dashboard = runtimeDashboardChanged();
    act(() => bridge.emit(dashboard));
    fireEvent.click(screen.getByRole("button", { name: "查看智能体：运行桌面测试" }));
    expect(screen.getByRole("heading", { name: "智能体详情" })).toBeInTheDocument();

    act(() => bridge.emit({
      ...dashboard,
      subagents: []
    }));

    expect(screen.queryByRole("heading", { name: "智能体详情" })).not.toBeInTheDocument();
  });

  it("shows runtime dashboard errors without replacing the conversation engine state", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);
    act(() => bridge.emit({
      type: "engine/status",
      status: "ready",
      sessionId: "session-42"
    }));
    fireEvent.click(screen.getByRole("button", { name: "智能体" }));

    act(() => bridge.emit({
      type: "runtime/dashboard/error",
      sessionId: "session-42",
      message: "无法读取运行中的任务。",
      operation: "refresh"
    }));

    expect(screen.getByRole("alert")).toHaveTextContent("无法读取运行中的任务。");
    fireEvent.click(screen.getByRole("button", { name: "重试" }));
    expect(bridge.commands.filter(
      (command) => command.type === "runtime/dashboard/refresh"
    )).toHaveLength(2);
  });

  it("opens a third worktree surface with loading, empty, stale, and recoverable states", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);

    fireEvent.click(screen.getByRole("button", { name: "工作树" }));

    expect(screen.getByRole("main", { name: "工作树" })).toHaveAttribute("aria-busy", "true");
    expect(screen.getByRole("status")).toHaveTextContent("正在加载工作树");
    expect(bridge.commands).toContainEqual({
      type: "worktree/list",
      workspaceGeneration: 1,
      includeAll: false,
      types: []
    });

    emitWorktreeEvent(bridge, worktreeListChanged([], 0));
    expect(screen.queryByText("没有工作树")).not.toBeInTheDocument();
    expect(screen.getByRole("status")).toHaveTextContent("正在加载工作树");

    emitWorktreeEvent(bridge, worktreeListChanged([], 1));
    expect(screen.getByText("没有工作树")).toBeInTheDocument();
    expect(screen.getByRole("main", { name: "工作树" })).toHaveAttribute("aria-busy", "false");

    emitWorktreeEvent(bridge, {
      type: "worktree/error",
      workspaceGeneration: 1,
      message: "无法读取工作树。",
      operation: "list"
    });
    expect(screen.getByRole("alert")).toHaveTextContent("无法读取工作树。");

    fireEvent.click(screen.getByRole("button", { name: "重试" }));
    expect(commandsOfType(bridge, "worktree/list")).toHaveLength(2);
  });

  it("creates and inspects worktrees for the active session", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);
    act(() => bridge.emit({
      type: "engine/status",
      status: "ready",
      sessionId: "session-42"
    }));
    fireEvent.click(screen.getByRole("button", { name: "工作树" }));
    emitWorktreeEvent(bridge, worktreeListChanged([worktreeRecord()], 1));

    expect(screen.getByText("Parser experiment")).toBeInTheDocument();
    expect(screen.getByText("C:\\repo\\.worktrees\\parser")).toBeInTheDocument();

    const create = screen.getByRole("button", { name: "创建工作树" });
    fireEvent.click(create);
    const createDialog = screen.getByRole("dialog", { name: zhCn.createWorktree });
    fireEvent.change(within(createDialog).getByLabelText(zhCn.worktreeCopyMode), {
      target: { value: "clean" }
    });
    fireEvent.change(within(createDialog).getByLabelText(zhCn.creationType), {
      target: { value: "standalone" }
    });
    fireEvent.change(within(createDialog).getByLabelText(zhCn.gitReference), {
      target: { value: "feature/base" }
    });
    fireEvent.change(within(createDialog).getByLabelText(zhCn.worktreeLabel), {
      target: { value: "parser-copy" }
    });
    fireEvent.change(within(createDialog).getByLabelText(zhCn.worktreeDestination), {
      target: { value: "C:\\repo\\.worktrees\\parser-copy" }
    });
    fireEvent.click(within(createDialog).getByRole("button", { name: zhCn.createWorktree }));
    expect(bridge.commands).toContainEqual({
      type: "worktree/create",
      workspaceGeneration: 1,
      sessionId: "session-42",
      copyMode: "clean",
      copyIgnoredInBackground: false,
      ignoredSkipPatterns: [],
      creationType: "standalone",
      gitReference: "feature/base",
      label: "parser-copy",
      destinationPath: "C:\\repo\\.worktrees\\parser-copy"
    });
    expect(create).toBeDisabled();
    expect(screen.getByRole("main", { name: "工作树" })).toHaveAttribute("aria-busy", "true");

    emitWorktreeEvent(bridge, {
      type: "worktree/created",
      workspaceGeneration: 1,
      status: "creating",
      sessionId: "session-42",
      worktreePath: "C:\\repo\\.worktrees\\new-parser",
      sourceGitRoot: "C:\\repo"
    });
    expect(create).toBeEnabled();

    fireEvent.click(screen.getByRole("button", { name: "查看工作树：Parser experiment" }));

    expect(screen.getByRole("heading", { name: "工作树详情" })).toBeInTheDocument();
    expect(screen.getByText("feature/parser")).toBeInTheDocument();
    expect(commandsOfType(bridge, "worktree/show")).toHaveLength(0);
  });

  it("selects the main Git worktree directly from the list snapshot", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);
    fireEvent.click(screen.getByRole("button", { name: "工作树" }));
    const mainWorktree = {
      ...worktreeRecord(),
      id: "git-main-repo",
      path: "C:\\repo",
      sourceRepository: "C:\\repo",
      creationType: "standalone" as const,
      gitReference: "main",
      metadata: { label: "Main repository", userProvided: false }
    };
    emitWorktreeEvent(bridge, worktreeListChanged([mainWorktree], 1));

    expect(screen.getByText("Main repository")).toBeInTheDocument();
    expect(screen.getByText("C:\\repo")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "查看工作树：Main repository" }));

    const details = screen.getByRole("heading", { name: "工作树详情" }).closest("section");
    expect(details).not.toBeNull();
    expect(within(details!).getByText("Main repository")).toBeInTheDocument();
    expect(within(details!).getAllByText("C:\\repo")).toHaveLength(2);
    expect(within(details!).getByText("main")).toBeInTheDocument();
    expect(commandsOfType(bridge, "worktree/show")).toHaveLength(0);
  });

  it("protects the main Git worktree while keeping linked worktree actions enabled", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);
    act(() => bridge.emit({
      type: "engine/status",
      status: "ready",
      sessionId: "session-42"
    }));
    fireEvent.click(screen.getByRole("button", { name: "工作树" }));
    const mainWorktree = {
      ...worktreeRecord(),
      id: "git-main-repo",
      path: "C:\\repo",
      sourceRepository: "C:\\repo",
      creationType: "standalone" as const,
      gitReference: "main",
      metadata: { label: "Main repository", userProvided: false }
    };
    emitWorktreeEvent(bridge, worktreeListChanged([mainWorktree, worktreeRecord()], 1));

    expect(screen.getByRole("button", { name: "应用工作树：Main repository" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "移除工作树：Main repository" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "应用工作树：Parser experiment" })).toBeEnabled();
    const removeLinked = screen.getByRole("button", { name: "移除工作树：Parser experiment" });
    expect(removeLinked).toBeEnabled();
    const confirm = vi.spyOn(window, "confirm").mockReturnValue(true);
    try {
      fireEvent.click(removeLinked);
    }
    finally {
      confirm.mockRestore();
    }
    expect(bridge.commands).toContainEqual({
      type: "worktree/remove",
      workspaceGeneration: 1,
      idOrPath: "C:\\repo\\.worktrees\\parser",
      force: false,
      dryRun: false
    });
  });

  it("polls a bounded number of times after worktree creation and clears timers on unmount", () => {
    vi.useFakeTimers();
    try {
      const bridge = new RecordingBridge();
      const view = render(<Workbench bridge={bridge} />);
      fireEvent.click(screen.getByRole("button", { name: "工作树" }));
      emitWorktreeEvent(bridge, worktreeListChanged([], 1));
      expect(commandsOfType(bridge, "worktree/list")).toHaveLength(1);

      emitWorktreeEvent(bridge, {
        type: "worktree/created",
        workspaceGeneration: 1,
        status: "creating",
        sessionId: "session-42",
        worktreePath: "C:\\repo\\.worktrees\\eventual",
        sourceGitRoot: "C:\\repo"
      });
      expect(commandsOfType(bridge, "worktree/list")).toHaveLength(1);

      for (let attempt = 1; attempt <= 6; attempt += 1) {
        act(() => vi.advanceTimersByTime(500));
        expect(commandsOfType(bridge, "worktree/list")).toHaveLength(1 + attempt);
        emitWorktreeEvent(bridge, worktreeListChanged([], 1));
      }
      act(() => vi.advanceTimersByTime(10_000));
      expect(commandsOfType(bridge, "worktree/list")).toHaveLength(7);

      emitWorktreeEvent(bridge, {
        type: "worktree/created",
        workspaceGeneration: 1,
        status: "creating",
        sessionId: "session-42",
        worktreePath: "C:\\repo\\.worktrees\\cancelled",
        sourceGitRoot: "C:\\repo"
      });
      view.unmount();
      act(() => vi.advanceTimersByTime(10_000));
      expect(commandsOfType(bridge, "worktree/list")).toHaveLength(7);
    }
    finally {
      vi.useRealTimers();
    }
  });

  it("stops created-worktree polling as soon as the new path appears", () => {
    vi.useFakeTimers();
    try {
      const bridge = new RecordingBridge();
      render(<Workbench bridge={bridge} />);
      fireEvent.click(screen.getByRole("button", { name: "工作树" }));
      emitWorktreeEvent(bridge, worktreeListChanged([], 1));
      emitWorktreeEvent(bridge, {
        type: "worktree/created",
        workspaceGeneration: 1,
        status: "creating",
        sessionId: "session-42",
        worktreePath: "C:\\repo\\.worktrees\\parser",
        sourceGitRoot: "C:\\repo"
      });

      act(() => vi.advanceTimersByTime(500));
      emitWorktreeEvent(bridge, worktreeListChanged([worktreeRecord()], 1));
      act(() => vi.advanceTimersByTime(10_000));

      expect(commandsOfType(bridge, "worktree/list")).toHaveLength(2);
    }
    finally {
      vi.useRealTimers();
    }
  });

  it("prepares an editable bounded review request and submits only after explicit approval", async () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);
    act(() => bridge.emit({
      type: "engine/status",
      status: "ready",
      sessionId: "session-42"
    }));
    fireEvent.click(screen.getByRole("button", { name: "工作树" }));
    emitWorktreeEvent(bridge, worktreeListChanged([worktreeRecord()], 1));
    expect(screen.queryByRole("button", { name: zhCn.reviewWorktree })).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "查看工作树：Parser experiment" }));
    emitWorktreeEvent(bridge, {
      type: "worktree/detail",
      workspaceGeneration: 1,
      worktree: {
        ...worktreeRecord(),
        metadata: { label: "DO_NOT_EMBED", userProvided: true }
      }
    });

    fireEvent.click(screen.getByRole("button", { name: zhCn.reviewWorktree }));
    const request = screen.getByRole("textbox", { name: zhCn.worktreeReviewRequest });
    expect(request).toHaveValue(zhCn.worktreeReviewPrompt
      .replace("{id}", JSON.stringify("worktree-7"))
      .replace("{path}", JSON.stringify("C:\\repo\\.worktrees\\parser"))
      .replace("{baseReference}", JSON.stringify("feature/parser"))
      .replace("{basePath}", JSON.stringify("C:\\repo")));
    expect((request as HTMLTextAreaElement).value).not.toContain("DO_NOT_EMBED");
    expect((request as HTMLTextAreaElement).value.length).toBeLessThanOrEqual(16 * 1024);
    expect(commandsOfType(bridge, "engine/prompt")).toHaveLength(0);

    fireEvent.change(request, { target: { value: "Review only the parser boundary changes." } });
    fireEvent.click(screen.getByRole("button", { name: zhCn.startWorktreeReview }));

    expect(screen.getByRole("alertdialog", { name: zhCn.nativeRiskTitle })).toBeInTheDocument();
    expect(commandsOfType(bridge, "engine/prompt")).toHaveLength(0);
    fireEvent.click(screen.getByRole("button", { name: zhCn.nativeRiskContinue }));

    expect(commandsOfType(bridge, "engine/prompt")).toContainEqual({
      type: "engine/prompt",
      text: "Review only the parser boundary changes.",
      executionProfile: "NativeProtected",
      sessionMode: "default",
      nativeRiskAcknowledged: true,
      workspaceGeneration: 1
    });
    expect(screen.getByRole("main", { name: zhCn.conversation })).toBeInTheDocument();
  });

  it("disables worktree review while running and drops drafts from stale generations", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);
    act(() => bridge.emit({
      type: "engine/status",
      status: "ready",
      sessionId: "session-42"
    }));
    fireEvent.click(screen.getByRole("button", { name: "工作树" }));
    emitWorktreeEvent(bridge, worktreeListChanged([worktreeRecord()], 1));
    fireEvent.click(screen.getByRole("button", { name: "查看工作树：Parser experiment" }));
    emitWorktreeEvent(bridge, {
      type: "worktree/detail",
      workspaceGeneration: 1,
      worktree: worktreeRecord()
    });
    fireEvent.click(screen.getByRole("button", { name: zhCn.reviewWorktree }));

    const startReview = screen.getByRole("button", { name: zhCn.startWorktreeReview });
    act(() => bridge.emit({
      type: "engine/status",
      status: "running",
      sessionId: "session-42"
    }));
    expect(startReview).toBeDisabled();

    act(() => bridge.emit({
      type: "workspace/selected",
      path: "C:\\repo-next",
      workspaceGeneration: 2
    }));
    expect(screen.queryByRole("textbox", { name: zhCn.worktreeReviewRequest }))
      .not.toBeInTheDocument();
    emitWorktreeEvent(bridge, {
      type: "worktree/detail",
      workspaceGeneration: 1,
      worktree: worktreeRecord()
    });
    expect(screen.queryByRole("button", { name: zhCn.startWorktreeReview }))
      .not.toBeInTheDocument();
    expect(commandsOfType(bridge, "engine/prompt")).toHaveLength(0);
  });

  it("clears the selected worktree and review draft after path-based removal", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);
    act(() => bridge.emit({
      type: "engine/status",
      status: "ready",
      sessionId: "session-42"
    }));
    fireEvent.click(screen.getByRole("button", { name: "工作树" }));
    emitWorktreeEvent(bridge, worktreeListChanged([worktreeRecord()], 1));
    fireEvent.click(screen.getByRole("button", { name: "查看工作树：Parser experiment" }));
    fireEvent.click(screen.getByRole("button", { name: zhCn.reviewWorktree }));
    expect(screen.getByRole("textbox", { name: zhCn.worktreeReviewRequest })).toBeInTheDocument();

    const confirm = vi.spyOn(window, "confirm").mockReturnValue(true);
    try {
      fireEvent.click(screen.getByRole("button", { name: "移除工作树：Parser experiment" }));
    }
    finally {
      confirm.mockRestore();
    }
    emitWorktreeEvent(bridge, {
      type: "worktree/removed",
      workspaceGeneration: 1,
      idOrPath: "C:\\repo\\.worktrees\\parser",
      removed: true
    });

    expect(screen.queryByRole("textbox", { name: zhCn.worktreeReviewRequest }))
      .not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: zhCn.reviewWorktree }))
      .not.toBeInTheDocument();

    emitWorktreeEvent(bridge, worktreeListChanged([worktreeRecord()], 1));
    fireEvent.click(screen.getByRole("button", { name: "查看工作树：Parser experiment" }));
    expect(screen.queryByRole("textbox", { name: zhCn.worktreeReviewRequest }))
      .not.toBeInTheDocument();
  });

  it("rejects worktree review metadata above the bounded prompt context", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);
    act(() => bridge.emit({
      type: "engine/status",
      status: "ready",
      sessionId: "session-42"
    }));
    fireEvent.click(screen.getByRole("button", { name: "工作树" }));
    const oversized = { ...worktreeRecord(), id: "x".repeat(513) };
    emitWorktreeEvent(bridge, worktreeListChanged([oversized], 1));
    fireEvent.click(screen.getByRole("button", { name: "查看工作树：Parser experiment" }));
    emitWorktreeEvent(bridge, {
      type: "worktree/detail",
      workspaceGeneration: 1,
      worktree: oversized
    });

    expect(screen.getByRole("button", { name: zhCn.reviewWorktree })).toBeDisabled();
    expect(screen.queryByRole("textbox", { name: zhCn.worktreeReviewRequest }))
      .not.toBeInTheDocument();
    expect(commandsOfType(bridge, "engine/prompt")).toHaveLength(0);
  });

  it("keeps apply, remove, and gc operations single-flight until authoritative results", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);
    act(() => bridge.emit({
      type: "engine/status",
      status: "ready",
      sessionId: "session-42"
    }));
    fireEvent.click(screen.getByRole("button", { name: "工作树" }));
    emitWorktreeEvent(bridge, worktreeListChanged([worktreeRecord()], 1));

    const apply = screen.getByRole("button", { name: "应用工作树：Parser experiment" });
    const remove = screen.getByRole("button", { name: "移除工作树：Parser experiment" });
    const gc = screen.getByRole("button", { name: "清理工作树" });
    const refresh = screen.getByRole("button", { name: "刷新工作树" });

    fireEvent.click(apply);
    const applyDialog = screen.getByRole("dialog", { name: zhCn.applyWorktree });
    const applyConfirm = vi.spyOn(window, "confirm").mockReturnValue(true);
    fireEvent.click(within(applyDialog).getByRole("button", { name: zhCn.applyWorktree }));
    applyConfirm.mockRestore();
    expect(bridge.commands).toContainEqual({
      type: "worktree/apply",
      workspaceGeneration: 1,
      sessionId: "session-42",
      worktreePath: "C:\\repo\\.worktrees\\parser",
      mode: "merge"
    });
    for (const button of [apply, remove, gc, refresh]) {
      expect(button).toBeDisabled();
    }

    emitWorktreeEvent(bridge, {
      type: "worktree/applied",
      workspaceGeneration: 1,
      status: "conflicts",
      files: [{
        path: "src/parser.rs",
        changeType: "edit",
        additions: 4,
        deletions: 2
      }],
      conflicts: [{
        path: "src/parser.rs",
        changeType: "edit",
        ours: "ours",
        theirs: "theirs"
      }],
      gitRoot: "C:\\repo"
    });
    expect(apply).toBeEnabled();
    expect(screen.getByRole("alert")).toHaveTextContent("src/parser.rs");

    const confirm = vi.spyOn(window, "confirm").mockReturnValue(true);
    try {
      fireEvent.click(remove);
      expect(confirm).toHaveBeenCalled();
    }
    finally {
      confirm.mockRestore();
    }
    expect(bridge.commands).toContainEqual({
      type: "worktree/remove",
      workspaceGeneration: 1,
      idOrPath: "C:\\repo\\.worktrees\\parser",
      force: false,
      dryRun: false
    });
    expect(gc).toBeDisabled();

    emitWorktreeEvent(bridge, {
      type: "worktree/removed",
      workspaceGeneration: 1,
      idOrPath: "worktree-7",
      removed: true,
      resolvedPath: "C:\\repo\\.worktrees\\parser"
    });
    expect(gc).toBeEnabled();

    fireEvent.click(gc);
    expect(bridge.commands).toContainEqual({
      type: "worktree/gc",
      workspaceGeneration: 1,
      dryRun: true,
      force: false
    });
    expect(gc).toBeDisabled();

    emitWorktreeEvent(bridge, {
      type: "worktree/gc/completed",
      workspaceGeneration: 1,
      deadRemoved: 1,
      expiredRemoved: 2,
      skippedAlive: 3,
      removeFailed: 0
    });
    const gcDialog = screen.getByRole("dialog", { name: zhCn.gcWorktrees });
    const removable = within(gcDialog).getByText(zhCn.worktreeGcRemovable).parentElement!;
    expect(within(removable).getByText("3")).toBeInTheDocument();
    const cleanupConfirm = vi.spyOn(window, "confirm").mockReturnValue(true);
    fireEvent.click(within(gcDialog).getByRole("button", { name: zhCn.worktreeGcExecute }));
    cleanupConfirm.mockRestore();
    expect(bridge.commands).toContainEqual({
      type: "worktree/gc",
      workspaceGeneration: 1,
      dryRun: false,
      force: false
    });
    emitWorktreeEvent(bridge, {
      type: "worktree/gc/completed",
      workspaceGeneration: 1,
      deadRemoved: 1,
      expiredRemoved: 2,
      skippedAlive: 3,
      removeFailed: 0
    });
    expect(screen.queryByRole("dialog", { name: zhCn.gcWorktrees })).not.toBeInTheDocument();
  });

  it("shows local maintenance groups and explains portable-only updates", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);
    act(() => bridge.emit({
      type: "engine/status",
      status: "ready",
      sessionId: "session-42"
    }));

    fireEvent.click(screen.getByRole("button", { name: "设置" }));

    expect(screen.getByRole("region", { name: "本地维护" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "会话数据" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "备份与恢复" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "软件更新" })).toBeInTheDocument();
    for (const label of [
      "导出会话",
      "导入会话",
      "创建备份",
      "恢复备份",
      "检查更新",
      "安装并重启"
    ]) {
      expect(screen.getByRole("button", { name: label })).toBeInTheDocument();
    }
    expect(screen.getByText("应用内更新仅适用于便携版")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "安装并重启" })).toBeDisabled();

    fireEvent.click(screen.getByRole("button", { name: "检查更新" }));
    const check = lastCommandOfType(bridge, "update/check");
    expect(check.requestId).toEqual(expect.stringMatching(maintenanceRequestIdPattern));
    emitMaintenanceEvent(bridge, {
      type: "update/status",
      requestId: check.requestId,
      status: "unsupported"
    });

    expect(screen.getByText("MSIX 安装不支持应用内更新")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "检查更新" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "安装并重启" })).toBeDisabled();
  });

  it("keeps all local maintenance actions single-flight until the matching result", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);
    act(() => bridge.emit({
      type: "engine/status",
      status: "ready",
      sessionId: "session-42"
    }));
    fireEvent.click(screen.getByRole("button", { name: "设置" }));

    fireEvent.click(screen.getByRole("button", { name: "导出会话" }));
    const command = lastCommandOfType(bridge, "session/export");
    expect(command).toMatchObject({
      type: "session/export",
      sessionId: "session-42",
      requestId: expect.stringMatching(maintenanceRequestIdPattern)
    });
    for (const label of ["导出会话", "导入会话", "创建备份", "恢复备份", "检查更新"]) {
      expect(screen.getByRole("button", { name: label })).toBeDisabled();
    }

    emitMaintenanceEvent(bridge, {
      type: "session/imported",
      requestId: "99999999-9999-4999-8999-999999999999",
      sessionId: "unrelated-session",
      workspacePath: "C:\\unrelated"
    });
    expect(screen.getByRole("button", { name: "导入会话" })).toBeDisabled();

    emitMaintenanceEvent(bridge, {
      type: "session/exported",
      requestId: command.requestId,
      sessionId: "session-42",
      fileName: "session-42.agentdesk-session.json"
    });
    for (const label of ["导出会话", "导入会话", "创建备份", "恢复备份", "检查更新"]) {
      expect(screen.getByRole("button", { name: label })).toBeEnabled();
    }
  });

  it("keeps cloud local-only by default and exposes opt-in profile and policy workflows", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);
    act(() => bridge.emit({
      type: "engine/status",
      status: "ready",
      sessionId: "session-42"
    }));

    fireEvent.click(screen.getByRole("button", { name: zhCn.settings }));
    expect(screen.getByRole("heading", { name: zhCn.cloudTitle })).toBeInTheDocument();
    const initialProfile = lastCommandOfType(bridge, "cloud/profile/get");
    act(() => bridge.emit({
      type: "cloud/profile",
      requestId: initialProfile.requestId as string,
      localOnly: true,
      baseUri: null,
      teamId: null,
      deviceId: null,
      hasAccessToken: false
    }));
    expect(screen.getByText(zhCn.cloudLocalOnly)).toBeInTheDocument();
    expect(screen.queryByLabelText(/token|令牌/iu)).not.toBeInTheDocument();

    fireEvent.change(screen.getByLabelText(zhCn.cloudBaseUri), {
      target: { value: "https://cloud.example.test/" }
    });
    fireEvent.change(screen.getByLabelText(zhCn.cloudTeamId), {
      target: { value: "team-1" }
    });
    fireEvent.change(screen.getByLabelText(zhCn.cloudDeviceId), {
      target: { value: "device-1" }
    });
    fireEvent.click(screen.getByRole("button", { name: zhCn.cloudSaveRemote }));
    const saveRemote = lastCommandOfType(bridge, "cloud/profile/save-remote");
    expect(saveRemote).toMatchObject({
      baseUri: "https://cloud.example.test/",
      teamId: "team-1",
      deviceId: "device-1"
    });
    expect(saveRemote).not.toHaveProperty("accessToken");

    act(() => bridge.emit({
      type: "cloud/profile",
      requestId: saveRemote.requestId as string,
      localOnly: false,
      baseUri: "https://cloud.example.test/",
      teamId: "team-1",
      deviceId: "device-1",
      hasAccessToken: true
    }));
    fireEvent.click(screen.getByRole("button", { name: zhCn.cloudLoadPolicy }));
    const policyRequest = lastCommandOfType(bridge, "cloud/policy/get");
    act(() => bridge.emit({
      type: "cloud/policy",
      requestId: policyRequest.requestId as string,
      version: 2,
      allowedExecutionProfiles: ["NativeProtected"],
      remoteRunnerEnabled: false,
      uiAutomationEnabled: false,
      maximumConcurrentJobs: 2,
      allowedPluginPublishers: ["agentdesk.official"]
    }));
    fireEvent.click(screen.getByLabelText(zhCn.cloudUiAutomationExperimental));
    fireEvent.click(screen.getByRole("button", { name: zhCn.cloudSavePolicy }));
    expect(lastCommandOfType(bridge, "cloud/policy/update")).toMatchObject({
      allowedExecutionProfiles: ["NativeProtected"],
      remoteRunnerEnabled: false,
      uiAutomationEnabled: true,
      maximumConcurrentJobs: 2,
      allowedPluginPublishers: ["agentdesk.official"]
    });
  });

  it("disables tenant operations for an unsaved cloud draft and clears old tenant state", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);
    act(() => bridge.emit({ type: "engine/status", status: "ready", sessionId: "session-42" }));
    fireEvent.click(screen.getByRole("button", { name: zhCn.settings }));
    const initialProfile = lastCommandOfType(bridge, "cloud/profile/get");
    act(() => bridge.emit({
      type: "cloud/profile",
      requestId: initialProfile.requestId as string,
      localOnly: false,
      baseUri: "https://cloud-a.example.test/",
      teamId: "team-a",
      deviceId: "device-a",
      hasAccessToken: true
    }));

    fireEvent.click(screen.getByRole("button", { name: zhCn.cloudLoadPolicy }));
    const policyRequest = lastCommandOfType(bridge, "cloud/policy/get");
    act(() => bridge.emit({
      type: "cloud/policy",
      requestId: policyRequest.requestId as string,
      version: 9,
      allowedExecutionProfiles: ["NativeProtected"],
      remoteRunnerEnabled: true,
      uiAutomationEnabled: true,
      maximumConcurrentJobs: 4,
      allowedPluginPublishers: ["tenant-a.publisher"]
    }));
    expect(screen.getByLabelText(zhCn.cloudUiAutomationExperimental)).toBeChecked();

    fireEvent.change(screen.getByLabelText(zhCn.cloudTeamId), {
      target: { value: "team-b" }
    });
    fireEvent.change(screen.getByLabelText(zhCn.cloudRemoteSessionId), {
      target: { value: "remote-session-1" }
    });
    expect(screen.getByRole("button", { name: zhCn.cloudUploadSession })).toBeDisabled();
    expect(screen.getByRole("button", { name: zhCn.cloudDeleteSession })).toBeDisabled();

    fireEvent.click(screen.getByRole("button", { name: zhCn.cloudSaveRemote }));
    const saveRemote = lastCommandOfType(bridge, "cloud/profile/save-remote");
    act(() => bridge.emit({
      type: "cloud/profile",
      requestId: saveRemote.requestId as string,
      localOnly: false,
      baseUri: "https://cloud-a.example.test/",
      teamId: "team-b",
      deviceId: "device-a",
      hasAccessToken: true
    }));
    expect(screen.getByLabelText(zhCn.cloudUiAutomationExperimental)).not.toBeChecked();
    expect(screen.queryByDisplayValue("tenant-a.publisher")).not.toBeInTheDocument();
  });

  it("exports and deletes cloud sessions and surfaces push notifications", () => {
    const bridge = new RecordingBridge();
    const confirm = vi.spyOn(window, "confirm").mockReturnValue(true);
    render(<Workbench bridge={bridge} />);
    act(() => bridge.emit({
      type: "engine/status",
      status: "ready",
      sessionId: "session-42"
    }));

    fireEvent.click(screen.getByRole("button", { name: zhCn.settings }));
    const initialProfile = lastCommandOfType(bridge, "cloud/profile/get");
    act(() => bridge.emit({
      type: "cloud/profile",
      requestId: initialProfile.requestId as string,
      localOnly: false,
      baseUri: "https://cloud.example.test/",
      teamId: "team-1",
      deviceId: "device-1",
      hasAccessToken: true
    }));

    fireEvent.click(screen.getByRole("button", { name: zhCn.cloudExportSession }));
    const exported = lastCommandOfType(bridge, "cloud/session/export");
    expect(exported).toMatchObject({ sessionId: "session-42" });
    act(() => bridge.emit({
      type: "cloud/session/exported",
      requestId: exported.requestId as string,
      sessionId: "session-42",
      fileName: "session-42.agentdesk-session.json"
    }));
    expect(screen.getByText(
      zhCn.cloudSessionExported.replace("{fileName}", "session-42.agentdesk-session.json")
    )).toBeInTheDocument();

    fireEvent.change(screen.getByLabelText(zhCn.cloudRemoteSessionId), {
      target: { value: "remote-session-1" }
    });
    fireEvent.click(screen.getByRole("button", { name: zhCn.cloudDeleteSession }));
    expect(confirm).toHaveBeenCalledWith(zhCn.cloudDeleteSessionConfirm);
    const deleted = lastCommandOfType(bridge, "cloud/session/delete");
    expect(deleted).toMatchObject({ remoteSessionId: "remote-session-1" });
    act(() => bridge.emit({
      type: "cloud/session/deleted",
      requestId: deleted.requestId as string,
      remoteSessionId: "remote-session-1",
      found: true,
      revision: 4
    }));
    expect(screen.getByText(
      zhCn.cloudSessionDeleted.replace("{revision}", "4")
    )).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: zhCn.close }));
    act(() => bridge.emit({
      type: "cloud/notification",
      kind: "policy-changed",
      policyVersion: 7
    }));
    expect(screen.getByText(
      zhCn.cloudNotificationPolicy.replace("{version}", "7")
    )).toBeInTheDocument();
  });

  it("queues, claims, completes runner jobs and creates cloud automations", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);
    fireEvent.click(screen.getByRole("button", { name: zhCn.settings }));
    const initialProfile = lastCommandOfType(bridge, "cloud/profile/get");
    act(() => bridge.emit({
      type: "cloud/profile",
      requestId: initialProfile.requestId as string,
      localOnly: false,
      baseUri: "https://cloud.example.test/",
      teamId: "team-1",
      deviceId: "device-1",
      hasAccessToken: true
    }));

    fireEvent.change(screen.getByLabelText("Runner ID"), { target: { value: "runner-1" } });
    fireEvent.change(screen.getByLabelText("Runner 所需能力"), {
      target: { value: "windows" }
    });
    fireEvent.change(screen.getByLabelText("Runner 任务"), {
      target: { value: "inspect workspace" }
    });
    fireEvent.click(screen.getByRole("button", { name: "加入 Runner 队列" }));
    const queued = lastCommandOfType(bridge, "cloud/runner/queue");
    expect(queued).toMatchObject({
      requiredCapability: "windows",
      task: "inspect workspace"
    });
    act(() => bridge.emit({
      type: "cloud/runner/queued",
      requestId: queued.requestId as string,
      jobId: "job-1"
    }));
    expect(screen.getByText("已排队任务 job-1")).toBeInTheDocument();

    const claimButton = screen.getByRole("button", { name: "领取 Runner 任务" });
    fireEvent.change(screen.getByLabelText("租约秒数"), { target: { value: "9" } });
    expect(claimButton).toBeDisabled();
    fireEvent.change(screen.getByLabelText("租约秒数"), { target: { value: "601" } });
    expect(claimButton).toBeDisabled();
    fireEvent.change(screen.getByLabelText("租约秒数"), { target: { value: "60" } });
    fireEvent.click(claimButton);
    const claimed = lastCommandOfType(bridge, "cloud/runner/claim");
    expect(claimed).toMatchObject({ runnerId: "runner-1", leaseSeconds: 60 });
    act(() => bridge.emit({
      type: "cloud/runner/claimed",
      requestId: claimed.requestId as string,
      found: true,
      claimHandle: "claim-1",
      jobId: "job-1",
      requiredCapability: "windows",
      task: "inspect workspace",
      leaseExpiresAt: "2026-07-18T12:05:00Z"
    } as HostEvent));
    expect(screen.getByText("inspect workspace")).toBeInTheDocument();

    fireEvent.change(screen.getByLabelText("任务结果"), {
      target: { value: "completed locally" }
    });
    fireEvent.click(screen.getByRole("button", { name: "完成 Runner 任务" }));
    const completed = lastCommandOfType(bridge, "cloud/runner/complete");
    expect(completed).toMatchObject({
      claimHandle: "claim-1",
      jobId: "job-1",
      result: "completed locally"
    });
    act(() => bridge.emit({
      type: "cloud/runner/completed",
      requestId: completed.requestId as string,
      claimHandle: "claim-1",
      jobId: "job-1"
    }));

    fireEvent.change(screen.getByLabelText("自动化名称"), {
      target: { value: "Nightly review" }
    });
    fireEvent.change(screen.getByLabelText("间隔秒数"), { target: { value: "3600" } });
    fireEvent.change(screen.getByLabelText("自动化所需能力"), {
      target: { value: "windows" }
    });
    fireEvent.change(screen.getByLabelText("自动化任务"), {
      target: { value: "review branch" }
    });
    fireEvent.click(screen.getByRole("button", { name: "创建自动化" }));
    const created = lastCommandOfType(bridge, "cloud/automation/create");
    expect(created).toMatchObject({
      name: "Nightly review",
      intervalSeconds: 3600,
      requiredCapability: "windows",
      task: "review branch"
    });
    act(() => bridge.emit({
      type: "cloud/automation/created",
      requestId: created.requestId as string,
      automation: {
        automationId: "automation-2",
        name: "Nightly review",
        intervalSeconds: 3600,
        enabled: true,
        nextRunAt: "2026-07-19T00:00:00Z"
      }
    }));
    expect(screen.getByText("Nightly review")).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: zhCn.cloudLoadAutomations }));
    const list = lastCommandOfType(bridge, "cloud/automation/list");
    act(() => bridge.emit({
      type: "cloud/automations",
      requestId: list.requestId as string,
      automations: [{
        automationId: "automation-2",
        name: "Nightly review",
        intervalSeconds: 3600,
        enabled: true,
        nextRunAt: "2026-07-19T00:00:00Z"
      }]
    }));
    fireEvent.click(screen.getByRole("button", { name: zhCn.cloudDisableAutomation }));
    expect(lastCommandOfType(bridge, "cloud/automation/disable")).toMatchObject({
      automationId: "automation-2"
    });
  });

  it("lists and manages extensions without exposing secret values", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);
    act(() => bridge.emit({
      type: "engine/status",
      status: "ready",
      sessionId: "session-42"
    }));
    fireEvent.click(screen.getByRole("button", { name: zhCn.settings }));
    const request = lastCommandOfType(bridge, "extensions/list");
    expect(request).toMatchObject({
      workspaceGeneration: 1,
      sessionId: "session-42",
      useCache: true
    });
    act(() => bridge.emit({
      type: "extensions/catalog",
      requestId: request.requestId as string,
      sessionId: "session-42",
      mcp: {
        servers: [{
          name: "github",
          displayName: "GitHub",
          source: "local",
          transport: "stdio",
          command: "npx",
          arguments: ["server-github"],
          environmentVariableNames: ["GITHUB_TOKEN"],
          session: { enabled: true, status: "ready", tools: [], authRequired: false }
        }]
      },
      skills: {
        skills: [],
        configuration: { paths: [], ignoredPaths: [], totalSkills: 0 }
      },
      hooks: { hooks: [], projectTrusted: false, loadErrorCount: 0 },
      plugins: { plugins: [] },
      marketplace: {
        sources: [{
          name: "official",
          kind: "git",
          source: "https://example.test/catalog.git",
          plugins: [{
            name: "Review tools",
            source: "https://example.test/catalog.git",
            author: "publisher-key-1",
            tags: [],
            keywords: [],
            domains: [],
            relativePath: "plugins/review",
            skillCount: 1,
            hasHooks: false,
            hasAgents: false,
            hasMcp: false,
            installStatus: "not_installed"
          }, {
            name: "Review update",
            source: "https://example.test/catalog.git",
            author: "publisher-key-2",
            tags: [],
            keywords: [],
            domains: [],
            relativePath: "plugins/review-update",
            skillCount: 1,
            hasHooks: false,
            hasAgents: false,
            hasMcp: false,
            installStatus: "update_available"
          }]
        }]
      }
    }));

    expect(screen.getByText("GitHub")).toBeInTheDocument();
    expect(screen.getByText(/GITHUB_TOKEN/u)).toBeInTheDocument();
    expect(screen.queryByText("must-not-cross-web")).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", {
      name: `${zhCn.extensionDisable}: GitHub`
    }));
    const toggle = lastCommandOfType(bridge, "extensions/action");
    expect(toggle).toMatchObject({
      sessionId: "session-42",
      scope: "mcp",
      action: "toggle",
      confirmed: true,
      payload: { serverName: "github", enabled: false }
    });
    act(() => bridge.emit({
      type: "extensions/action/completed",
      requestId: toggle.requestId as string,
      sessionId: "session-42",
      scope: "mcp",
      action: "toggle",
      status: "success",
      message: "updated",
      requiresReload: false,
      requiresRestart: false
    }));

    fireEvent.click(screen.getByRole("tab", { name: zhCn.extensionTab_marketplace }));
    const confirm = vi.spyOn(window, "confirm").mockReturnValue(true);
    fireEvent.click(screen.getByRole("button", { name: zhCn.marketplaceAction_install }));
    expect(lastCommandOfType(bridge, "extensions/action")).toMatchObject({
      scope: "marketplace",
      action: "install",
      confirmed: true,
      payload: {
        source: "https://example.test/catalog.git",
        relativePath: "plugins/review"
      }
    });
    expect(lastCommandOfType(bridge, "extensions/action").payload).not.toHaveProperty(
      "publisherKeyId"
    );
    const marketplaceInstall = lastCommandOfType(bridge, "extensions/action");
    act(() => bridge.emit({
      type: "extensions/action/completed",
      requestId: marketplaceInstall.requestId as string,
      sessionId: "session-42",
      scope: "marketplace",
      action: "install",
      status: "success",
      message: "installed",
      requiresReload: true,
      requiresRestart: false
    }));
    fireEvent.click(screen.getByRole("button", { name: zhCn.marketplaceAction_update }));
    expect(lastCommandOfType(bridge, "extensions/action")).toMatchObject({
      scope: "marketplace",
      action: "update",
      payload: {
        source: "https://example.test/catalog.git",
        relativePath: "plugins/review-update"
      }
    });
    expect(lastCommandOfType(bridge, "extensions/action").payload).not.toHaveProperty(
      "publisherKeyId"
    );
    confirm.mockRestore();
  });

  it("uses native extension identifier rules and releases pending actions on errors", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);
    act(() => bridge.emit({
      type: "engine/status",
      status: "ready",
      sessionId: "session-42"
    }));
    fireEvent.click(screen.getByRole("button", { name: zhCn.settings }));
    const list = lastCommandOfType(bridge, "extensions/list");
    act(() => bridge.emit({
      type: "extensions/catalog",
      requestId: list.requestId as string,
      sessionId: "session-42",
      mcp: { servers: [] },
      skills: { skills: [], configuration: { paths: [], ignoredPaths: [], totalSkills: 0 } },
      hooks: { hooks: [], projectTrusted: false, loadErrorCount: 0 },
      plugins: { plugins: [] },
      marketplace: { sources: [] }
    }));
    const save = screen.getByRole("button", { name: zhCn.mcpSaveServer });
    fireEvent.change(screen.getByLabelText(zhCn.mcpServerName), {
      target: { value: "server!" }
    });
    fireEvent.change(screen.getByLabelText(zhCn.mcpCommand), {
      target: { value: "npx" }
    });

    expect(save).toBeDisabled();

    fireEvent.change(screen.getByLabelText(zhCn.mcpServerName), {
      target: { value: "server-1" }
    });
    fireEvent.click(save);
    const action = lastCommandOfType(bridge, "extensions/action");
    expect(save).toBeDisabled();
    act(() => bridge.emit({
      type: "extensions/error",
      requestId: action.requestId as string,
      message: "invalid extension payload"
    }));

    expect(save).toBeEnabled();
    expect(screen.getByText("invalid extension payload")).toBeInTheDocument();
  });

  it("exposes the engine-supported Hook, Plugin, and marketplace actions", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);
    act(() => bridge.emit({
      type: "engine/status",
      status: "ready",
      sessionId: "session-42"
    }));
    fireEvent.click(screen.getByRole("button", { name: zhCn.settings }));
    const request = lastCommandOfType(bridge, "extensions/list");
    act(() => bridge.emit({
      type: "extensions/catalog",
      requestId: request.requestId as string,
      sessionId: "session-42",
      mcp: { servers: [] },
      skills: { skills: [] },
      hooks: {
        projectTrusted: true,
        loadErrorCount: 0,
        hooks: [{
          name: "safety:pre[0]",
          event: "pre_tool_use",
          handlerType: "command",
          hasCommand: true,
          hasUrl: false,
          timeoutMs: 5000,
          sourceDirectory: "C:\\Users\\tester\\.grok",
          disabled: false
        }, {
          name: "safety:post[0]",
          event: "post_tool_use",
          handlerType: "command",
          hasCommand: true,
          hasUrl: false,
          timeoutMs: 5000,
          sourceDirectory: "C:\\Users\\tester\\.grok",
          disabled: false
        }]
      },
      plugins: {
        plugins: [{
          name: "Review",
          id: "user/12345678/review",
          root: "C:\\plugins\\review",
          scope: "user",
          trusted: true,
          enabled: true,
          skillCount: 1,
          skillNames: ["review"],
          agentCount: 0,
          agentNames: [],
          hookStatus: "none",
          hookCount: 0,
          mcpServerCount: 0,
          mcpStatus: "none"
        }]
      },
      marketplace: { sources: [] }
    }));

    const completeAction = () => {
      const command = lastCommandOfType(bridge, "extensions/action") as Extract<
        HostCommand,
        { type: "extensions/action" }
      >;
      act(() => bridge.emit({
        type: "extensions/action/completed",
        requestId: command.requestId as string,
        sessionId: "session-42",
        scope: command.scope,
        action: command.action,
        status: "success",
        message: "updated",
        requiresReload: false,
        requiresRestart: false
      }));
      return command;
    };

    fireEvent.click(screen.getByRole("tab", { name: zhCn.extensionTab_hooks }));
    fireEvent.change(screen.getByRole("textbox", { name: "Hook 路径" }), {
      target: { value: "C:\\Users\\tester\\.grok\\hooks.json" }
    });
    fireEvent.click(screen.getByRole("button", { name: "添加 Hook 路径" }));
    expect(completeAction()).toMatchObject({
      scope: "hooks",
      action: "add",
      payload: { path: "C:\\Users\\tester\\.grok\\hooks.json" }
    });
    fireEvent.click(screen.getByRole("button", { name: "移除 Hook 路径" }));
    expect(completeAction()).toMatchObject({ scope: "hooks", action: "remove" });
    fireEvent.click(screen.getByRole("button", { name: "禁用来源: safety:pre[0]" }));
    expect(completeAction()).toMatchObject({
      scope: "hooks",
      action: "toggle_source",
      payload: {
        hookNames: ["safety:pre[0]", "safety:post[0]"],
        disableSource: true
      }
    });

    fireEvent.click(screen.getByRole("tab", { name: zhCn.extensionTab_plugins }));
    fireEvent.change(screen.getByRole("textbox", { name: "Plugin 路径" }), {
      target: { value: ".agentdesk/plugins/review" }
    });
    fireEvent.click(screen.getByRole("button", { name: "添加 Plugin 路径" }));
    expect(completeAction()).toMatchObject({ scope: "plugins", action: "add" });
    fireEvent.click(screen.getByRole("button", { name: "移除 Plugin 路径" }));
    expect(completeAction()).toMatchObject({ scope: "plugins", action: "remove" });
    fireEvent.change(screen.getByRole("textbox", { name: "Plugin 安装源" }), {
      target: { value: "https://github.com/example/review-plugin" }
    });
    const confirm = vi.spyOn(window, "confirm").mockReturnValue(true);
    fireEvent.click(screen.getByRole("button", { name: "安装 Plugin" }));
    expect(completeAction()).toMatchObject({
      scope: "plugins",
      action: "install",
      confirmed: true,
      payload: {
        source: "https://github.com/example/review-plugin"
      }
    });
    expect(lastCommandOfType(bridge, "extensions/action").payload).not.toHaveProperty(
      "publisherKeyId"
    );
    fireEvent.click(screen.getByRole("button", { name: "更新: Review" }));
    expect(completeAction()).toMatchObject({
      scope: "plugins",
      action: "update",
      payload: {
        pluginId: "user/12345678/review"
      }
    });
    expect(lastCommandOfType(bridge, "extensions/action").payload).not.toHaveProperty(
      "publisherKeyId"
    );

    fireEvent.click(screen.getByRole("tab", { name: zhCn.extensionTab_marketplace }));
    fireEvent.click(screen.getByRole("button", { name: "刷新市场目录" }));
    expect(completeAction()).toMatchObject({
      scope: "marketplace",
      action: "refresh",
      payload: {}
    });
    confirm.mockRestore();
  });

  it("treats a cancelled native file picker as a neutral completion", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);
    act(() => bridge.emit({
      type: "engine/status",
      status: "ready",
      sessionId: "session-42"
    }));
    fireEvent.click(screen.getByRole("button", { name: "设置" }));

    fireEvent.click(screen.getByRole("button", { name: "恢复备份" }));
    const command = lastCommandOfType(bridge, "backup/restore");
    emitMaintenanceEvent(bridge, {
      type: "maintenance/cancelled",
      requestId: command.requestId,
      operation: "backup-restore"
    });

    expect(screen.getByRole("button", { name: "恢复备份" })).toBeEnabled();
    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
    expect(screen.queryByText(/备份已恢复|恢复失败/)).not.toBeInTheDocument();
  });

  it("disables every local maintenance action while a prompt is running", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);
    act(() => bridge.emit({
      type: "engine/status",
      status: "running",
      sessionId: "session-42"
    }));
    fireEvent.click(screen.getByRole("button", { name: "设置" }));

    for (const label of [
      "导出会话",
      "导入会话",
      "创建备份",
      "恢复备份",
      "检查更新",
      "安装并重启"
    ]) {
      expect(screen.getByRole("button", { name: label })).toBeDisabled();
    }
  });

  it("enables update apply only after a trusted update is staged", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);
    act(() => bridge.emit({
      type: "engine/status",
      status: "ready",
      sessionId: "session-42"
    }));
    fireEvent.click(screen.getByRole("button", { name: "设置" }));
    const apply = screen.getByRole("button", { name: "安装并重启" });
    const check = screen.getByRole("button", { name: "检查更新" });
    expect(apply).toBeDisabled();

    fireEvent.click(check);
    const firstCheck = lastCommandOfType(bridge, "update/check");
    emitMaintenanceEvent(bridge, {
      type: "update/status",
      requestId: firstCheck.requestId,
      status: "checking"
    });
    expect(apply).toBeDisabled();
    expect(check).toBeDisabled();
    emitMaintenanceEvent(bridge, {
      type: "update/status",
      requestId: firstCheck.requestId,
      status: "up-to-date"
    });
    expect(apply).toBeDisabled();
    expect(check).toBeEnabled();

    fireEvent.click(check);
    const secondCheck = lastCommandOfType(bridge, "update/check");
    expect(secondCheck.requestId).not.toBe(firstCheck.requestId);
    emitMaintenanceEvent(bridge, {
      type: "update/status",
      requestId: secondCheck.requestId,
      status: "available",
      version: "1.2.0"
    });
    expect(screen.getByText("1.2.0")).toBeInTheDocument();
    expect(apply).toBeEnabled();

    fireEvent.click(apply);
    const applyCommand = lastCommandOfType(bridge, "update/apply");
    expect(applyCommand.requestId).toEqual(expect.stringMatching(maintenanceRequestIdPattern));
    expect(applyCommand.requestId).not.toBe(secondCheck.requestId);
    expect(apply).toBeDisabled();
    expect(screen.getByRole("button", { name: "导出会话" })).toBeDisabled();
    emitMaintenanceEvent(bridge, {
      type: "update/status",
      requestId: applyCommand.requestId,
      status: "launching",
      version: "1.2.0"
    });
  });

  it("projects a trusted background update without starting a manual check", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);

    act(() => bridge.emit({
      type: "update/background-available",
      version: "2.3.4"
    }));

    expect(commandsOfType(bridge, "update/check")).toEqual([]);
    fireEvent.click(screen.getByRole("button", { name: zhCn.settings }));
    expect(screen.getByText("2.3.4")).toBeInTheDocument();
    expect(screen.getByText(zhCn.backgroundUpdateAvailable)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: zhCn.installAndRestart })).toBeEnabled();
  });

  it("loads and explicitly saves an existing AGENTS.md from settings", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);

    fireEvent.click(screen.getByRole("button", { name: zhCn.settings }));
    const list = lastCommandOfType(bridge, "workspace/context/instructions/list");
    expect(list).toMatchObject({ workspaceGeneration: 1 });
    act(() => bridge.emit({
      type: "workspace/context/instructions/list",
      requestId: list.requestId as string,
      workspaceGeneration: 1,
      files: [{
        relativePath: "AGENTS.md",
        byteLength: 12,
        lastWriteTime: "2026-07-18T08:30:00Z"
      }]
    }));
    const read = lastCommandOfType(bridge, "workspace/context/file/read");
    expect(read).toMatchObject({ workspaceGeneration: 1, relativePath: "AGENTS.md" });
    act(() => bridge.emit({
      type: "workspace/context/file/read",
      requestId: read.requestId as string,
      workspaceGeneration: 1,
      relativePath: "AGENTS.md",
      content: "# Original\n"
    }));

    const editor = screen.getByRole("textbox", { name: zhCn.agentsEditor });
    expect(editor).toHaveValue("# Original\n");
    fireEvent.change(editor, { target: { value: "# Updated\n\nRun tests.\n" } });
    const save = screen.getByRole("button", { name: zhCn.saveAgentsInstructions });
    expect(save).toBeEnabled();
    fireEvent.click(save);

    const write = lastCommandOfType(bridge, "workspace/context/instructions/write");
    expect(write).toMatchObject({
      workspaceGeneration: 1,
      relativePath: "AGENTS.md",
      content: "# Updated\n\nRun tests.\n"
    });
    act(() => bridge.emit({
      type: "workspace/context/instructions/write",
      requestId: write.requestId as string,
      workspaceGeneration: 1,
      relativePath: "AGENTS.md"
    }));
    expect(screen.getByText(zhCn.agentsSaved)).toBeInTheDocument();
  });

  it("creates a root AGENTS.md when the workspace has no instruction file", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);

    fireEvent.click(screen.getByRole("button", { name: zhCn.settings }));
    const list = lastCommandOfType(bridge, "workspace/context/instructions/list");
    act(() => bridge.emit({
      type: "workspace/context/instructions/list",
      requestId: list.requestId as string,
      workspaceGeneration: 1,
      files: []
    }));

    const editor = screen.getByRole("textbox", { name: zhCn.agentsEditor });
    fireEvent.change(editor, { target: { value: "# Project rules\n\nRun tests.\n" } });
    fireEvent.click(screen.getByRole("button", { name: zhCn.saveAgentsInstructions }));

    expect(lastCommandOfType(bridge, "workspace/context/instructions/write")).toMatchObject({
      workspaceGeneration: 1,
      relativePath: "AGENTS.md",
      content: "# Project rules\n\nRun tests.\n"
    });
  });

  it("rejects AGENTS.md content above the UTF-8 byte budget before saving", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);

    fireEvent.click(screen.getByRole("button", { name: zhCn.settings }));
    const list = lastCommandOfType(bridge, "workspace/context/instructions/list");
    act(() => bridge.emit({
      type: "workspace/context/instructions/list",
      requestId: list.requestId as string,
      workspaceGeneration: 1,
      files: [{
        relativePath: "AGENTS.md",
        byteLength: 12,
        lastWriteTime: "2026-07-18T08:30:00Z"
      }]
    }));
    const read = lastCommandOfType(bridge, "workspace/context/file/read");
    act(() => bridge.emit({
      type: "workspace/context/file/read",
      requestId: read.requestId as string,
      workspaceGeneration: 1,
      relativePath: "AGENTS.md",
      content: "# Original\n"
    }));

    fireEvent.change(screen.getByRole("textbox", { name: zhCn.agentsEditor }), {
      target: { value: "界".repeat(174_763) }
    });
    expect(screen.getByRole("alert")).toHaveTextContent(zhCn.agentsContentTooLarge);
    expect(screen.getByRole("button", { name: zhCn.saveAgentsInstructions })).toBeDisabled();
    expect(commandsOfType(bridge, "workspace/context/instructions/write")).toHaveLength(0);
  });

  it("lists, reads, confirms, writes, and deletes memory files from settings", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);
    act(() => {
      bridge.emit({ type: "engine/status", status: "ready", sessionId: "session-42" });
      bridge.emit({
        type: "memory/capabilities",
        sessionId: "session-42",
        memory: {
          schemaVersion: 1,
          list: true,
          read: true,
          write: true,
          delete: true,
          mutationConfirmationRequired: true
        }
      });
    });

    fireEvent.click(screen.getByRole("button", { name: zhCn.settings }));
    const list = lastCommandOfType(bridge, "memory/list");
    expect(list).toMatchObject({ workspaceGeneration: 1, sessionId: "session-42" });
    act(() => bridge.emit({
      type: "memory/listed",
      requestId: list.requestId as string,
      workspaceGeneration: 1,
      sessionId: "session-42",
      files: [{
        id: "workspace",
        scope: "workspace",
        name: "MEMORY.md",
        byteLength: 11,
        modifiedAt: "2026-07-18T08:30:00Z",
        writable: true
      }],
      truncated: false
    }));
    const read = lastCommandOfType(bridge, "memory/read");
    expect(read).toMatchObject({ fileId: "workspace", sessionId: "session-42" });
    act(() => bridge.emit({
      type: "memory/document",
      requestId: read.requestId as string,
      workspaceGeneration: 1,
      sessionId: "session-42",
      file: {
        id: "workspace",
        scope: "workspace",
        name: "MEMORY.md",
        byteLength: 11,
        modifiedAt: "2026-07-18T08:30:00Z",
        writable: true
      },
      content: "# Original\n"
    }));

    const editor = screen.getByRole("textbox", { name: "编辑记忆文件" });
    expect(editor).toHaveValue("# Original\n");
    fireEvent.change(editor, { target: { value: "# Updated\n" } });
    fireEvent.click(screen.getByRole("button", { name: "保存记忆" }));
    const writeChallenge = lastCommandOfType(bridge, "memory/write");
    expect(writeChallenge).toMatchObject({
      sessionId: "session-42",
      fileId: "workspace",
      content: "# Updated\n",
      confirmed: false
    });
    act(() => bridge.emit({
      type: "memory/mutation",
      requestId: writeChallenge.requestId as string,
      workspaceGeneration: 1,
      sessionId: "session-42",
      operation: "write",
      fileId: "workspace",
      status: "confirmation_required",
      message: "保存会覆盖当前记忆文件。",
      confirmationToken: "A".repeat(64)
    }));
    expect(screen.getByText("保存会覆盖当前记忆文件。")).toBeInTheDocument();
    expect(commandsOfType(bridge, "memory/write")).toHaveLength(1);

    fireEvent.click(screen.getByRole("button", { name: "确认保存" }));
    const confirmedWrite = lastCommandOfType(bridge, "memory/write");
    expect(confirmedWrite).toMatchObject({
      fileId: "workspace",
      content: "# Updated\n",
      confirmed: true,
      confirmationToken: "A".repeat(64)
    });
    expect(confirmedWrite.requestId).not.toBe(writeChallenge.requestId);
    act(() => bridge.emit({
      type: "memory/mutation",
      requestId: confirmedWrite.requestId as string,
      workspaceGeneration: 1,
      sessionId: "session-42",
      operation: "write",
      fileId: "workspace",
      status: "success",
      message: "已保存",
      file: {
        id: "workspace",
        scope: "workspace",
        name: "MEMORY.md",
        byteLength: 10,
        modifiedAt: "2026-07-18T08:31:00Z",
        writable: true
      }
    }));
    expect(screen.getByText("记忆文件已保存")).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "删除记忆文件" }));
    const deleteChallenge = lastCommandOfType(bridge, "memory/delete");
    expect(deleteChallenge).toMatchObject({ fileId: "workspace", confirmed: false });
    act(() => bridge.emit({
      type: "memory/mutation",
      requestId: deleteChallenge.requestId as string,
      workspaceGeneration: 1,
      sessionId: "session-42",
      operation: "delete",
      fileId: "workspace",
      status: "confirmation_required",
      message: "删除后无法撤销。",
      confirmationToken: "B".repeat(64)
    }));
    fireEvent.click(screen.getByRole("button", { name: "确认删除" }));
    const confirmedDelete = lastCommandOfType(bridge, "memory/delete");
    expect(confirmedDelete).toMatchObject({
      fileId: "workspace",
      confirmed: true,
      confirmationToken: "B".repeat(64)
    });
    act(() => bridge.emit({
      type: "memory/mutation",
      requestId: confirmedDelete.requestId as string,
      workspaceGeneration: 1,
      sessionId: "session-42",
      operation: "delete",
      fileId: "workspace",
      status: "success",
      message: "已删除"
    }));
    expect(screen.getByText("记忆文件已删除")).toBeInTheDocument();
    expect(screen.getByText("当前会话没有可管理的记忆文件")).toBeInTheDocument();
  });

  it("rejects memory content above the UTF-8 byte budget before requesting a mutation", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);
    act(() => {
      bridge.emit({ type: "engine/status", status: "ready", sessionId: "session-42" });
      bridge.emit({
        type: "memory/capabilities",
        sessionId: "session-42",
        memory: {
          schemaVersion: 1,
          list: true,
          read: true,
          write: true,
          delete: false,
          mutationConfirmationRequired: true
        }
      });
    });
    fireEvent.click(screen.getByRole("button", { name: zhCn.settings }));
    const list = lastCommandOfType(bridge, "memory/list");
    act(() => bridge.emit({
      type: "memory/listed",
      requestId: list.requestId as string,
      workspaceGeneration: 1,
      sessionId: "session-42",
      files: [{
        id: "workspace",
        scope: "workspace",
        name: "MEMORY.md",
        byteLength: 0,
        writable: true
      }],
      truncated: false
    }));
    const read = lastCommandOfType(bridge, "memory/read");
    act(() => bridge.emit({
      type: "memory/document",
      requestId: read.requestId as string,
      workspaceGeneration: 1,
      sessionId: "session-42",
      file: {
        id: "workspace",
        scope: "workspace",
        name: "MEMORY.md",
        byteLength: 0,
        writable: true
      },
      content: ""
    }));

    fireEvent.change(screen.getByRole("textbox", { name: "编辑记忆文件" }), {
      target: { value: "界".repeat(21_846) }
    });
    expect(screen.getByRole("alert")).toHaveTextContent(
      "记忆文件的 UTF-8 内容不能超过 64 KiB"
    );
    expect(screen.getByRole("button", { name: "保存记忆" })).toBeDisabled();
    expect(commandsOfType(bridge, "memory/write")).toHaveLength(0);
  });

  it("drops stale memory results after the workspace generation changes", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);
    act(() => {
      bridge.emit({ type: "engine/status", status: "ready", sessionId: "session-42" });
      bridge.emit({
        type: "memory/capabilities",
        sessionId: "session-42",
        memory: {
          schemaVersion: 1,
          list: true,
          read: true,
          write: false,
          delete: false,
          mutationConfirmationRequired: false
        }
      });
    });
    fireEvent.click(screen.getByRole("button", { name: zhCn.settings }));
    const staleList = lastCommandOfType(bridge, "memory/list");

    act(() => bridge.emit({
      type: "workspace/selected",
      path: "C:\\workspace",
      workspaceGeneration: 2
    }));
    act(() => bridge.emit({
      type: "memory/listed",
      requestId: staleList.requestId as string,
      workspaceGeneration: 1,
      sessionId: "session-42",
      files: [{
        id: "workspace",
        scope: "workspace",
        name: "STALE.md",
        byteLength: 5,
        writable: false
      }],
      truncated: false
    }));

    expect(screen.queryByText("STALE.md")).not.toBeInTheDocument();
    expect(screen.getByText("当前会话没有可管理的记忆文件")).toBeInTheDocument();
  });

  it("searches @ files into reference chips and sends relative paths without file bodies", async () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);
    act(() => bridge.emit({
      type: "engine/status",
      status: "ready",
      sessionId: "session-42",
      capabilities: { executionProfiles: ["NativeProtected", "WslStrict"] }
    }));
    fireEvent.click(screen.getByRole("button", { name: zhCn.wslStrict }));
    const composer = screen.getByRole("textbox", { name: zhCn.promptPlaceholder });

    fireEvent.change(composer, { target: { value: "检查 @par" } });
    await waitFor(() => {
      expect(commandsOfType(bridge, "workspace/context/file/search")).toHaveLength(1);
    });
    const search = lastCommandOfType(bridge, "workspace/context/file/search");
    expect(search).toMatchObject({ workspaceGeneration: 1, query: "par" });
    act(() => bridge.emit({
      type: "workspace/context/file/search",
      requestId: search.requestId as string,
      workspaceGeneration: 1,
      query: "par",
      files: [{
        relativePath: "src/Parser.cs",
        byteLength: 64,
        lastWriteTime: "2026-07-18T08:30:00Z"
      }]
    }));

    fireEvent.click(screen.getByRole("option", { name: "src/Parser.cs" }));
    expect(screen.getByText("src/Parser.cs")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: zhCn.send }));

    const promptCommand = lastCommandOfType(bridge, "engine/prompt");
    expect(promptCommand).toMatchObject({
      text: "检查\n\n@src/Parser.cs",
      workspaceGeneration: 1
    });
    expect(promptCommand).not.toHaveProperty("attachments");
    expect(JSON.stringify(promptCommand)).not.toContain("class Parser");
    expect(screen.queryByText("src/Parser.cs")).not.toBeInTheDocument();
  });

  it("moves through file reference results with the keyboard and exposes the active option", async () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);
    const composer = screen.getByRole("textbox", { name: zhCn.promptPlaceholder });

    fireEvent.change(composer, { target: { value: "检查 @src" } });
    await waitFor(() => {
      expect(commandsOfType(bridge, "workspace/context/file/search")).toHaveLength(1);
    });
    const search = lastCommandOfType(bridge, "workspace/context/file/search");
    act(() => bridge.emit({
      type: "workspace/context/file/search",
      requestId: search.requestId as string,
      workspaceGeneration: 1,
      query: "src",
      files: [{
        relativePath: "src/First.cs",
        byteLength: 32,
        lastWriteTime: "2026-07-18T08:30:00Z"
      }, {
        relativePath: "src/Second.cs",
        byteLength: 64,
        lastWriteTime: "2026-07-18T08:30:00Z"
      }]
    }));

    const options = screen.getAllByRole("option");
    expect(options[0]).toHaveAttribute("aria-selected", "true");
    expect(options[1]).toHaveAttribute("aria-selected", "false");
    expect(composer).toHaveAttribute("aria-activedescendant", options[0].id);

    composer.focus();
    fireEvent.keyDown(composer, { key: "ArrowDown" });
    expect(options[0]).toHaveAttribute("aria-selected", "false");
    expect(options[1]).toHaveAttribute("aria-selected", "true");
    expect(composer).toHaveAttribute("aria-activedescendant", options[1].id);

    fireEvent.keyDown(composer, { key: "Enter" });
    expect(screen.getByText("src/Second.cs")).toBeInTheDocument();
    expect(composer).toHaveValue("检查");
    expect(composer).toHaveFocus();
  });

  it("shows file search loading, empty, and error states while ignoring superseded results", async () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);
    act(() => bridge.emit({
      type: "engine/status",
      status: "ready",
      sessionId: "session-42",
      capabilities: { executionProfiles: ["NativeProtected", "WslStrict"] }
    }));
    const composer = screen.getByRole("textbox", { name: zhCn.promptPlaceholder });

    fireEvent.change(composer, { target: { value: "检查 @old" } });
    expect(screen.getByText(zhCn.fileSearchLoading)).toBeInTheDocument();
    await waitFor(() => {
      expect(commandsOfType(bridge, "workspace/context/file/search")).toHaveLength(1);
    });
    const oldSearch = lastCommandOfType(bridge, "workspace/context/file/search");

    fireEvent.change(composer, { target: { value: "检查 @new" } });
    await waitFor(() => {
      expect(commandsOfType(bridge, "workspace/context/file/search")).toHaveLength(2);
    });
    const newSearch = lastCommandOfType(bridge, "workspace/context/file/search");
    act(() => bridge.emit({
      type: "workspace/context/file/search",
      requestId: oldSearch.requestId as string,
      workspaceGeneration: 1,
      query: "old",
      files: [{
        relativePath: "src/Old.cs",
        byteLength: 64,
        lastWriteTime: "2026-07-18T08:30:00Z"
      }]
    }));
    expect(screen.queryByText("src/Old.cs")).not.toBeInTheDocument();
    expect(screen.getByText(zhCn.fileSearchLoading)).toBeInTheDocument();

    act(() => bridge.emit({
      type: "workspace/context/file/search",
      requestId: newSearch.requestId as string,
      workspaceGeneration: 1,
      query: "new",
      files: []
    }));
    expect(screen.getByText(zhCn.fileSearchEmpty)).toBeInTheDocument();

    fireEvent.change(composer, { target: { value: "检查 @error" } });
    await waitFor(() => {
      expect(commandsOfType(bridge, "workspace/context/file/search")).toHaveLength(3);
    });
    const failedSearch = lastCommandOfType(bridge, "workspace/context/file/search");
    act(() => bridge.emit({
      type: "workspace/context/error",
      requestId: failedSearch.requestId as string,
      workspaceGeneration: 1,
      operation: "file-search"
    }));
    expect(screen.getByRole("alert")).toHaveTextContent(zhCn.fileSearchError);
  });

  it("offers an accessible Execute and Plan session mode control", () => {
    render(<Workbench />);

    const modeGroup = screen.getByRole("radiogroup", { name: "会话模式" });
    const execute = screen.getByRole("radio", { name: "执行" });
    const plan = screen.getByRole("radio", { name: "计划" });

    expect(modeGroup).toContainElement(execute);
    expect(modeGroup).toContainElement(plan);
    expect(execute).toHaveAttribute("aria-checked", "true");
    expect(plan).toHaveAttribute("aria-checked", "false");

    execute.focus();
    fireEvent.keyDown(execute, { key: "ArrowRight" });
    expect(plan).toHaveAttribute("aria-checked", "true");
    expect(plan).toHaveFocus();

    fireEvent.keyDown(plan, { key: "ArrowLeft" });
    expect(execute).toHaveAttribute("aria-checked", "true");
    expect(execute).toHaveFocus();
  });

  it("disables Plan after the host reports that it is unavailable", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);
    const plan = screen.getByRole("radio", { name: "计划" });

    fireEvent.click(plan);
    expect(plan).toHaveAttribute("aria-checked", "true");

    act(() => bridge.emit({
      type: "session/mode/changed",
      sessionId: "session-42",
      mode: "default",
      planAvailable: false
    } as HostEvent));

    expect(plan).toBeDisabled();
    expect(plan).toHaveAttribute("aria-checked", "false");
    expect(plan).toHaveAccessibleDescription("当前引擎不支持计划模式");
    expect(screen.getByRole("radio", { name: "执行" })).toHaveAttribute("aria-checked", "true");
  });

  it.each(["starting", "running"] as const)(
    "disables session mode switching while the engine is %s",
    (status) => {
      const bridge = new RecordingBridge();
      render(<Workbench bridge={bridge} />);

      act(() => bridge.emit({ type: "engine/status", status, sessionId: "session-42" }));

      expect(screen.getByRole("radio", { name: "执行" })).toBeDisabled();
      expect(screen.getByRole("radio", { name: "计划" })).toBeDisabled();
    }
  );

  it("keeps mode switching disabled until the host confirms the requested mode", async () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);
    const plan = screen.getByRole("radio", { name: "计划" });

    fireEvent.click(plan);
    fireEvent.change(screen.getByPlaceholderText("向 AgentDesk 描述任务"), {
      target: { value: "先制定实施计划" }
    });
    fireEvent.click(screen.getByRole("button", { name: "发送" }));
    fireEvent.click(screen.getByRole("button", { name: "继续本机执行" }));
    await waitForFocusRestore();

    act(() => {
      bridge.emit({
        type: "session/mode/changed",
        sessionId: "session-42",
        mode: "default",
        planAvailable: true
      } as HostEvent);
      bridge.emit({ type: "engine/status", status: "ready", sessionId: "session-42" });
    });
    expect(plan).toBeDisabled();
    expect(screen.getByText("正在确认计划模式")).toBeInTheDocument();

    act(() => bridge.emit({
      type: "session/mode/changed",
      sessionId: "session-42",
      mode: "plan",
      planAvailable: true
    } as HostEvent));

    expect(plan).toBeEnabled();
    expect(screen.queryByText("正在确认计划模式")).not.toBeInTheDocument();
  });

  it("debounces session search for 250 ms and preserves Ctrl+K focus", () => {
    vi.useFakeTimers();
    try {
      const bridge = new RecordingBridge();
      render(<Workbench bridge={bridge} />);
      const search = screen.getByRole("searchbox", { name: "搜索会话" });
      const listCommands = () => sessionListCommandPayloads(bridge);

      expect(listCommands()).toEqual([{ type: "session/list", query: "", limit: 50 }]);
      fireEvent.keyDown(document, { key: "k", ctrlKey: true });
      expect(search).toHaveFocus();

      fireEvent.change(search, { target: { value: "登" } });
      fireEvent.change(search, { target: { value: "登录" } });
      act(() => vi.advanceTimersByTime(249));
      expect(listCommands()).toHaveLength(1);

      act(() => vi.advanceTimersByTime(1));
      expect(listCommands()).toEqual([
        { type: "session/list", query: "", limit: 50 },
        { type: "session/list", query: "登录", limit: 50 }
      ]);
    } finally {
      vi.useRealTimers();
    }
  });

  it("renders an empty state after the host returns no sessions", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);

    act(() => bridge.emit({
      type: "session/list/changed",
      sessions: []
    } as HostEvent));

    expect(screen.getByText("没有会话")).toBeInTheDocument();
    expect(screen.queryByText("正在加载会话")).not.toBeInTheDocument();
  });

  it("switches between active and archived sessions with an accessible tablist", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);
    const active = screen.getByRole("tab", { name: "活动" });
    const archived = screen.getByRole("tab", { name: "已归档" });

    expect(screen.getByRole("tablist", { name: "会话视图" })).toBeInTheDocument();
    expect(active).toHaveAttribute("aria-selected", "true");
    expect(archived).toHaveAttribute("aria-selected", "false");

    active.focus();
    fireEvent.keyDown(active, { key: "ArrowRight" });

    expect(archived).toHaveFocus();
    expect(archived).toHaveAttribute("aria-selected", "true");
    expect(sessionListCommandPayloads(bridge)).toEqual([
      { type: "session/list", query: "", limit: 50 },
      { type: "session/list", query: "", limit: 50, archived: true }
    ]);
  });

  it("renders host sessions with workspace, branch, and message metadata", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);

    act(() => bridge.emit(sessionListChanged([
      sessionSummary({
        sessionId: "session-42",
        title: "检查登录流程",
        workspacePath: "C:\\work\\agentdesk",
        messageCount: 12,
        branch: "feature/login"
      }),
      sessionSummary({
        sessionId: "session-43",
        title: "修复终端输出",
        workspacePath: "D:\\code\\terminal",
        messageCount: 3,
        worktreeLabel: "terminal-fix"
      })
    ])));

    expect(screen.getByRole("button", { name: "打开会话：检查登录流程" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "打开会话：修复终端输出" })).toBeInTheDocument();
    expect(screen.getByText("agentdesk · 12 条消息")).toBeInTheDocument();
    expect(screen.getByText("feature/login")).toBeInTheDocument();
    expect(screen.getByText("terminal-fix")).toBeInTheDocument();
  });

  it("virtualizes 10,000 sessions without losing distant keyboard selection or list semantics", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);
    const sessions = Array.from({ length: 10_000 }, (_, index) => sessionSummary({
      sessionId: `session-${index + 1}`,
      title: `会话 ${index + 1}`,
      messageCount: index + 1
    }));

    act(() => bridge.emit(sessionListChanged(sessions)));

    const viewport = document.querySelector<HTMLElement>(".session-list");
    expect(viewport).not.toBeNull();
    expect(viewport!.querySelectorAll(".session-item").length).toBeGreaterThan(0);
    expect(viewport!.querySelectorAll(".session-item").length).toBeLessThanOrEqual(20);
    const list = screen.getByRole("list", { name: "会话" });
    expect(screen.queryByRole("button", { name: "打开会话：会话 10000" })).not.toBeInTheDocument();

    Object.defineProperty(viewport!, "clientHeight", { configurable: true, value: 480 });
    fireEvent.scroll(viewport!, { target: { scrollTop: 10_000 * 60 } });

    const lastSession = screen.getByRole("button", { name: "打开会话：会话 10000" });
    const lastListItem = lastSession.closest("[role='listitem']");
    expect(lastListItem).toHaveAttribute("aria-posinset", "10000");
    expect(lastListItem).toHaveAttribute("aria-setsize", "10000");
    expect(within(list).getAllByRole("listitem").length).toBeLessThanOrEqual(20);

    lastSession.focus();
    fireEvent.keyDown(lastSession, { key: "Enter" });
    expect(bridge.commands).toContainEqual({
      type: "session/open",
      sessionId: "session-10000",
      workspacePath: "C:\\workspace",
      executionProfile: "NativeProtected"
    });
    act(() => bridge.emit({
      type: "session/active/changed",
      sessionId: "session-10000",
      workspacePath: "C:\\workspace"
    } as HostEvent));
    expect(lastSession).toHaveAttribute("aria-current", "page");

    fireEvent.keyDown(lastSession, { key: "Home" });
    expect(screen.getByRole("button", { name: "打开会话：会话 1" })).toHaveFocus();
    expect(screen.queryByRole("button", { name: "打开会话：会话 10000" })).not.toBeInTheDocument();
  }, 30_000);

  it("keeps a shorter authoritative virtual list replacement visible after distant scrolling", () => {
    const sessions = Array.from(
      { length: 10_000 },
      (_, index) => ({ sessionId: `session-${index + 1}`, title: `会话 ${index + 1}` })
    );
    const renderList = (items: typeof sessions) => (
      <VirtualizedList
        className="session-list"
        ariaLabel="会话"
        items={items}
        getKey={(item) => item.sessionId}
        overscan={4}
        rowHeight={60}
        renderItem={(item, index) => (
          <div className="session-item">
            <button type="button" data-virtual-index={index}>{item.title}</button>
          </div>
        )}
      />
    );
    const view = render(renderList(sessions));
    const viewport = document.querySelector<HTMLElement>(".session-list")!;
    fireEvent.scroll(viewport, { target: { scrollTop: 10_000 * 60 } });
    expect(screen.getByRole("button", { name: "会话 10000" })).toBeInTheDocument();

    view.rerender(renderList([{ sessionId: "replacement", title: "权威替换会话" }]));

    expect(screen.getByRole("button", { name: "权威替换会话" })).toBeInTheDocument();
    expect(viewport.querySelectorAll(".session-item")).toHaveLength(1);
  });

  it("keeps the focused session mounted across pointer scrolling and keyboard continuation", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);
    act(() => bridge.emit(sessionListChanged(Array.from(
      { length: 10_000 },
      (_, index) => sessionSummary({ sessionId: `session-${index + 1}`, title: `会话 ${index + 1}` })
    ))));
    const viewport = document.querySelector<HTMLElement>(".session-list")!;
    const firstSession = screen.getByRole("button", { name: "打开会话：会话 1" });
    firstSession.focus();

    fireEvent.scroll(viewport, { target: { scrollTop: 10_000 * 60 } });

    expect(firstSession).toHaveFocus();
    expect(firstSession.isConnected).toBe(true);
    expect(viewport.querySelectorAll(".session-item").length).toBeLessThanOrEqual(21);
    fireEvent.keyDown(firstSession, { key: "ArrowDown" });
    expect(screen.getByRole("button", { name: "打开会话：会话 2" })).toHaveFocus();
  }, 30_000);

  it("pins the focused session by stable key when the authoritative order changes", () => {
    const sessions = Array.from(
      { length: 100 },
      (_, index) => ({ sessionId: `session-${index + 1}`, title: `会话 ${index + 1}` })
    );
    const renderList = (items: typeof sessions) => (
      <VirtualizedList
        ariaLabel="会话"
        items={items}
        getKey={(item) => item.sessionId}
        overscan={4}
        rowHeight={60}
        renderItem={(item, index) => (
          <button type="button" data-virtual-index={index}>{item.title}</button>
        )}
      />
    );
    const view = render(renderList(sessions));
    const list = screen.getByRole("list", { name: "会话" });
    const firstSession = screen.getByRole("button", { name: "会话 1" });
    firstSession.focus();

    view.rerender(renderList([...sessions.slice(1), sessions[0]]));

    expect(firstSession).toHaveFocus();
    expect(firstSession.closest("[role='listitem']")).toHaveAttribute("aria-posinset", "100");
    expect(within(list).getAllByRole("listitem").length).toBeLessThanOrEqual(13);
    fireEvent.keyDown(firstSession, { key: "ArrowUp" });
    expect(screen.getByRole("button", { name: "会话 100" })).toHaveFocus();
    expect(within(list).getAllByRole("listitem").length).toBeLessThanOrEqual(13);
  });

  it("marks only the host-confirmed active session as current", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);
    act(() => {
      bridge.emit(sessionListChanged([
        sessionSummary({ sessionId: "session-42", title: "会话 A" }),
        sessionSummary({ sessionId: "session-43", title: "会话 B" })
      ]));
      bridge.emit({
        type: "session/active/changed",
        sessionId: "session-43",
        workspacePath: "C:\\workspace"
      } as HostEvent);
    });

    expect(screen.getByRole("button", { name: "打开会话：会话 A" })).not.toHaveAttribute(
      "aria-current"
    );
    expect(screen.getByRole("button", { name: "打开会话：会话 B" })).toHaveAttribute(
      "aria-current",
      "page"
    );
    expect(screen.getByRole("heading", { name: "会话 B" })).toBeInTheDocument();
  });

  it("opens a session by double click or Enter using the selected execution profile", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);
    act(() => {
      bridge.emit({
        type: "engine/status",
        status: "ready",
        capabilities: { executionProfiles: ["NativeProtected", "WslStrict"] }
      });
      bridge.emit(sessionListChanged([
        sessionSummary({
          sessionId: "session-42",
          title: "检查登录流程",
          workspacePath: "D:\\work\\login"
        })
      ]));
    });
    fireEvent.click(screen.getByRole("button", { name: "WSL2 严格模式" }));
    const row = screen.getByRole("button", { name: "打开会话：检查登录流程" });

    fireEvent.doubleClick(row);
    fireEvent.keyDown(row, { key: "Enter" });

    expect(bridge.commands.filter((command) => command.type === "session/open")).toEqual([
      {
        type: "session/open",
        sessionId: "session-42",
        workspacePath: "D:\\work\\login",
        executionProfile: "WslStrict"
      },
      {
        type: "session/open",
        sessionId: "session-42",
        workspacePath: "D:\\work\\login",
        executionProfile: "WslStrict"
      }
    ]);
  });

  it("forks a listed session and opens the authoritative fork after refreshing", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);
    act(() => bridge.emit(sessionListChanged([
      sessionSummary({
        sessionId: "session-42",
        title: "源会话",
        workspacePath: "C:\\workspace"
      })
    ])));

    fireEvent.click(screen.getByRole("button", { name: "分叉：源会话" }));
    expect(bridge.commands).toContainEqual({
      type: "session/fork",
      sessionId: "session-42",
      sourceWorkspacePath: "C:\\workspace",
      targetWorkspacePath: "C:\\workspace"
    });

    act(() => bridge.emit({
      type: "session/forked",
      sessionId: "fork-session",
      workspacePath: "C:\\workspace",
      parentSessionId: "session-42",
      chatMessagesCopied: 4,
      updatesCopied: 9,
      planStateCopied: true
    }));

    expect(bridge.commands.filter((command) => command.type === "session/list").at(-1)).toEqual(
      expect.objectContaining({
      type: "session/list",
      requestId: expect.stringMatching(maintenanceRequestIdPattern),
      query: "",
      limit: 50
      }));
    expect(bridge.commands).toContainEqual({
      type: "session/open",
      sessionId: "fork-session",
      workspacePath: "C:\\workspace",
      executionProfile: "NativeProtected"
    });
  });

  it("compacts the active session from the conversation toolbar", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);
    act(() => {
      bridge.emit(sessionListChanged([
        sessionSummary({ sessionId: "session-42", title: "活动会话" })
      ]));
      bridge.emit({
        type: "session/active/changed",
        sessionId: "session-42",
        workspacePath: "C:\\workspace",
        engineEpoch: 0
      });
      bridge.emit({ type: "engine/status", status: "ready", sessionId: "session-42" });
    });

    const compact = screen.getByRole("button", { name: "压缩会话" });
    fireEvent.click(compact);
    fireEvent.click(compact);

    expect(bridge.commands.filter((command) => command.type === "session/compact")).toEqual([{
      type: "session/compact",
      sessionId: "session-42"
    }]);
    act(() => bridge.emit({ type: "session/compacted", sessionId: "session-42" }));
    expect(screen.getByText("会话已压缩")).toHaveAttribute("role", "status");
  });

  it("requires a second explicit Force action when rewind reports conflicts", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);
    act(() => {
      bridge.emit({
        type: "session/active/changed",
        sessionId: "session-42",
        workspacePath: "C:\\workspace",
        engineEpoch: 0
      });
      bridge.emit({ type: "engine/status", status: "ready", sessionId: "session-42" });
    });

    fireEvent.click(screen.getByRole("button", { name: "回退会话" }));
    expect(bridge.commands).toContainEqual({
      type: "session/rewind/points",
      sessionId: "session-42"
    });
    expect(screen.getByRole("dialog", { name: "回退会话" })).toBeInTheDocument();

    act(() => bridge.emit({
      type: "session/rewind/points",
      sessionId: "session-42",
      points: [{
        promptIndex: 3,
        createdAt: "2026-07-16T09:30:00Z",
        fileSnapshotCount: 2,
        hasFileChanges: true,
        promptPreview: "重构解析器"
      }]
    }));
    expect(screen.getByRole("radio", { name: /重构解析器/ })).toHaveAttribute(
      "aria-checked",
      "true"
    );
    fireEvent.click(screen.getByRole("radio", { name: "仅对话" }));
    fireEvent.click(screen.getByRole("button", { name: "回退到此处" }));

    expect(bridge.commands).toContainEqual({
      type: "session/rewind",
      sessionId: "session-42",
      targetPromptIndex: 3,
      mode: "conversation_only",
      force: false
    });
    expect(bridge.commands).not.toContainEqual(expect.objectContaining({
      type: "session/rewind",
      force: true
    }));

    act(() => bridge.emit({
      type: "session/rewound",
      sessionId: "session-42",
      success: false,
      targetPromptIndex: 3,
      mode: "conversation_only",
      revertedFiles: [],
      cleanFiles: ["src/parser.rs"],
      conflicts: [{ path: "src/parser.rs", conflictType: "content_mismatch" }],
      error: "文件已在检查点后修改"
    }));

    expect(screen.getByRole("alert")).toHaveTextContent("src/parser.rs");
    fireEvent.click(screen.getByRole("button", { name: "强制回退" }));
    expect(bridge.commands).toContainEqual({
      type: "session/rewind",
      sessionId: "session-42",
      targetPromptIndex: 3,
      mode: "conversation_only",
      force: true
    });
  });

  it("shows rewind point failures locally without changing the ready engine status", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);
    act(() => {
      bridge.emit({
        type: "session/active/changed",
        sessionId: "session-42",
        workspacePath: "C:\\workspace",
        engineEpoch: 0
      });
      bridge.emit({ type: "engine/status", status: "ready", sessionId: "session-42" });
    });

    fireEvent.click(screen.getByRole("button", { name: "回退会话" }));
    const dialog = screen.getByRole("dialog", { name: "回退会话" });
    expect(within(dialog).getByRole("status")).toBeInTheDocument();
    act(() => bridge.emit({
      type: "session/rewind/points/error",
      sessionId: "session-42",
      message: "无法读取回退检查点。"
    } as unknown as HostEvent));

    expect(within(dialog).queryByRole("status")).not.toBeInTheDocument();
    expect(screen.getByText("无法读取回退检查点。")).toBeInTheDocument();
    expect(screen.queryByText("引擎错误")).not.toBeInTheDocument();
  });

  it("trims transcript after a successful rewind and restores the target prompt", async () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);
    act(() => {
      bridge.emit({
        type: "session/active/changed",
        sessionId: "session-42",
        workspacePath: "C:\\workspace",
        engineEpoch: 0
      });
      bridge.emit({
        type: "engine/status",
        status: "ready",
        sessionId: "session-42",
        capabilities: { executionProfiles: ["NativeProtected", "WslStrict"] }
      });
    });
    fireEvent.click(screen.getByRole("button", { name: "WSL2 严格模式" }));
    const composer = screen.getByPlaceholderText("向 AgentDesk 描述任务");
    for (const text of ["保留的第一步", "需要重做的第二步", "移除的第三步"]) {
      fireEvent.change(composer, { target: { value: text } });
      fireEvent.click(screen.getByRole("button", { name: "发送" }));
      act(() => bridge.emit({
        type: "prompt/completed",
        sessionId: "session-42",
        stopReason: "end_turn"
      }));
    }

    fireEvent.click(screen.getByRole("button", { name: "回退会话" }));
    act(() => bridge.emit({
      type: "session/rewind/points",
      sessionId: "session-42",
      points: [{
        promptIndex: 1,
        createdAt: "2026-07-16T09:30:00Z",
        fileSnapshotCount: 0,
        hasFileChanges: false,
        promptPreview: "需要重做的第二步"
      }]
    }));
    fireEvent.click(screen.getByRole("radio", { name: "仅对话" }));
    fireEvent.click(screen.getByRole("button", { name: "回退到此处" }));
    act(() => bridge.emit({
      type: "session/rewound",
      sessionId: "session-42",
      success: true,
      targetPromptIndex: 1,
      mode: "conversation_only",
      revertedFiles: [],
      cleanFiles: [],
      conflicts: [],
      promptText: "需要重做的第二步"
    }));
    await waitForFocusRestore();

    expect(Array.from(document.querySelectorAll(".user-message")).map(
      (message) => message.textContent
    )).toEqual(["你保留的第一步"]);
    expect(composer).toHaveValue("需要重做的第二步");
    expect(composer).toHaveFocus();
    expect(screen.queryByRole("dialog", { name: "回退会话" })).not.toBeInTheDocument();
  });

  it("renames a session from its action button", async () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);
    act(() => bridge.emit(sessionListChanged([
      sessionSummary({
        sessionId: "session-42",
        title: "旧标题",
        workspacePath: "C:\\workspace"
      })
    ])));

    fireEvent.click(screen.getByRole("button", { name: "重命名：旧标题" }));
    const titleInput = screen.getByRole("textbox", { name: "会话标题" });
    await act(async () => Promise.resolve());
    expect(titleInput).toHaveFocus();
    fireEvent.change(titleInput, { target: { value: "  新标题  " } });
    fireEvent.keyDown(titleInput, { key: "Enter" });

    expect(bridge.commands).toContainEqual(expect.objectContaining({
      type: "session/rename",
      requestId: expect.stringMatching(maintenanceRequestIdPattern),
      sessionId: "session-42",
      title: "新标题",
      workspacePath: "C:\\workspace"
    }));
    expect(screen.queryByRole("textbox", { name: "会话标题" })).not.toBeInTheDocument();
    await waitForFocusRestore();
    const restoredRename = screen.getByRole("button", { name: "重命名：旧标题" });
    expect(restoredRename).toHaveFocus();

    fireEvent.click(restoredRename);
    await waitForFocusRestore();
    fireEvent.click(screen.getByRole("button", { name: "取消重命名" }));
    await waitForFocusRestore();
    expect(screen.getByRole("button", { name: "重命名：旧标题" })).toHaveFocus();
  });

  it("opens inline rename from the session context menu", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);
    act(() => bridge.emit(sessionListChanged([
      sessionSummary({ sessionId: "session-42", title: "上下文会话" })
    ])));

    fireEvent.contextMenu(screen.getByRole("button", { name: "打开会话：上下文会话" }));

    expect(screen.getByRole("textbox", { name: "会话标题" })).toHaveValue("上下文会话");
  });

  it("loads the next session page and appends unique rows", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);
    act(() => bridge.emit(sessionListChanged([
      sessionSummary({ sessionId: "session-42", title: "第一页" })
    ], "page-2")));

    fireEvent.click(screen.getByRole("button", { name: "加载更多" }));
    expect(bridge.commands).toContainEqual(expect.objectContaining({
      type: "session/list",
      requestId: expect.stringMatching(maintenanceRequestIdPattern),
      query: "",
      cursor: "page-2",
      limit: 50
    }));

    act(() => bridge.emit(sessionListChanged([
      sessionSummary({ sessionId: "session-42", title: "第一页更新" }),
      sessionSummary({ sessionId: "session-43", title: "第二页" })
    ])));

    expect(screen.getByRole("button", { name: "打开会话：第一页更新" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "打开会话：第二页" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "打开会话：第一页" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "加载更多" })).not.toBeInTheDocument();
  });

  it("keeps later session pages reachable when the current page is empty", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);
    const request = lastCommandOfType(bridge, "session/list");
    const requestId = typeof request.requestId === "string"
      ? request.requestId
      : "missing-session-request";

    act(() => bridge.emit(sessionListChanged([], "page-2", requestId)));

    fireEvent.click(screen.getByRole("button", { name: "加载更多" }));
    expect(lastCommandOfType(bridge, "session/list")).toEqual(expect.objectContaining({
      type: "session/list",
      requestId: expect.stringMatching(maintenanceRequestIdPattern),
      query: "",
      cursor: "page-2",
      limit: 50
    }));
  });

  it("shows a matching session list failure", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);
    const request = lastCommandOfType(bridge, "session/list");
    const requestId = typeof request.requestId === "string"
      ? request.requestId
      : "missing-session-request";

    act(() => bridge.emit({
      type: "session/list/error",
      requestId,
      message: "无法加载会话。"
    } as HostEvent));

    expect(screen.getByRole("alert")).toHaveTextContent("无法加载会话。");
  });

  it("keeps the current session list request across an unrelated engine error", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);
    const request = lastCommandOfType(bridge, "session/list");
    const requestId = typeof request.requestId === "string"
      ? request.requestId
      : "missing-session-request";

    act(() => {
      bridge.emit({ type: "engine/status", status: "error", message: "其他引擎操作失败" });
      bridge.emit(sessionListChanged([
        sessionSummary({ sessionId: "session-current", title: "当前列表结果" })
      ], undefined, requestId));
    });

    expect(screen.getByRole("button", { name: "打开会话：当前列表结果" })).toBeInTheDocument();
  });

  it("ignores a stale session list failure without clearing a newer search", () => {
    vi.useFakeTimers();
    try {
      const bridge = new RecordingBridge();
      render(<Workbench bridge={bridge} />);
      const initialRequest = lastCommandOfType(bridge, "session/list");
      const initialRequestId = typeof initialRequest.requestId === "string"
        ? initialRequest.requestId
        : "missing-initial-request";
      act(() => bridge.emit(sessionListChanged([
        sessionSummary({ sessionId: "session-page-1", title: "旧查询第一页" })
      ], "page-2", initialRequestId)));
      fireEvent.click(screen.getByRole("button", { name: "加载更多" }));
      const paginationRequest = lastCommandOfType(bridge, "session/list");
      const paginationRequestId = typeof paginationRequest.requestId === "string"
        ? paginationRequest.requestId
        : "missing-pagination-request";

      fireEvent.change(screen.getByRole("searchbox", { name: "搜索会话" }), {
        target: { value: "new-query" }
      });
      act(() => vi.advanceTimersByTime(250));
      const searchRequest = lastCommandOfType(bridge, "session/list");
      const searchRequestId = typeof searchRequest.requestId === "string"
        ? searchRequest.requestId
        : "missing-search-request";

      act(() => {
        bridge.emit({
          type: "session/list/error",
          requestId: paginationRequestId,
          message: "过期分页失败"
        } as HostEvent);
        bridge.emit(sessionListChanged([
          sessionSummary({ sessionId: "session-search", title: "新搜索结果" })
        ], undefined, searchRequestId));
      });

      expect(screen.getByRole("button", { name: "打开会话：新搜索结果" })).toBeInTheDocument();
      expect(screen.queryByText("过期分页失败")).not.toBeInTheDocument();
    } finally {
      vi.useRealTimers();
    }
  });

  it("ignores a stale pagination response after a newer search response", () => {
    vi.useFakeTimers();
    try {
      const bridge = new RecordingBridge();
      render(<Workbench bridge={bridge} />);
      const initialRequest = lastCommandOfType(bridge, "session/list");
      const initialRequestId = typeof initialRequest.requestId === "string"
        ? initialRequest.requestId
        : "missing-initial-request";
      act(() => bridge.emit(sessionListChanged([
        sessionSummary({ sessionId: "session-page-1", title: "旧查询第一页" })
      ], "page-2", initialRequestId)));
      fireEvent.click(screen.getByRole("button", { name: "加载更多" }));
      const paginationRequest = lastCommandOfType(bridge, "session/list");
      const paginationRequestId = typeof paginationRequest.requestId === "string"
        ? paginationRequest.requestId
        : "missing-pagination-request";

      fireEvent.change(screen.getByRole("searchbox", { name: "搜索会话" }), {
        target: { value: "new-query" }
      });
      act(() => vi.advanceTimersByTime(250));
      const searchRequest = lastCommandOfType(bridge, "session/list");
      const searchRequestId = typeof searchRequest.requestId === "string"
        ? searchRequest.requestId
        : "missing-search-request";

      act(() => {
        bridge.emit(sessionListChanged([
          sessionSummary({ sessionId: "session-search", title: "新搜索结果" })
        ], undefined, searchRequestId));
        bridge.emit(sessionListChanged([
          sessionSummary({ sessionId: "session-stale", title: "过期分页结果" })
        ], undefined, paginationRequestId));
      });

      expect(screen.getByRole("button", { name: "打开会话：新搜索结果" })).toBeInTheDocument();
      expect(screen.queryByRole("button", { name: "打开会话：过期分页结果" }))
        .not.toBeInTheDocument();
    } finally {
      vi.useRealTimers();
    }
  });

  it("ignores an uncorrelated mutation refresh for the active search", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);
    const request = lastCommandOfType(bridge, "session/list");
    const requestId = typeof request.requestId === "string"
      ? request.requestId
      : "missing-session-request";
    act(() => bridge.emit(sessionListChanged([
      sessionSummary({ sessionId: "session-search", title: "活动搜索结果" })
    ], undefined, requestId)));

    act(() => bridge.emit(sessionListChanged([
      sessionSummary({ sessionId: "session-mutation", title: "无关联刷新" })
    ]), false));

    expect(screen.getByRole("button", { name: "打开会话：活动搜索结果" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "打开会话：无关联刷新" }))
      .not.toBeInTheDocument();
  });

  it("merge-updates existing renamed sessions from an uncorrelated mutation refresh", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);
    const request = lastCommandOfType(bridge, "session/list");
    const requestId = typeof request.requestId === "string"
      ? request.requestId
      : "missing-session-request";
    act(() => bridge.emit(sessionListChanged([
      sessionSummary({ sessionId: "session-42", title: "重命名前" })
    ], "page-2", requestId)));

    act(() => bridge.emit(sessionListChanged([
      sessionSummary({ sessionId: "session-42", title: "重命名后" }),
      sessionSummary({ sessionId: "session-unrelated", title: "无关会话" })
    ]), false));

    expect(screen.getByRole("button", { name: "打开会话：重命名后" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "打开会话：无关会话" })).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "加载更多" })).toBeInTheDocument();
  });

  it("preserves archived filtering across search and pagination", () => {
    vi.useFakeTimers();
    try {
      const bridge = new RecordingBridge();
      render(<Workbench bridge={bridge} />);
      fireEvent.change(screen.getByRole("searchbox", { name: "搜索会话" }), {
        target: { value: "parser" }
      });
      act(() => vi.advanceTimersByTime(250));
      fireEvent.click(screen.getByRole("tab", { name: "已归档" }));

      expect(bridge.commands.filter((command) => command.type === "session/list")).toContainEqual(
        expect.objectContaining({
        type: "session/list",
        requestId: expect.stringMatching(maintenanceRequestIdPattern),
        query: "parser",
        limit: 50,
        archived: true
        }));

      act(() => bridge.emit(sessionListChanged([
        sessionSummary({ sessionId: "session-archived", title: "归档解析器会话" })
      ], "archive-page-2")));
      fireEvent.click(screen.getByRole("button", { name: "加载更多" }));

      expect(bridge.commands).toContainEqual(expect.objectContaining({
        type: "session/list",
        requestId: expect.stringMatching(maintenanceRequestIdPattern),
        query: "parser",
        cursor: "archive-page-2",
        limit: 50,
        archived: true
      }));
    } finally {
      vi.useRealTimers();
    }
  });

  it("archives an active session after authoritative confirmation and refreshes the view", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);
    act(() => bridge.emit(sessionListChanged([
      sessionSummary({ sessionId: "session-42", title: "待归档会话" })
    ])));
    const archive = screen.getByRole("button", { name: "归档：待归档会话" });

    archive.focus();
    fireEvent.click(archive);
    expect(screen.getByRole("button", { name: "打开会话：待归档会话" })).toBeInTheDocument();
    expect(bridge.commands).toContainEqual(expect.objectContaining({
      type: "session/archive",
      requestId: expect.stringMatching(maintenanceRequestIdPattern),
      sessionId: "session-42",
      archived: true
    }));
    const archiveRequest = lastCommandOfType(bridge, "session/archive");
    const archiveRequestId = typeof archiveRequest.requestId === "string"
      ? archiveRequest.requestId
      : "missing-archive-request";

    act(() => bridge.emit({
      type: "session/archive/changed",
      requestId: archiveRequestId,
      sessionId: "session-42",
      archived: true
    } as HostEvent));

    expect(screen.queryByRole("button", { name: "打开会话：待归档会话" })).not.toBeInTheDocument();
    expect(screen.getByRole("searchbox", { name: "搜索会话" })).toHaveFocus();
    expect(bridge.commands.filter((command) => command.type === "session/list").at(-1)).toEqual(
      expect.objectContaining({
      type: "session/list",
      requestId: expect.stringMatching(maintenanceRequestIdPattern),
      query: "",
      limit: 50
      }));
  });

  it("restores an archived session and refreshes the archived view", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);
    fireEvent.click(screen.getByRole("tab", { name: "已归档" }));
    act(() => bridge.emit(sessionListChanged([
      sessionSummary({ sessionId: "session-42", title: "待恢复会话" })
    ])));

    fireEvent.click(screen.getByRole("button", { name: "恢复：待恢复会话" }));
    expect(bridge.commands).toContainEqual(expect.objectContaining({
      type: "session/archive",
      requestId: expect.stringMatching(maintenanceRequestIdPattern),
      sessionId: "session-42",
      archived: false
    }));
    const restoreRequest = lastCommandOfType(bridge, "session/archive");
    const restoreRequestId = typeof restoreRequest.requestId === "string"
      ? restoreRequest.requestId
      : "missing-restore-request";

    act(() => bridge.emit({
      type: "session/archive/changed",
      requestId: restoreRequestId,
      sessionId: "session-42",
      archived: false
    } as HostEvent));

    expect(screen.queryByRole("button", { name: "打开会话：待恢复会话" })).not.toBeInTheDocument();
    expect(bridge.commands.filter((command) => command.type === "session/list").at(-1)).toEqual(
      expect.objectContaining({
      type: "session/list",
      requestId: expect.stringMatching(maintenanceRequestIdPattern),
      query: "",
      limit: 50,
      archived: true
      }));
  });

  it("keeps a permission request open when an archive operation fails", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);
    act(() => bridge.emit(sessionListChanged([
      sessionSummary({ sessionId: "session-42", title: "待归档会话" })
    ])));

    fireEvent.click(screen.getByRole("button", { name: "归档：待归档会话" }));
    const archive = lastCommandOfType(bridge, "session/archive");
    const requestId = typeof archive.requestId === "string"
      ? archive.requestId
      : "missing-archive-request";
    act(() => {
      bridge.emit(permissionRequest("permission-archive", "仍需批准的操作"));
      bridge.emit({
        type: "session/operation/error",
        requestId,
        operation: "archive",
        sessionId: "session-42",
        message: "无法归档会话。"
      } as HostEvent);
    });

    expect(screen.getByRole("dialog", { name: "权限审批" })).toBeInTheDocument();
    expect(screen.getByText("仍需批准的操作")).toBeInTheDocument();
    expect(document.querySelector(".session-archive-button")).toBeEnabled();
    expect(screen.getByText("无法归档会话。")).toBeInTheDocument();
  });

  it("shows a recoverable session list error", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);
    const request = lastCommandOfType(bridge, "session/list");
    const requestId = typeof request.requestId === "string"
      ? request.requestId
      : "missing-session-request";

    act(() => bridge.emit({
      type: "session/list/error",
      requestId,
      message: "无法读取会话索引"
    } as HostEvent));
    expect(screen.getByRole("alert")).toHaveTextContent("无法读取会话索引");

    fireEvent.click(screen.getByRole("button", { name: "重试" }));
    expect(sessionListCommandPayloads(bridge)).toEqual([
      { type: "session/list", query: "", limit: 50 },
      { type: "session/list", query: "", limit: 50 }
    ]);
  });

  it("renders streamed Markdown and code without executing raw HTML", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);

    act(() => bridge.emit({
      type: "session/update",
      sessionId: "session-42",
      updateKind: "agent_message_chunk",
      engineEpoch: 0,
      text: [
        "## 构建结果",
        "",
        "- [x] 测试通过",
        "",
        "```ts",
        "const ready = true;",
        "```",
        "",
        "<script>window.__agentDeskInjected = true</script>"
      ].join("\n")
    }));

    expect(screen.getByRole("heading", { name: "构建结果", level: 2 })).toBeInTheDocument();
    expect(screen.getByText("const ready = true;")).toBeInTheDocument();
    expect(screen.getByRole("checkbox")).toBeChecked();
    expect(document.querySelector(".markdown-body script")).not.toBeInTheDocument();
    expect(screen.queryByText(/agentDeskInjected/)).not.toBeInTheDocument();
  });

  it("ignores a session update from an older engine epoch", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);

    act(() => {
      bridge.emit({
        type: "session/active/changed",
        sessionId: "current-session",
        workspacePath: "C:\\workspace",
        engineEpoch: 2
      } as HostEvent);
      bridge.emit({
        type: "session/update",
        sessionId: "stale-session",
        updateKind: "agent_message_chunk",
        text: "must not render",
        engineEpoch: 1
      } as HostEvent);
    });

    expect(screen.queryByText("must not render")).not.toBeInTheDocument();
    expect(document.querySelector(".message.assistant-message")).toBeNull();
  });

  it("ignores an engine status from an older engine epoch", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);

    act(() => {
      bridge.emit({
        type: "session/active/changed",
        sessionId: "current-session",
        workspacePath: "C:\\workspace",
        engineEpoch: 2
      } as HostEvent);
      bridge.emit({
        type: "engine/status",
        status: "ready",
        sessionId: "stale-session",
        engineEpoch: 1
      } as HostEvent);
    });
    fireEvent.click(screen.getByRole("button", { name: zhCn.settings }));

    expect(lastCommandOfType(bridge, "extensions/list")).toEqual(expect.objectContaining({
      sessionId: "current-session"
    }));
  });

  it("projects tool call updates into a live timeline", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);

    act(() => {
      bridge.emit({
        type: "session/update",
        sessionId: "session-42",
        updateKind: "tool_call",
        engineEpoch: 0,
        update: {
          toolCallId: "tool-1",
          title: "运行 Web 测试",
          kind: "execute",
          status: "in_progress",
          rawInput: { command: "npm", args: ["test"] }
        }
      });
      bridge.emit({
        type: "session/update",
        sessionId: "session-42",
        updateKind: "tool_call_update",
        engineEpoch: 0,
        update: { toolCallId: "tool-1", status: "completed" }
      });
    });

    const timeline = screen.getByRole("group", { name: "工具调用：运行 Web 测试" });
    expect(timeline).toHaveTextContent("已完成");
    expect(timeline).toHaveTextContent("npm test");
  });

  it("enables WSL strict mode only when the host reports the capability", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);
    const wslButton = screen.getByRole("button", { name: "WSL2 严格模式" });

    expect(wslButton).toBeDisabled();
    expect(screen.getByText("当前桌面宿主未提供 WSL2 严格模式")).toBeInTheDocument();

    act(() => bridge.emit({
      type: "engine/status",
      status: "ready",
      capabilities: { executionProfiles: ["NativeProtected", "WslStrict"] }
    }));
    fireEvent.click(wslButton);
    fireEvent.change(screen.getByPlaceholderText("向 AgentDesk 描述任务"), {
      target: { value: "在 WSL 中运行测试" }
    });
    fireEvent.click(screen.getByRole("button", { name: "发送" }));

    expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument();
    expect(bridge.commands).toContainEqual({
      type: "engine/prompt",
      text: "在 WSL 中运行测试",
      executionProfile: "WslStrict",
      sessionMode: "default",
      nativeRiskAcknowledged: false,
      workspaceGeneration: 1
    });
  });

  it("traps keyboard focus inside the settings dialog", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);
    fireEvent.click(screen.getByRole("button", { name: "设置" }));
    const close = screen.getByRole("button", { name: "关闭" });
    const save = screen.getByRole("button", { name: "保存提供商" });

    save.focus();
    fireEvent.keyDown(document, { key: "Tab" });
    expect(close).toHaveFocus();

    close.focus();
    fireEvent.keyDown(document, { key: "Tab", shiftKey: true });
    expect(save).toHaveFocus();
  });

  it("keeps the host modal gate open across dialog handoffs", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);
    const modalCommands = () => bridge.commands.filter(
      (command): command is Extract<HostCommand, { type: "ui/modal" }> =>
        command.type === "ui/modal"
    );

    fireEvent.click(screen.getByRole("button", { name: "设置" }));
    expect(modalCommands()).toEqual([{ type: "ui/modal", isOpen: true }]);

    act(() => bridge.emit(permissionRequest("permission-handoff", "交接期间的操作")));
    fireEvent.click(screen.getByRole("button", { name: "关闭" }));
    expect(screen.getByRole("dialog", { name: "权限审批" })).toBeInTheDocument();
    expect(modalCommands()).toEqual([{ type: "ui/modal", isOpen: true }]);

    fireEvent.click(screen.getByRole("button", { name: "取消审批" }));
    expect(modalCommands()).toEqual([
      { type: "ui/modal", isOpen: true },
      { type: "ui/modal", isOpen: false }
    ]);

    fireEvent.change(screen.getByPlaceholderText("向 AgentDesk 描述任务"), {
      target: { value: "验证风险确认" }
    });
    fireEvent.click(screen.getByRole("button", { name: "发送" }));
    fireEvent.click(screen.getByRole("button", { name: "取消并返回" }));

    expect(modalCommands()).toEqual([
      { type: "ui/modal", isOpen: true },
      { type: "ui/modal", isOpen: false },
      { type: "ui/modal", isOpen: true },
      { type: "ui/modal", isOpen: false }
    ]);
  });

  it("releases the host modal gate when unmounted with a dialog open", () => {
    const bridge = new RecordingBridge();
    const { unmount } = render(<Workbench bridge={bridge} />);

    fireEvent.click(screen.getByRole("button", { name: "设置" }));
    unmount();

    expect(bridge.commands.filter((command) => command.type === "ui/modal")).toEqual([
      { type: "ui/modal", isOpen: true },
      { type: "ui/modal", isOpen: false }
    ]);
  });

  it("offers workspace selection without querying sessions when no workspace is available", () => {
    const bridge = new RecordingBridge(null);
    render(<Workbench bridge={bridge} />);

    expect(commandsOfType(bridge, "session/list")).toEqual([]);
    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "重试" })).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "选择工作区" }));

    expect(bridge.commands).toContainEqual({ type: "workspace/select" });
  });

  it("queries sessions once when the first workspace becomes available", () => {
    const bridge = new RecordingBridge(null);
    render(<Workbench bridge={bridge} />);

    act(() => bridge.emit({
      type: "workspace/selected",
      path: "C:\\workspace",
      workspaceGeneration: 1
    }));

    expect(sessionListCommandPayloads(bridge)).toEqual([
      { type: "session/list", query: "", limit: 50 }
    ]);
  });

  it("queries sessions only once more when switching workspaces", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);

    expect(sessionListCommandPayloads(bridge)).toHaveLength(1);

    act(() => bridge.emit({
      type: "workspace/selected",
      path: "D:\\second-workspace",
      workspaceGeneration: 2
    }));

    expect(sessionListCommandPayloads(bridge)).toEqual([
      { type: "session/list", query: "", limit: 50 },
      { type: "session/list", query: "", limit: 50 }
    ]);
  });

  it("ignores duplicate workspace snapshots from multiple webviews", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);

    act(() => {
      bridge.emit({
        type: "workspace/selected",
        path: "c:\\WORKSPACE",
        workspaceGeneration: 1
      });
      bridge.emit({
        type: "workspace/selected",
        path: "C:\\workspace",
        workspaceGeneration: 1
      });
    });

    expect(sessionListCommandPayloads(bridge)).toEqual([
      { type: "session/list", query: "", limit: 50 }
    ]);
  });

  it("waits for the initial workspace snapshot before enabling prompts", () => {
    const bridge = new RecordingBridge(null);
    render(<Workbench bridge={bridge} />);

    fireEvent.change(screen.getByPlaceholderText("向 AgentDesk 描述任务"), {
      target: { value: "等待工作区同步" }
    });

    expect(screen.getByRole("button", { name: "发送" })).toBeDisabled();
    expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument();

    act(() => bridge.emit({
      type: "workspace/selected",
      path: "C:\\workspace",
      workspaceGeneration: 1
    }));

    expect(screen.getByRole("button", { name: "发送" })).toBeEnabled();
  });

  it("requires acknowledgement before starting native execution", async () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);

    fireEvent.change(screen.getByPlaceholderText("向 AgentDesk 描述任务"), {
      target: { value: "检查登录流程" }
    });
    const sendButton = screen.getByRole("button", { name: "发送" });
    fireEvent.click(sendButton);

    expect(bridge.commands).not.toContainEqual(expect.objectContaining({ type: "engine/prompt" }));
    expect(screen.getByPlaceholderText("向 AgentDesk 描述任务")).toHaveValue("检查登录流程");
    expect(document.querySelector(".message.user-message")).not.toBeInTheDocument();
    expect(screen.getByRole("alertdialog", { name: "确认本机非沙箱执行" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "取消并返回" })).toHaveFocus();
    expect(document.querySelector(".conversation")).toHaveAttribute("inert");
    expect(document.querySelector(".task-sidebar")).toHaveAttribute("inert");

    fireEvent.click(screen.getByRole("button", { name: "取消并返回" }));
    await waitForFocusRestore();

    expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument();
    expect(screen.getByPlaceholderText("向 AgentDesk 描述任务")).toHaveValue("检查登录流程");
    expect(bridge.commands).not.toContainEqual(expect.objectContaining({ type: "engine/prompt" }));
    expect(sendButton).toHaveFocus();
  });

  it("Escape closes native risk acknowledgement and restores the send button", async () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);

    fireEvent.change(screen.getByPlaceholderText("向 AgentDesk 描述任务"), {
      target: { value: "检查工作区" }
    });
    const sendButton = screen.getByRole("button", { name: "发送" });
    fireEvent.click(sendButton);

    fireEvent.keyDown(document, { key: "Escape" });
    await waitForFocusRestore();

    expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument();
    expect(screen.getByPlaceholderText("向 AgentDesk 描述任务")).toHaveValue("检查工作区");
    expect(bridge.commands).not.toContainEqual(expect.objectContaining({ type: "engine/prompt" }));
    expect(sendButton).toHaveFocus();
  });

  it.each(["starting", "running"] as const)(
    "keeps the prompt untouched while the engine is %s",
    (status) => {
      const bridge = new RecordingBridge();
      render(<Workbench bridge={bridge} />);
      act(() => bridge.emit({
        type: "engine/status",
        status,
        sessionId: "session-42"
      }));
      const composer = screen.getByPlaceholderText("向 AgentDesk 描述任务");
      fireEvent.change(composer, { target: { value: "等待当前任务完成" } });

      fireEvent.keyDown(composer, { key: "Enter", ctrlKey: true });

      expect(document.querySelector(".message.user-message")).not.toBeInTheDocument();
      expect(composer).toHaveValue("等待当前任务完成");
      expect(bridge.commands).not.toContainEqual(expect.objectContaining({ type: "engine/prompt" }));
    }
  );

  it("disables prompt submission while the engine is running before a session is assigned", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);
    act(() => bridge.emit({
      type: "engine/status",
      status: "running"
    }));
    fireEvent.change(screen.getByPlaceholderText("向 AgentDesk 描述任务"), {
      target: { value: "等待会话分配" }
    });

    expect(screen.getByRole("button", { name: "发送" })).toBeDisabled();
  });

  it("submits the composer with Enter", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);
    const composer = screen.getByPlaceholderText("向 AgentDesk 描述任务");
    fireEvent.change(composer, { target: { value: "使用回车发送" } });

    fireEvent.keyDown(composer, { key: "Enter" });

    expect(screen.getByRole("alertdialog", { name: "确认本机非沙箱执行" })).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "继续本机执行" }));
    expect(bridge.commands).toContainEqual(expect.objectContaining({
      type: "engine/prompt",
      text: "使用回车发送"
    }));
    expect(composer).toHaveValue("");
  });

  it("keeps Shift+Enter available for a composer newline", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);
    const composer = screen.getByPlaceholderText("向 AgentDesk 描述任务");
    fireEvent.change(composer, { target: { value: "第一行" } });

    expect(fireEvent.keyDown(composer, { key: "Enter", shiftKey: true })).toBe(true);
    fireEvent.change(composer, { target: { value: "第一行\n第二行" } });

    expect(composer).toHaveValue("第一行\n第二行");
    expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument();
    expect(bridge.commands).not.toContainEqual(expect.objectContaining({ type: "engine/prompt" }));
  });

  it("does not submit while a Chinese IME composition is active", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);
    const composer = screen.getByPlaceholderText("向 AgentDesk 描述任务");
    fireEvent.change(composer, { target: { value: "正在输入中文" } });

    expect(fireEvent.keyDown(composer, {
      key: "Enter",
      isComposing: true
    })).toBe(true);

    expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument();
    expect(composer).toHaveValue("正在输入中文");
    expect(bridge.commands).not.toContainEqual(expect.objectContaining({ type: "engine/prompt" }));
  });

  it("does not expose browser preview fixtures inside the desktop host", () => {
    const bridge = new RecordingBridge();

    render(<Workbench bridge={bridge} />);

    expect(screen.queryByText("实现 Windows 登录窗口")).not.toBeInTheDocument();
    expect(screen.queryByText("LoginDialog.xaml")).not.toBeInTheDocument();
    expect(screen.queryByText("上下文 18%")).not.toBeInTheDocument();
  });

  it("sends an acknowledged native prompt after confirmation", async () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);

    fireEvent.change(screen.getByPlaceholderText("向 AgentDesk 描述任务"), {
      target: { value: "运行 Windows 测试" }
    });
    fireEvent.click(screen.getByRole("button", { name: "发送" }));
    fireEvent.click(screen.getByRole("button", { name: "继续本机执行" }));
    await waitForFocusRestore();

    expect(bridge.commands).toContainEqual({
      type: "engine/prompt",
      text: "运行 Windows 测试",
      executionProfile: "NativeProtected",
      sessionMode: "default",
      nativeRiskAcknowledged: true,
      workspaceGeneration: 1
    });
    expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument();
    expect(screen.getByPlaceholderText("向 AgentDesk 描述任务")).toHaveValue("");
    expect(document.querySelector(".message.user-message")).toHaveTextContent("运行 Windows 测试");
    expect(screen.getByPlaceholderText("向 AgentDesk 描述任务")).toHaveFocus();
  });

  it("keeps Plan Mode independent from native execution risk acknowledgement", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);

    fireEvent.click(screen.getByRole("radio", { name: "计划" }));
    fireEvent.change(screen.getByPlaceholderText("向 AgentDesk 描述任务"), {
      target: { value: "只分析改动并制定计划" }
    });
    fireEvent.click(screen.getByRole("button", { name: "发送" }));

    expect(screen.getByRole("alertdialog", { name: "确认本机非沙箱执行" })).toBeInTheDocument();
    expect(bridge.commands).not.toContainEqual(expect.objectContaining({ type: "engine/prompt" }));

    fireEvent.click(screen.getByRole("button", { name: "继续本机执行" }));
    expect(bridge.commands).toContainEqual(expect.objectContaining({
      type: "engine/prompt",
      text: "只分析改动并制定计划",
      executionProfile: "NativeProtected",
      sessionMode: "plan",
      nativeRiskAcknowledged: true
    }));
  });

  it("reuses native risk acknowledgement within the same workspace", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);

    const composer = screen.getByPlaceholderText("向 AgentDesk 描述任务");
    fireEvent.change(composer, { target: { value: "第一个任务" } });
    fireEvent.click(screen.getByRole("button", { name: "发送" }));
    fireEvent.click(screen.getByRole("button", { name: "继续本机执行" }));
    act(() => bridge.emit({
      type: "prompt/completed",
      sessionId: "session-1",
      stopReason: "end_turn"
    }));

    fireEvent.change(composer, { target: { value: "第二个任务" } });
    fireEvent.click(screen.getByRole("button", { name: "发送" }));

    expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument();
    expect(bridge.commands.filter((command) => command.type === "engine/prompt")).toEqual([
      expect.objectContaining({
        text: "第一个任务",
        nativeRiskAcknowledged: true,
        workspaceGeneration: 1
      }),
      expect.objectContaining({
        text: "第二个任务",
        nativeRiskAcknowledged: true,
        workspaceGeneration: 1
      })
    ]);
  });

  it("clears native acknowledgement as soon as workspace selection starts", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);

    const composer = screen.getByPlaceholderText("向 AgentDesk 描述任务");
    fireEvent.change(composer, { target: { value: "第一个任务" } });
    fireEvent.click(screen.getByRole("button", { name: "发送" }));
    fireEvent.click(screen.getByRole("button", { name: "继续本机执行" }));
    act(() => bridge.emit({
      type: "prompt/completed",
      sessionId: "session-1",
      stopReason: "end_turn"
    }));

    fireEvent.click(screen.getByRole("button", { name: "工作区" }));
    fireEvent.change(composer, { target: { value: "选择期间的任务" } });
    fireEvent.click(screen.getByRole("button", { name: "发送" }));

    expect(bridge.commands).toContainEqual({ type: "workspace/select" });
    expect(screen.getByRole("alertdialog", { name: "确认本机非沙箱执行" })).toBeInTheDocument();
    expect(bridge.commands.filter((command) => command.type === "engine/prompt")).toHaveLength(1);
  });

  it("requires acknowledgement again after the workspace changes", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);

    const composer = screen.getByPlaceholderText("向 AgentDesk 描述任务");
    fireEvent.change(composer, { target: { value: "第一个工作区任务" } });
    fireEvent.click(screen.getByRole("button", { name: "发送" }));
    fireEvent.click(screen.getByRole("button", { name: "继续本机执行" }));
    act(() => bridge.emit({
      type: "prompt/completed",
      sessionId: "session-1",
      stopReason: "end_turn"
    }));
    act(() => bridge.emit({
      type: "workspace/selected",
      path: "D:\\second-workspace",
      workspaceGeneration: 7
    }));

    fireEvent.change(composer, { target: { value: "第二个工作区任务" } });
    fireEvent.click(screen.getByRole("button", { name: "发送" }));

    expect(screen.getByRole("alertdialog", { name: "确认本机非沙箱执行" })).toBeInTheDocument();
    expect(bridge.commands.filter((command) => command.type === "engine/prompt")).toHaveLength(1);

    fireEvent.click(screen.getByRole("button", { name: "继续本机执行" }));
    expect(bridge.commands.filter((command) => command.type === "engine/prompt")).toHaveLength(2);
    expect(bridge.commands).toContainEqual(expect.objectContaining({
      type: "engine/prompt",
      text: "第二个工作区任务",
      nativeRiskAcknowledged: true,
      workspaceGeneration: 7
    }));
  });

  it("ignores workspace events older than the latest generation", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);

    act(() => bridge.emit({
      type: "workspace/selected",
      path: "D:\\current-workspace",
      workspaceGeneration: 7
    }));
    act(() => bridge.emit({
      type: "workspace/selected",
      path: "C:\\stale-workspace",
      workspaceGeneration: 6
    }));

    expect(screen.getByTitle("D:\\current-workspace")).toBeInTheDocument();
    expect(screen.queryByTitle("C:\\stale-workspace")).not.toBeInTheDocument();

    fireEvent.change(screen.getByPlaceholderText("向 AgentDesk 描述任务"), {
      target: { value: "使用最新工作区" }
    });
    fireEvent.click(screen.getByRole("button", { name: "发送" }));
    fireEvent.click(screen.getByRole("button", { name: "继续本机执行" }));

    expect(bridge.commands).toContainEqual(expect.objectContaining({
      type: "engine/prompt",
      text: "使用最新工作区",
      workspaceGeneration: 7
    }));
  });

  it("clears the old active session and accepts capabilities for the new workspace", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);
    act(() => {
      bridge.emit({ type: "engine/status", status: "ready", sessionId: "old-session" });
      bridge.emit({
        type: "engine/capabilities",
        sessionId: "old-session",
        imagePrompts: false,
        sessionModes: ["default"]
      });
      bridge.emit(sessionListChanged([
        sessionSummary({ sessionId: "old-session", title: "旧工作区会话" })
      ]));
      bridge.emit({
        type: "workspace/selected",
        path: "D:\\new-workspace",
        workspaceGeneration: 7
      });
    });

    expect(screen.queryByRole("button", { name: "刷新会话记忆" })).not.toBeInTheDocument();
    expect(screen.queryByText("旧工作区会话")).not.toBeInTheDocument();
    act(() => bridge.emit({
      type: "engine/capabilities",
      sessionId: "new-session",
      imagePrompts: true,
      sessionModes: ["default", "plan"]
    }));
    expect(screen.getByRole("button", { name: "附加图片" })).toBeEnabled();
    expect(bridge.commands).toContainEqual(expect.objectContaining({
      type: "session/list",
      requestId: expect.stringMatching(maintenanceRequestIdPattern),
      query: "",
      limit: 50
    }));
  });

  it("preserves the active session capabilities when the same workspace gets a new generation",
    async () => {
      const bridge = new RecordingBridge();
      render(<Workbench bridge={bridge} />);
      act(() => {
        bridge.emit({ type: "engine/status", status: "ready", sessionId: "session-42" });
        bridge.emit({
          type: "engine/capabilities",
          sessionId: "session-42",
          imagePrompts: true,
          sessionModes: ["default", "plan"]
        });
        bridge.emit(sessionListChanged([
          sessionSummary({ sessionId: "session-42", title: "Same workspace session" })
        ]));
      });
      fireEvent.click(screen.getByRole("button", { name: zhCn.attachImage }));
      const selection = lastCommandOfType(bridge, "attachment/select");
      act(() => bridge.emit({
        type: "attachment/changed",
        requestId: selection.requestId as string,
        attachments: [{
          token: "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF",
          name: "same-workspace.png",
          mimeType: "image/png",
          size: 68
        }]
      }));
      expect(screen.getByText("same-workspace.png")).toBeInTheDocument();

      act(() => bridge.emit({
        type: "workspace/selected",
        path: "c:\\WORKSPACE",
        workspaceGeneration: 2
      }));

      expect(screen.getByRole("heading", { name: "Same workspace session" })).toBeInTheDocument();
      expect(screen.getByRole("button", { name: zhCn.flushMemory })).toBeEnabled();
      expect(screen.getByRole("button", { name: zhCn.attachImage })).toBeEnabled();
      expect(screen.getByRole("radio", { name: zhCn.plan })).toBeEnabled();
      expect(screen.queryByText("same-workspace.png")).not.toBeInTheDocument();
      expect(bridge.commands).toContainEqual({
        type: "attachment/discard",
        tokens: ["0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF"]
      });
    });

  it("requests a native credential replacement without rendering a password field", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);

    fireEvent.click(screen.getByRole("button", { name: zhCn.settings }));
    expect(screen.getByLabelText(zhCn.baseUrl)).toHaveValue("https://api.x.ai/v1");
    expect(screen.getByLabelText(zhCn.model)).toHaveValue("grok-build");
    expect(document.querySelector('input[type="password"]')).toBeNull();
    const credentialText = screen.getByText(zhCn.providerCredentialNativePrompt);
    const credentialNote = credentialText.closest(".settings-security-note");
    expect(credentialNote).not.toBeNull();
    expect(credentialNote?.querySelector("svg")).not.toBeNull();
    expect(credentialText.tagName).toBe("SPAN");
    fireEvent.click(screen.getByRole("button", { name: zhCn.saveProvider }));

    expect(bridge.commands).toContainEqual({
      type: "provider/save",
      baseUrl: "https://api.x.ai/v1",
      model: "grok-build",
      backend: "chat_completions",
      allowInsecureTransport: false,
      useExistingCredential: false,
      replaceCredential: true
    });
    expect(bridge.commands.at(-1)).not.toHaveProperty("secret");
    expect(bridge.commands.at(-1)).not.toHaveProperty("apiKey");
  });

  it("selects and saves the Responses provider backend", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);

    fireEvent.click(screen.getByRole("button", { name: zhCn.settings }));
    const backend = screen.getByRole("combobox", { name: zhCn.backend });
    fireEvent.change(backend, { target: { value: "responses" } });
    fireEvent.click(screen.getByRole("button", { name: zhCn.saveProvider }));

    expect(bridge.commands).toContainEqual({
      type: "provider/save",
      baseUrl: "https://api.x.ai/v1",
      model: "grok-build",
      backend: "responses",
      allowInsecureTransport: false,
      useExistingCredential: false,
      replaceCredential: true
    });
  });

  it("requires an explicit opt-in before sending credentials over HTTP", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);

    fireEvent.click(screen.getByRole("button", { name: "设置" }));
    fireEvent.change(screen.getByLabelText("Base URL"), {
      target: { value: "http://localhost:8081/v1" }
    });
    const save = screen.getByRole("button", { name: "保存提供商" });
    const allowHttp = screen.getByRole("checkbox", {
      name: "允许通过不安全 HTTP 发送 API Key"
    });

    expect(allowHttp).not.toBeChecked();
    expect(save).toBeDisabled();
    expect(screen.getByRole("alert")).toHaveTextContent("明文 HTTP 需要明确授权");

    fireEvent.click(allowHttp);
    expect(save).toBeEnabled();
    fireEvent.click(save);

    expect(bridge.commands).toContainEqual({
      type: "provider/save",
      baseUrl: "http://localhost:8081/v1",
      model: "grok-build",
      backend: "chat_completions",
      allowInsecureTransport: true,
      useExistingCredential: false,
      replaceCredential: true
    });
  });

  it("loads provider status fields without ever accepting a returned secret", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);

    act(() => bridge.emit({
      type: "provider/status",
      status: "loaded",
      baseUrl: "http://localhost:8081/v1",
      model: "grok-4.5",
      backend: "chat_completions",
      allowInsecureTransport: true,
      hasCredential: true,
      secret: "host-secret-must-be-ignored"
    } as HostEvent));
    fireEvent.click(screen.getByRole("button", { name: "设置" }));

    expect(screen.getByLabelText("Base URL")).toHaveValue("http://localhost:8081/v1");
    expect(screen.getByLabelText("模型")).toHaveValue("grok-4.5");
    expect(screen.getByLabelText("后端")).toHaveValue("chat_completions");
    expect(screen.getByRole("checkbox", {
      name: "允许通过不安全 HTTP 发送 API Key"
    })).toBeChecked();
    expect(document.querySelector('input[type="password"]')).toBeNull();
    expect(screen.getByText(zhCn.providerCredentialStored)).toBeInTheDocument();
    expect(screen.queryByText("host-secret-must-be-ignored")).not.toBeInTheDocument();
    expect(screen.queryByDisplayValue("host-secret-must-be-ignored")).not.toBeInTheDocument();
  });

  it("keeps provider fields and displays host errors when settings cannot be loaded", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);

    act(() => bridge.emit({
      type: "provider/status",
      status: "error",
      baseUrl: "",
      model: "",
      backend: "chat_completions",
      allowInsecureTransport: false,
      hasCredential: false,
      message: "无法读取模型服务设置"
    }));
    fireEvent.click(screen.getByRole("button", { name: "设置" }));

    expect(screen.getByLabelText("Base URL")).toHaveValue("https://api.x.ai/v1");
    expect(screen.getByLabelText("模型")).toHaveValue("grok-build");
    expect(screen.getByRole("alert")).toHaveTextContent("无法读取模型服务设置");
  });

  it("reuses the stored credential only for the exact provider endpoint", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);

    act(() => bridge.emit({
      type: "provider/status",
      status: "loaded",
      baseUrl: "https://example.com/v1",
      model: "grok-4.5",
      backend: "chat_completions",
      allowInsecureTransport: false,
      hasCredential: true
    }));
    fireEvent.click(screen.getByRole("button", { name: "设置" }));

    expect(screen.getByLabelText("后端")).toHaveValue("chat_completions");
    expect(document.querySelector('input[type="password"]')).toBeNull();
    expect(screen.getByText(zhCn.providerCredentialStored)).toBeInTheDocument();
    const save = screen.getByRole("button", { name: "保存提供商" });
    expect(save).toBeEnabled();
    fireEvent.click(save);

    expect(bridge.commands).toContainEqual({
      type: "provider/save",
      baseUrl: "https://example.com/v1",
      model: "grok-4.5",
      backend: "chat_completions",
      allowInsecureTransport: false,
      useExistingCredential: true,
      replaceCredential: false
    });
  });

  it("requests native replacement when the provider Base URL changes", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);

    act(() => bridge.emit({
      type: "provider/status",
      status: "loaded",
      baseUrl: "https://api.x.ai/v1",
      model: "grok-build",
      backend: "chat_completions",
      allowInsecureTransport: false,
      hasCredential: true
    }));
    fireEvent.click(screen.getByRole("button", { name: "设置" }));
    const save = screen.getByRole("button", { name: "保存提供商" });

    expect(save).toBeEnabled();
    fireEvent.change(screen.getByLabelText("Base URL"), {
      target: { value: "https://example.com/v1" }
    });

    expect(save).toBeEnabled();
    expect(screen.getByText(zhCn.providerCredentialNativePrompt)).toBeInTheDocument();
    fireEvent.click(save);

    expect(bridge.commands).toContainEqual({
      type: "provider/save",
      baseUrl: "https://example.com/v1",
      model: "grok-build",
      backend: "chat_completions",
      allowInsecureTransport: false,
      useExistingCredential: false,
      replaceCredential: true
    });
  });

  it("can explicitly replace a credential stored for the current endpoint", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);

    act(() => bridge.emit({
      type: "provider/status",
      status: "loaded",
      baseUrl: "https://example.com/v1",
      model: "grok-4.5",
      backend: "chat_completions",
      allowInsecureTransport: false,
      hasCredential: true
    }));
    fireEvent.click(screen.getByRole("button", { name: zhCn.settings }));
    fireEvent.click(screen.getByRole("checkbox", { name: zhCn.replaceProviderCredential }));
    fireEvent.click(screen.getByRole("button", { name: zhCn.saveProvider }));

    expect(bridge.commands).toContainEqual({
      type: "provider/save",
      baseUrl: "https://example.com/v1",
      model: "grok-4.5",
      backend: "chat_completions",
      allowInsecureTransport: false,
      useExistingCredential: false,
      replaceCredential: true
    });
  });

  it("restores settings focus only after inert is removed on the next microtask", async () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);
    const settingsButton = screen.getByRole("button", { name: "设置" });

    settingsButton.focus();
    fireEvent.click(settingsButton);
    const modelInput = screen.getByLabelText(zhCn.model);
    modelInput.focus();
    const focusInertStates: boolean[] = [];
    const originalFocus = settingsButton.focus.bind(settingsButton);
    const focusSpy = vi.spyOn(settingsButton, "focus").mockImplementation(() => {
      focusInertStates.push(document.querySelector(".tool-rail")?.hasAttribute("inert") ?? false);
      originalFocus();
    });
    expect(screen.queryByRole("button", { name: "工作区" })).not.toBeInTheDocument();
    fireEvent.keyDown(document, { key: "Escape" });

    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(focusSpy).not.toHaveBeenCalled();

    await act(async () => {
      await Promise.resolve();
    });

    expect(focusInertStates).toEqual([false]);
    expect(settingsButton).toHaveFocus();
  });

  it("offers cancellation while a prompt is running", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);

    act(() => {
      bridge.emit({
        type: "engine/status",
        status: "running",
        message: "正在运行",
        sessionId: "session-42"
      });
    });
    fireEvent.click(screen.getByRole("button", { name: "停止任务" }));

    expect(bridge.commands).toContainEqual({
      type: "engine/cancel",
      sessionId: "session-42"
    });
  });

  it("shows the real permission options in a modal and makes the workspace inert", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);

    act(() => bridge.emit(permissionRequest(
      "permission-1",
      "运行 PowerShell 安装脚本",
      [
        { optionId: "allow-once", name: "允许一次", kind: "allow_once" },
        { optionId: "reject-once", name: "拒绝此次操作", kind: "reject_once" }
      ]
    )));

    expect(screen.getByRole("dialog", { name: "权限审批" })).toBeInTheDocument();
    expect(screen.getByText("运行 PowerShell 安装脚本")).toBeInTheDocument();
    expect(screen.getByText("C:\\workspace\\install.ps1:8")).toBeInTheDocument();
    expect(screen.getByText("execute")).toBeInTheDocument();
    expect(screen.getByText(/pwsh -File install\.ps1/)).toBeInTheDocument();
    expect(screen.getByText(/--silent/)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "允许一次" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "拒绝此次操作" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "取消审批" })).toHaveFocus();
    expect(document.querySelector(".conversation")).toHaveAttribute("inert");
    expect(document.querySelector(".task-sidebar")).toHaveAttribute("inert");
  });

  it("returns the selected option and restores focus after the queue is empty", async () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);
    const composer = screen.getByPlaceholderText("向 AgentDesk 描述任务");
    composer.focus();

    act(() => bridge.emit(permissionRequest("permission-1", "写入 README.md")));
    fireEvent.click(screen.getByRole("button", { name: "允许一次" }));
    await waitForFocusRestore();

    expect(bridge.commands).toContainEqual({
      type: "permission/respond",
      requestId: "permission-1",
      outcome: "selected",
      optionId: "allow-once"
    });
    expect(screen.queryByRole("dialog", { name: "权限审批" })).not.toBeInTheDocument();
    expect(composer).toHaveFocus();
  });

  it("queues parallel permission requests in arrival order", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);

    act(() => {
      bridge.emit(permissionRequest("permission-1", "第一个操作"));
      bridge.emit(permissionRequest("permission-2", "第二个操作"));
    });

    expect(screen.getByText("第一个操作")).toBeInTheDocument();
    expect(screen.queryByText("第二个操作")).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "拒绝" }));

    expect(bridge.commands).toContainEqual({
      type: "permission/respond",
      requestId: "permission-1",
      outcome: "selected",
      optionId: "reject-once"
    });
    expect(screen.getByText("第二个操作")).toBeInTheDocument();
  });

  it("Escape cancels the current permission and advances the queue", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);

    act(() => {
      bridge.emit(permissionRequest("permission-1", "第一个操作"));
      bridge.emit(permissionRequest("permission-2", "第二个操作"));
    });
    fireEvent.keyDown(document, { key: "Escape" });

    expect(bridge.commands).toContainEqual({
      type: "permission/respond",
      requestId: "permission-1",
      outcome: "cancelled"
    });
    expect(screen.getByText("第二个操作")).toBeInTheDocument();
  });

  it("clears a crashed session permission queue and restores focus", async () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);
    const composer = screen.getByPlaceholderText("向 AgentDesk 描述任务");
    composer.focus();
    act(() => bridge.emit(permissionRequest("permission-1", "等待中的操作")));
    expect(screen.getByRole("dialog", { name: "权限审批" })).toBeInTheDocument();

    act(() => bridge.emit({
      type: "engine/status",
      status: "stopped",
      message: "引擎进程意外退出",
      sessionId: "session-42"
    }));
    await waitForFocusRestore();

    expect(screen.queryByRole("dialog", { name: "权限审批" })).not.toBeInTheDocument();
    expect(composer).toHaveFocus();
  });

  it.each(["stopped", "error"] as const)(
    "ignores late permissions from a session after it is %s",
    (status) => {
      const bridge = new RecordingBridge();
      render(<Workbench bridge={bridge} />);
      act(() => {
        bridge.emit({
          type: "engine/status",
          status: "running",
          sessionId: "session-42"
        });
        bridge.emit({
          type: "engine/status",
          status,
          sessionId: "session-42"
        });
        bridge.emit(permissionRequest("permission-late", "迟到的旧会话操作"));
      });

      expect(screen.queryByRole("dialog", { name: "权限审批" })).not.toBeInTheDocument();
      expect(screen.queryByText("迟到的旧会话操作")).not.toBeInTheDocument();
      expect(document.querySelector(".conversation")).not.toHaveAttribute("inert");
      expect(document.querySelector(".task-sidebar")).not.toHaveAttribute("inert");
    }
  );

  it("accepts permissions when an errored session is retried", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);
    act(() => {
      bridge.emit({
        type: "engine/status",
        status: "running",
        sessionId: "session-42"
      });
      bridge.emit({
        type: "engine/status",
        status: "error",
        sessionId: "session-42"
      });
      bridge.emit({
        type: "engine/status",
        status: "running",
        sessionId: "session-42"
      });
      bridge.emit(permissionRequest("permission-retry", "重试后的真实操作"));
    });

    expect(screen.getByRole("dialog", { name: "权限审批" })).toBeInTheDocument();
    expect(screen.getByText("重试后的真实操作")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "允许一次" }));
    expect(bridge.commands).toContainEqual({
      type: "permission/respond",
      requestId: "permission-retry",
      outcome: "selected",
      optionId: "allow-once"
    });
  });

  it("keeps image content native while selecting, sending, and clearing attachment tokens", async () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);
    act(() => {
      bridge.emit({
        type: "engine/status",
        status: "ready",
        sessionId: "session-42",
        capabilities: { executionProfiles: ["NativeProtected", "WslStrict"] }
      });
      bridge.emit({
        type: "engine/capabilities",
        sessionId: "session-42",
        imagePrompts: true,
        sessionModes: ["default", "plan"]
      });
    });
    fireEvent.click(screen.getByRole("button", { name: "WSL2 严格模式" }));
    const attach = screen.getByRole("button", { name: "附加图片" });
    fireEvent.click(attach);
    const selection = lastCommandOfType(bridge, "attachment/select");
    expect(selection.requestId).toMatch(maintenanceRequestIdPattern);
    expect(attach).toBeDisabled();
    fireEvent.click(attach);
    expect(commandsOfType(bridge, "attachment/select")).toHaveLength(1);
    act(() => bridge.emit({
      type: "attachment/changed",
      requestId: selection.requestId as string,
      attachments: [{
        token: "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF",
        name: "pixel.png",
        mimeType: "image/png",
        size: 68
      }]
    }));
    expect(attach).toBeEnabled();
    expect(screen.getByText("pixel.png")).toBeInTheDocument();
    expect(document.querySelector('input[type="file"]')).not.toBeInTheDocument();
    fireEvent.change(screen.getByPlaceholderText("向 AgentDesk 描述任务"), {
      target: { value: "检查图片" }
    });
    fireEvent.click(screen.getByRole("button", { name: "发送" }));

    expect(bridge.commands).toContainEqual(expect.objectContaining({
      type: "engine/prompt",
      text: "检查图片",
      executionProfile: "WslStrict",
      attachments: [{
        token: "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF",
        name: "pixel.png",
        mimeType: "image/png",
        size: 68
      }]
    }));
    expect(JSON.stringify(bridge.commands)).not.toContain("base64Data");
    expect(screen.queryByText("pixel.png")).not.toBeInTheDocument();
  });

  it("shows attachment capability and validation errors without adding invalid data", async () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);
    expect(screen.getByRole("button", { name: "附加图片" })).toBeDisabled();
    expect(screen.getByText("当前引擎不支持图片提示")).toBeInTheDocument();

    act(() => bridge.emit({
      type: "engine/capabilities",
      sessionId: "session-42",
      imagePrompts: true,
      sessionModes: ["default"]
    }));
    fireEvent.click(screen.getByRole("button", { name: "附加图片" }));
    const selection = lastCommandOfType(bridge, "attachment/select");
    act(() => bridge.emit({
      type: "attachment/changed",
      requestId: selection.requestId as string,
      attachments: [],
      error: "content_mismatch"
    }));

    expect(screen.getByRole("alert")).toHaveTextContent("图片内容与文件类型不匹配");
    expect(screen.queryByText("fake.png")).not.toBeInTheDocument();
    expect(document.querySelector('input[type="file"]')).not.toBeInTheDocument();
  });

  it.each([
    ["session", {
      type: "session/active/changed",
      sessionId: "session-next",
      workspacePath: "C:\\workspace"
    } as HostEvent],
    ["workspace", {
      type: "workspace/selected",
      path: "D:\\next-workspace",
      workspaceGeneration: 7
    } as HostEvent]
  ])("clears pending images when the active %s changes", async (_context, contextEvent) => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);
    act(() => {
      bridge.emit({ type: "engine/status", status: "ready", sessionId: "session-42" });
      bridge.emit({
        type: "engine/capabilities",
        sessionId: "session-42",
        imagePrompts: true,
        sessionModes: ["default"]
      });
    });
    fireEvent.click(screen.getByRole("button", { name: "附加图片" }));
    const selection = lastCommandOfType(bridge, "attachment/select");
    act(() => bridge.emit({
      type: "attachment/changed",
      requestId: selection.requestId as string,
      attachments: [{
        token: "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF",
        name: "context.png",
        mimeType: "image/png",
        size: 68
      }]
    }));
    expect(screen.getByText("context.png")).toBeInTheDocument();

    act(() => bridge.emit(contextEvent));

    expect(screen.queryByText("context.png")).not.toBeInTheDocument();
    expect(bridge.commands).toContainEqual({
      type: "attachment/discard",
      tokens: ["0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF"]
    });
  });

  it("sends a validated image prompt without requiring text", async () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);
    act(() => {
      bridge.emit({
        type: "engine/status",
        status: "ready",
        sessionId: "session-42",
        capabilities: { executionProfiles: ["NativeProtected", "WslStrict"] }
      });
      bridge.emit({
        type: "engine/capabilities",
        sessionId: "session-42",
        imagePrompts: true,
        sessionModes: ["default"]
      });
    });
    fireEvent.click(screen.getByRole("button", { name: "WSL2 严格模式" }));
    fireEvent.click(screen.getByRole("button", { name: "附加图片" }));
    const selection = lastCommandOfType(bridge, "attachment/select");
    act(() => bridge.emit({
      type: "attachment/changed",
      requestId: selection.requestId as string,
      attachments: [{
        token: "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF",
        name: "only-image.png",
        mimeType: "image/png",
        size: 68
      }]
    }));
    expect(screen.getByText("only-image.png")).toBeInTheDocument();

    const send = screen.getByRole("button", { name: "发送" });
    expect(send).toBeEnabled();
    fireEvent.click(send);

    expect(bridge.commands).toContainEqual(expect.objectContaining({
      type: "engine/prompt",
      text: "",
      executionProfile: "WslStrict",
      attachments: [expect.objectContaining({ name: "only-image.png" })]
    }));
  });

  it("opens the runtime command palette from slash input and supports keyboard selection", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);
    expect(bridge.commands).toContainEqual({
      type: "runtime/commands/list",
      workspaceGeneration: 1
    });
    act(() => bridge.emit({
      type: "runtime/commands/changed",
      workspaceGeneration: 1,
      commands: [{
        name: "skill",
        description: "运行仓库 Skill",
        input: { hint: "skill-name" },
        skill: { scope: "repo", path: "C:\\workspace\\skill.md" }
      }]
    }));
    const composer = screen.getByPlaceholderText("向 AgentDesk 描述任务");
    fireEvent.change(composer, { target: { value: "/s" } });

    expect(screen.getByRole("listbox", { name: "命令与 Skills" })).toBeInTheDocument();
    expect(screen.getByText("运行仓库 Skill")).toBeInTheDocument();
    expect(screen.getByText("skill-name")).toBeInTheDocument();
    expect(screen.getByText("仓库 Skill")).toBeInTheDocument();
    fireEvent.keyDown(composer, { key: "ArrowDown" });
    fireEvent.keyDown(composer, { key: "Enter" });

    expect(composer).toHaveValue("/skill ");
    expect(screen.queryByRole("listbox", { name: "命令与 Skills" })).not.toBeInTheDocument();
  });

  it("flushes memory for the active session and ignores stale completion events", () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);
    act(() => bridge.emit({
      type: "engine/status",
      status: "ready",
      sessionId: "session-42"
    }));
    fireEvent.click(screen.getByRole("button", { name: "刷新会话记忆" }));

    expect(bridge.commands).toContainEqual({
      type: "runtime/memory/flush",
      sessionId: "session-42"
    });
    act(() => {
      bridge.emit({
        type: "runtime/memory/status",
        sessionId: "old-session",
        status: "succeeded"
      });
      bridge.emit({
        type: "runtime/memory/status",
        sessionId: "session-42",
        status: "succeeded"
      });
    });
    expect(screen.getByText("会话记忆已刷新")).toBeInTheDocument();
  });

  it("restores persisted language, draft, modes, and saves language changes", async () => {
    const bridge = new RecordingBridge();
    render(<Workbench bridge={bridge} />);
    act(() => {
      bridge.emit({
        type: "ui/preferences/changed",
        language: "en-US",
        composerDraft: "continue the review",
        sessionMode: "plan",
        executionProfile: "WslStrict",
        notificationsEnabled: true,
        windowsAutomationEnabled: true,
        backgroundUpdateChecksEnabled: false,
        restartRequired: false
      });
      bridge.emit({
        type: "engine/status",
        status: "ready",
        sessionId: "session-42",
        capabilities: { executionProfiles: ["NativeProtected", "WslStrict"] }
      });
      bridge.emit({
        type: "engine/capabilities",
        sessionId: "session-42",
        imagePrompts: false,
        sessionModes: ["default", "plan"]
      });
    });

    expect(screen.getByPlaceholderText("Describe a task to AgentDesk")).toHaveValue(
      "continue the review"
    );
    expect(screen.getByRole("radio", { name: "Plan" })).toHaveAttribute("aria-checked", "true");
    expect(screen.getByRole("button", { name: "WSL2 strict mode" })).toHaveAttribute(
      "aria-pressed",
      "true"
    );
    fireEvent.click(screen.getByRole("button", { name: "Settings" }));
    fireEvent.change(screen.getByLabelText("Language"), { target: { value: "zh-CN" } });

    await waitFor(() => expect(bridge.commands).toContainEqual({
      type: "ui/preferences/save",
      language: "zh-CN",
      composerDraft: "continue the review",
      sessionMode: "plan",
      executionProfile: "WslStrict",
      notificationsEnabled: true,
      windowsAutomationEnabled: true,
      backgroundUpdateChecksEnabled: false
    }));
    expect(screen.getByPlaceholderText("向 AgentDesk 描述任务")).toBeInTheDocument();
    expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument();
  });

  it("requires host-approved Windows Automation and clears set-value input", async () => {
    const bridge = new RecordingBridge();
    const confirm = vi.spyOn(window, "confirm").mockReturnValue(true);
    render(<Workbench bridge={bridge} />);
    act(() => bridge.emit({
      type: "ui/preferences/changed",
      language: "zh-CN",
      composerDraft: "",
      sessionMode: "default",
      executionProfile: "NativeProtected",
      notificationsEnabled: false,
      windowsAutomationEnabled: false,
      backgroundUpdateChecksEnabled: false,
      restartRequired: false
    }));
    fireEvent.click(screen.getByRole("button", { name: "设置" }));

    fireEvent.click(screen.getByRole("checkbox", { name: "启用桌面通知" }));
    await waitFor(() => expect(bridge.commands).toContainEqual(expect.objectContaining({
      type: "ui/preferences/save",
      notificationsEnabled: true,
      windowsAutomationEnabled: false
    })));

    const backgroundUpdateCheckbox = screen.getByRole("checkbox", {
      name: "在后台检查更新"
    });
    expect(backgroundUpdateCheckbox).not.toBeChecked();
    fireEvent.click(backgroundUpdateCheckbox);
    await waitFor(() => expect(bridge.commands).toContainEqual(expect.objectContaining({
      type: "ui/preferences/save",
      backgroundUpdateChecksEnabled: true
    })));
    fireEvent.click(backgroundUpdateCheckbox);
    await waitFor(() => expect(bridge.commands).toContainEqual(expect.objectContaining({
      type: "ui/preferences/save",
      backgroundUpdateChecksEnabled: false
    })));

    fireEvent.click(screen.getByRole("checkbox", { name: "启用 Windows UI 自动化" }));
    expect(confirm).toHaveBeenCalledWith(zhCn.windowsAutomationEnableConfirm);
    await waitFor(() => expect(bridge.commands).toContainEqual(expect.objectContaining({
      type: "ui/preferences/save",
      notificationsEnabled: true,
      windowsAutomationEnabled: true
    })));
    expect(screen.getByRole("button", { name: "执行 Windows UI 自动化" })).toBeDisabled();

    act(() => bridge.emit({
      type: "ui/preferences/changed",
      language: "zh-CN",
      composerDraft: "",
      sessionMode: "default",
      executionProfile: "NativeProtected",
      notificationsEnabled: true,
      windowsAutomationEnabled: true,
      backgroundUpdateChecksEnabled: false,
      restartRequired: false
    }));
    fireEvent.change(screen.getByLabelText("Windows UI 自动化操作"), {
      target: { value: "set-value" }
    });
    fireEvent.change(screen.getByLabelText("目标进程 ID"), { target: { value: "4242" } });
    fireEvent.change(screen.getByLabelText("Automation ID"), {
      target: { value: "SearchBox" }
    });
    const valueInput = screen.getByLabelText("要设置的值");
    fireEvent.change(valueInput, { target: { value: "must-not-remain" } });
    fireEvent.click(screen.getByRole("button", { name: "执行 Windows UI 自动化" }));

    const command = lastCommandOfType(bridge, "windows/automation/execute");
    expect(command).toMatchObject({
      action: "set-value",
      processId: 4242,
      automationId: "SearchBox",
      value: "must-not-remain"
    });
    expect(valueInput).toHaveValue("");
    expect(screen.queryByText("must-not-remain")).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "执行 Windows UI 自动化" })).toBeDisabled();

    act(() => bridge.emit({
      type: "windows/automation/completed",
      requestId: "00000000-0000-4000-8000-000000000000",
      action: "set-value",
      processId: 4242,
      target: "SearchBox"
    }));
    expect(screen.getByRole("button", { name: "执行 Windows UI 自动化" })).toBeDisabled();
    act(() => bridge.emit({
      type: "windows/automation/completed",
      requestId: command.requestId as string,
      action: "set-value",
      processId: 4242,
      target: "SearchBox"
    }));
    expect(screen.getByText("Windows UI 自动化操作已完成")).toBeInTheDocument();
    expect(screen.queryByText("must-not-remain")).not.toBeInTheDocument();

    act(() => bridge.emit({
      type: "ui/preferences/changed",
      language: "zh-CN",
      composerDraft: "",
      sessionMode: "default",
      executionProfile: "NativeProtected",
      notificationsEnabled: true,
      windowsAutomationEnabled: false,
      backgroundUpdateChecksEnabled: false,
      restartRequired: false
    }));
    expect(screen.getByRole("checkbox", { name: "启用 Windows UI 自动化" })).not.toBeChecked();
    expect(screen.getByRole("button", { name: "执行 Windows UI 自动化" })).toBeDisabled();
    confirm.mockRestore();
  });
});

function permissionRequest(
  requestId: string,
  title: string,
  options = [
    { optionId: "allow-once", name: "允许一次", kind: "allow_once" as const },
    { optionId: "reject-once", name: "拒绝", kind: "reject_once" as const }
  ]
): HostEvent {
  return {
    type: "permission/requested",
    requestId,
    sessionId: "session-42",
    toolCallId: `tool-${requestId}`,
    title,
    toolKind: "execute",
    rawInput: {
      command: "pwsh -File install.ps1",
      args: ["--silent"]
    },
    options,
    locations: ["C:\\workspace\\install.ps1:8"]
  };
}

function sessionSummary(overrides: Partial<{
  sessionId: string;
  title: string;
  workspacePath: string;
  createdAt: string;
  updatedAt: string;
  messageCount: number;
  modelId: string;
  parentSessionId: string;
  branch: string;
  worktreeLabel: string;
  sourceWorkspacePath: string;
}> = {}) {
  return {
    sessionId: "session-default",
    title: "默认会话",
    workspacePath: "C:\\workspace",
    createdAt: "2026-07-16T10:00:00Z",
    updatedAt: "2026-07-17T08:30:00Z",
    messageCount: 1,
    ...overrides
  };
}

function sessionListChanged(
  sessions: ReturnType<typeof sessionSummary>[],
  nextCursor?: string,
  requestId?: string
): HostEvent {
  return {
    type: "session/list/changed",
    sessions,
    ...(requestId ? { requestId } : {}),
    ...(nextCursor ? { nextCursor } : {})
  } as HostEvent;
}

function runtimeDashboardChanged(): Extract<HostEvent, { type: "runtime/dashboard/changed" }> {
  return {
    type: "runtime/dashboard/changed",
    sessionId: "session-42",
    backgroundTasks: [{
      taskId: "task-7",
      command: "dotnet test",
      workingDirectory: "C:\\workspace",
      startedAt: "2026-07-17T08:00:00Z",
      output: "running",
      truncated: false,
      completed: false,
      kind: "bash",
      explicitlyKilled: false,
      ownerSessionId: "session-42"
    }],
    subagents: [{
      subagentId: "subagent-7",
      parentSessionId: "session-42",
      childSessionId: "child-session",
      subagentType: "worker",
      description: "运行桌面测试",
      startedAt: "2026-07-17T08:00:00Z",
      durationMs: 2000,
      status: "running",
      turnCount: 2,
      toolCallCount: 4,
      tokensUsed: 8192,
      contextWindowTokens: 131072,
      contextUsagePercent: 6,
      toolsUsed: ["shell_command"],
      errorCount: 0
    }]
  };
}

function worktreeListChanged(worktrees: ReturnType<typeof worktreeRecord>[], workspaceGeneration: number) {
  return {
    type: "worktree/list/changed",
    workspaceGeneration,
    worktrees
  };
}

function worktreeRecord() {
  return {
    id: "worktree-7",
    path: "C:\\repo\\.worktrees\\parser",
    sourceRepository: "C:\\repo",
    repositoryName: "repo",
    kind: "manual",
    creationType: "linked",
    gitReference: "feature/parser",
    headCommit: "abc123",
    sessionId: "session-42",
    creatorProcessId: 1234,
    createdAt: "2026-07-18T08:00:00Z",
    lastAccessedAt: "2026-07-18T08:05:00Z",
    status: "alive",
    metadata: { label: "Parser experiment", userProvided: true }
  };
}

function emitWorktreeEvent(bridge: RecordingBridge, event: unknown): void {
  act(() => bridge.emit(event as HostEvent));
}

function emitMaintenanceEvent(bridge: RecordingBridge, event: Record<string, unknown>): void {
  act(() => bridge.emit(event as unknown as HostEvent));
}

function commandsOfType(bridge: RecordingBridge, type: string): Record<string, unknown>[] {
  return (bridge.commands as unknown as Record<string, unknown>[])
    .filter((command) => command.type === type);
}

function lastCommandOfType(bridge: RecordingBridge, type: string): Record<string, unknown> {
  const command = commandsOfType(bridge, type).at(-1);
  expect(command).toBeDefined();
  return command!;
}

function sessionListCommandPayloads(bridge: RecordingBridge): Record<string, unknown>[] {
  return commandsOfType(bridge, "session/list").map((command) => {
    expect(command.requestId).toEqual(expect.stringMatching(maintenanceRequestIdPattern));
    const { requestId: _, ...payload } = command;
    return payload;
  });
}

class RecordingBridge implements HostBridge {
  readonly available = true;
  readonly commands: HostCommand[] = [];
  private readonly listeners = new Set<(event: HostEvent) => void>();
  private readonly pendingSessionListRequestIds: string[] = [];

  constructor(
    private readonly initialWorkspace: Extract<HostEvent, { type: "workspace/selected" }> | null = {
      type: "workspace/selected",
      path: "C:\\workspace",
      workspaceGeneration: 1
    }
  ) {}

  send = vi.fn((command: HostCommand) => {
    this.commands.push(command);
    if (command.type === "session/list") {
      if (!command.cursor) {
        this.pendingSessionListRequestIds.length = 0;
      }
      this.pendingSessionListRequestIds.push(command.requestId);
    }
  });

  subscribe(listener: (event: HostEvent) => void): () => void {
    this.listeners.add(listener);
    if (this.initialWorkspace) {
      listener(this.initialWorkspace);
    }
    return () => this.listeners.delete(listener);
  }

  emit(event: HostEvent, correlateSessionList = true): void {
    let deliveredEvent = event;
    if (event.type === "session/list/changed") {
      if (event.requestId) {
        const requestIndex = this.pendingSessionListRequestIds.indexOf(event.requestId);
        if (requestIndex >= 0) {
          this.pendingSessionListRequestIds.splice(requestIndex, 1);
        }
      } else if (correlateSessionList) {
        const requestId = this.pendingSessionListRequestIds.shift();
        if (requestId) {
          deliveredEvent = { ...event, requestId };
        }
      }
    }
    for (const listener of this.listeners) {
      listener(deliveredEvent);
    }
  }
}
