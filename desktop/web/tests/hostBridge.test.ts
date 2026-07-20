import { describe, expect, it, vi } from "vitest";
import {
  createHostBridge as createUninitializedHostBridge,
  type HostBridge,
  type HostCommand,
  type HostEvent
} from "../src/hostBridge";

const firstDocumentToken =
  "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";
const secondDocumentToken =
  "ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789";

function createHostBridge(
  transport?: Parameters<typeof createUninitializedHostBridge>[0]
): HostBridge {
  if (!transport) {
    return createUninitializedHostBridge(transport);
  }
  let tokenHandler: ((event: MessageEvent<unknown>) => void) | undefined;
  const bridge = createUninitializedHostBridge({
    ...transport,
    addEventListener(name, handler) {
      tokenHandler ??= handler;
      transport.addEventListener?.(name, handler);
    },
    postMessage(message) {
      if (typeof message !== "object" || message === null ||
          !Object.hasOwn(message, "documentToken")) {
        transport.postMessage(message);
        return;
      }
      const { documentToken: _, ...legacyEnvelope } = message as Record<string, unknown>;
      transport.postMessage(legacyEnvelope);
    }
  });
  tokenHandler?.(new MessageEvent("message", {
    data: {
      schemaVersion: 1,
      type: "host/document-token",
      documentToken: firstDocumentToken
    }
  }));
  return bridge;
}

describe("createHostBridge", () => {
  it("queues mutating commands until the native document token arrives", () => {
    let messageHandler: ((event: MessageEvent<unknown>) => void) | undefined;
    const postMessage = vi.fn();
    const bridge = createUninitializedHostBridge({
      postMessage,
      addEventListener: (_name, handler) => {
        messageHandler = handler;
      }
    });
    const received: HostEvent[] = [];
    bridge.subscribe((event) => received.push(event));

    const commands: HostCommand[] = [
      {
        type: "session/archive",
        requestId: "00000000-0000-4000-8000-000000000010",
        sessionId: "session-42",
        archived: true
      },
      {
        type: "backup/restore",
        requestId: "00000000-0000-4000-8000-000000000001"
      },
      {
        type: "update/apply",
        requestId: "00000000-0000-4000-8000-000000000002"
      },
      {
        type: "permission/respond",
        requestId: "permission-42",
        outcome: "cancelled"
      }
    ];
    commands.forEach((command) => bridge.send(command));

    expect(postMessage).not.toHaveBeenCalled();

    messageHandler?.(new MessageEvent("message", {
      data: {
        schemaVersion: 1,
        type: "host/document-token",
        documentToken: firstDocumentToken
      }
    }));

    expect(postMessage).toHaveBeenCalledTimes(commands.length);
    expect(postMessage.mock.calls.map(([message]) => message)).toEqual(
      commands.map((command) => ({
        schemaVersion: 1,
        documentToken: firstDocumentToken,
        ...command
      }))
    );
    expect(received).toEqual([]);
  });

  it("uses the latest native token without sharing it between bridge instances", () => {
    const first = hostBridgeWithoutToken();
    const second = hostBridgeWithoutToken();

    first.initialize(firstDocumentToken);
    second.initialize(secondDocumentToken);
    first.bridge.send({
      type: "session/archive",
      requestId: "00000000-0000-4000-8000-000000000011",
      sessionId: "first",
      archived: true
    });
    second.bridge.send({
      type: "session/archive",
      requestId: "00000000-0000-4000-8000-000000000012",
      sessionId: "second",
      archived: true
    });

    expect(first.postMessage).toHaveBeenLastCalledWith({
      schemaVersion: 1,
      documentToken: firstDocumentToken,
      type: "session/archive",
      requestId: "00000000-0000-4000-8000-000000000011",
      sessionId: "first",
      archived: true
    });
    expect(second.postMessage).toHaveBeenLastCalledWith({
      schemaVersion: 1,
      documentToken: secondDocumentToken,
      type: "session/archive",
      requestId: "00000000-0000-4000-8000-000000000012",
      sessionId: "second",
      archived: true
    });

    first.initialize(secondDocumentToken);
    first.bridge.send({ type: "ui/ready" });
    expect(first.postMessage).toHaveBeenLastCalledWith({
      schemaVersion: 1,
      documentToken: secondDocumentToken,
      type: "ui/ready"
    });
  });

  it("accepts only a version for trusted background update availability", () => {
    let messageHandler: ((event: MessageEvent<unknown>) => void) | undefined;
    const bridge = createHostBridge({
      postMessage: vi.fn(),
      addEventListener: (_name, handler) => {
        messageHandler = handler;
      }
    });
    const received: HostEvent[] = [];
    bridge.subscribe((event) => received.push(event));

    messageHandler?.(new MessageEvent("message", {
      data: {
        schemaVersion: 1,
        type: "update/background-available",
        version: "2.3.4"
      }
    }));
    for (const data of [
      { version: "not-a-version" },
      { version: `1.0.0+${"a".repeat(257)}` },
      { version: "2.3.4", manifestUrl: "https://private.example/update.json" },
      { version: "2.3.4", message: "signature failed" }
    ]) {
      messageHandler?.(new MessageEvent("message", {
        data: { schemaVersion: 1, type: "update/background-available", ...data }
      }));
    }

    expect(received).toEqual([{
      type: "update/background-available",
      version: "2.3.4"
    }]);
  });

  it("correlates bounded workspace context commands and events", () => {
    const postMessage = vi.fn();
    let messageHandler: ((event: MessageEvent<unknown>) => void) | undefined;
    const bridge = createHostBridge({
      postMessage,
      addEventListener: (_name, handler) => {
        messageHandler = handler;
      }
    });
    const received: HostEvent[] = [];
    bridge.subscribe((event) => received.push(event));
    const requestIds = [1, 2, 3, 4].map((value) =>
      `00000000-0000-4000-8000-${String(value).padStart(12, "0")}`);

    bridge.send({
      type: "workspace/context/instructions/list",
      requestId: requestIds[0],
      workspaceGeneration: 7
    });
    bridge.send({
      type: "workspace/context/file/read",
      requestId: requestIds[1],
      workspaceGeneration: 7,
      relativePath: "AGENTS.md"
    });
    bridge.send({
      type: "workspace/context/instructions/write",
      requestId: requestIds[2],
      workspaceGeneration: 7,
      relativePath: "AGENTS.md",
      content: "# Rules\n"
    });
    bridge.send({
      type: "workspace/context/file/search",
      requestId: requestIds[3],
      workspaceGeneration: 7,
      query: "parser"
    });

    const file = {
      relativePath: "AGENTS.md",
      byteLength: 8,
      lastWriteTime: "2026-07-18T08:30:00Z"
    };
    for (const event of [
      {
        schemaVersion: 1,
        type: "workspace/context/instructions/list",
        requestId: requestIds[0],
        workspaceGeneration: 7,
        files: [file]
      },
      {
        schemaVersion: 1,
        type: "workspace/context/file/read",
        requestId: requestIds[1],
        workspaceGeneration: 7,
        relativePath: "AGENTS.md",
        content: "# Rules\n"
      },
      {
        schemaVersion: 1,
        type: "workspace/context/instructions/write",
        requestId: requestIds[2],
        workspaceGeneration: 7,
        relativePath: "AGENTS.md"
      },
      {
        schemaVersion: 1,
        type: "workspace/context/file/search",
        requestId: requestIds[3],
        workspaceGeneration: 7,
        query: "parser",
        files: [{ ...file, relativePath: "src/Parser.cs" }]
      }
    ]) {
      messageHandler?.(new MessageEvent("message", { data: event }));
    }

    expect(postMessage).toHaveBeenCalledTimes(4);
    expect(received.map((event) => event.type)).toEqual([
      "workspace/context/instructions/list",
      "workspace/context/file/read",
      "workspace/context/instructions/write",
      "workspace/context/file/search"
    ]);
  });

  it("rejects stale or secret-bearing workspace context events", () => {
    let messageHandler: ((event: MessageEvent<unknown>) => void) | undefined;
    const bridge = createHostBridge({
      postMessage: vi.fn(),
      addEventListener: (_name, handler) => {
        messageHandler = handler;
      }
    });
    const received: HostEvent[] = [];
    bridge.subscribe((event) => received.push(event));
    const requestId = "00000000-0000-4000-8000-000000000001";
    bridge.send({
      type: "workspace/context/file/read",
      requestId,
      workspaceGeneration: 7,
      relativePath: "AGENTS.md"
    });

    for (const data of [
      { workspaceGeneration: 6, relativePath: "AGENTS.md", content: "rules" },
      { workspaceGeneration: 7, relativePath: "other.md", content: "rules" },
      {
        workspaceGeneration: 7,
        relativePath: "AGENTS.md",
        content: "rules",
        workspacePath: "C:\\private"
      },
      {
        workspaceGeneration: 7,
        relativePath: "AGENTS.md",
        content: "x".repeat(512 * 1024 + 1)
      }
    ]) {
      messageHandler?.(new MessageEvent("message", {
        data: {
          schemaVersion: 1,
          type: "workspace/context/file/read",
          requestId,
          ...data
        }
      }));
    }
    messageHandler?.(new MessageEvent("message", {
      data: {
        schemaVersion: 1,
        type: "workspace/context/file/read",
        requestId,
        workspaceGeneration: 7,
        relativePath: "AGENTS.md",
        content: "rules"
      }
    }));

    expect(received).toEqual([{
      type: "workspace/context/file/read",
      requestId,
      workspaceGeneration: 7,
      relativePath: "AGENTS.md",
      content: "rules"
    }]);
  });

  it("only sends AGENTS.md reads and enforces the UTF-8 workspace content budget", () => {
    const postMessage = vi.fn();
    const bridge = createHostBridge({ postMessage });

    bridge.send({
      type: "workspace/context/file/read",
      requestId: "00000000-0000-4000-8000-000000000011",
      workspaceGeneration: 7,
      relativePath: "src/secrets.env"
    });
    bridge.send({
      type: "workspace/context/instructions/write",
      requestId: "00000000-0000-4000-8000-000000000012",
      workspaceGeneration: 7,
      relativePath: "AGENTS.md",
      content: "界".repeat(174_763)
    });

    expect(postMessage).not.toHaveBeenCalled();
  });

  it("replaces superseded file searches and caps pending correlated requests", () => {
    const { bridge, emit, received } = hostEventHarness();
    const firstRequestId = "00000000-0000-4000-8000-000000000021";
    const secondRequestId = "00000000-0000-4000-8000-000000000022";
    bridge.send({
      type: "workspace/context/file/search",
      requestId: firstRequestId,
      workspaceGeneration: 7,
      query: "old"
    });
    bridge.send({
      type: "workspace/context/file/search",
      requestId: secondRequestId,
      workspaceGeneration: 7,
      query: "new"
    });

    emit({
      schemaVersion: 1,
      type: "workspace/context/file/search",
      requestId: firstRequestId,
      workspaceGeneration: 7,
      query: "old",
      files: []
    });
    emit({
      schemaVersion: 1,
      type: "workspace/context/file/search",
      requestId: secondRequestId,
      workspaceGeneration: 7,
      query: "new",
      files: []
    });

    expect(received).toEqual([{
      type: "workspace/context/file/search",
      requestId: secondRequestId,
      workspaceGeneration: 7,
      query: "new",
      files: []
    }]);

    const postMessage = vi.fn();
    const boundedBridge = createHostBridge({ postMessage });
    for (let index = 0; index < 300; index += 1) {
      boundedBridge.send({
        type: "update/check",
        requestId: `10000000-0000-4000-8000-${index.toString(16).padStart(12, "0")}`
      });
    }
    expect(postMessage).toHaveBeenCalledTimes(256);
  });

  it("releases stale workspace requests when the workspace generation changes", () => {
    const postMessage = vi.fn();
    let messageHandler: ((event: MessageEvent<unknown>) => void) | undefined;
    const bridge = createHostBridge({
      postMessage,
      addEventListener: (_name, handler) => {
        messageHandler = handler;
      }
    });
    const received: HostEvent[] = [];
    bridge.subscribe((event) => received.push(event));

    for (let index = 0; index < 256; index += 1) {
      bridge.send({
        type: "workspace/context/instructions/list",
        requestId: `20000000-0000-4000-8000-${index.toString(16).padStart(12, "0")}`,
        workspaceGeneration: 7
      });
    }
    bridge.send({
      type: "update/check",
      requestId: "30000000-0000-4000-8000-000000000001"
    });
    expect(postMessage).toHaveBeenCalledTimes(256);

    messageHandler?.(new MessageEvent("message", {
      data: {
        schemaVersion: 1,
        type: "workspace/selected",
        path: "C:\\workspace-b",
        workspaceGeneration: 8
      }
    }));
    bridge.send({
      type: "update/check",
      requestId: "30000000-0000-4000-8000-000000000002"
    });

    expect(postMessage).toHaveBeenCalledTimes(257);
    expect(received).toEqual([{
      type: "workspace/selected",
      path: "C:\\workspace-b",
      workspaceGeneration: 8
    }]);
  });

  it("correlates bounded memory browser commands, capabilities, and results", () => {
    const postMessage = vi.fn();
    let messageHandler: ((event: MessageEvent<unknown>) => void) | undefined;
    const bridge = createHostBridge({
      postMessage,
      addEventListener: (_name, handler) => {
        messageHandler = handler;
      }
    });
    const received: HostEvent[] = [];
    bridge.subscribe((event) => received.push(event));
    const requestIds = [31, 32, 33, 34, 35].map((value) =>
      `00000000-0000-4000-8000-${String(value).padStart(12, "0")}`);
    const confirmationToken = "A".repeat(64);

    bridge.send({
      type: "memory/list",
      requestId: requestIds[0],
      workspaceGeneration: 7,
      sessionId: "session-42"
    });
    bridge.send({
      type: "memory/read",
      requestId: requestIds[1],
      workspaceGeneration: 7,
      sessionId: "session-42",
      fileId: "workspace"
    });
    bridge.send({
      type: "memory/write",
      requestId: requestIds[2],
      workspaceGeneration: 7,
      sessionId: "session-42",
      fileId: "workspace",
      content: "# Memory\n",
      confirmed: false
    });
    bridge.send({
      type: "memory/delete",
      requestId: requestIds[3],
      workspaceGeneration: 7,
      sessionId: "session-42",
      fileId: "session/2026-07-19.md",
      confirmed: false
    });
    bridge.send({
      type: "memory/write",
      requestId: requestIds[4],
      workspaceGeneration: 7,
      sessionId: "session-42",
      fileId: "workspace",
      content: "# Memory\n",
      confirmed: true,
      confirmationToken
    });
    expect(postMessage).toHaveBeenCalledTimes(5);

    const file = {
      id: "workspace",
      scope: "workspace",
      name: "MEMORY.md",
      byteLength: 9,
      modifiedAt: "2026-07-19T08:30:00Z",
      writable: true
    };
    for (const data of [{
      schemaVersion: 1,
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
    }, {
      schemaVersion: 1,
      type: "memory/listed",
      requestId: requestIds[0],
      workspaceGeneration: 7,
      sessionId: "session-42",
      files: [file],
      truncated: false
    }, {
      schemaVersion: 1,
      type: "memory/document",
      requestId: requestIds[1],
      workspaceGeneration: 7,
      sessionId: "session-42",
      file,
      content: "# Memory\n"
    }, {
      schemaVersion: 1,
      type: "memory/mutation",
      requestId: requestIds[2],
      workspaceGeneration: 7,
      sessionId: "session-42",
      operation: "write",
      fileId: "workspace",
      status: "confirmation_required",
      message: "This operation requires confirmation.",
      confirmationToken
    }, {
      schemaVersion: 1,
      type: "memory/mutation",
      requestId: requestIds[3],
      workspaceGeneration: 7,
      sessionId: "session-42",
      operation: "delete",
      fileId: "session/2026-07-19.md",
      status: "not_found",
      message: "The memory file was not found."
    }, {
      schemaVersion: 1,
      type: "memory/mutation",
      requestId: requestIds[4],
      workspaceGeneration: 7,
      sessionId: "session-42",
      operation: "write",
      fileId: "workspace",
      status: "success",
      message: "The memory file was saved.",
      file
    }]) {
      messageHandler?.(new MessageEvent("message", { data }));
    }

    expect(received.map((event) => event.type)).toEqual([
      "memory/capabilities",
      "memory/listed",
      "memory/document",
      "memory/mutation",
      "memory/mutation",
      "memory/mutation"
    ]);
    expect(received[3]).toMatchObject({ confirmationToken });
  });

  it("rejects unsafe memory commands and uncorrelated or path-bearing results", () => {
    const postMessage = vi.fn();
    let messageHandler: ((event: MessageEvent<unknown>) => void) | undefined;
    const bridge = createHostBridge({
      postMessage,
      addEventListener: (_name, handler) => {
        messageHandler = handler;
      }
    });
    const received: HostEvent[] = [];
    bridge.subscribe((event) => received.push(event));

    bridge.send({
      type: "memory/read",
      requestId: "00000000-0000-4000-8000-000000000041",
      workspaceGeneration: 7,
      sessionId: "session-42",
      fileId: "../MEMORY.md"
    });
    bridge.send({
      type: "memory/write",
      requestId: "00000000-0000-4000-8000-000000000042",
      workspaceGeneration: 7,
      sessionId: "session-42",
      fileId: "workspace",
      content: "界".repeat((64 * 1024 / 3) + 1),
      confirmed: true
    });
    bridge.send({
      type: "memory/delete",
      requestId: "00000000-0000-4000-8000-000000000043",
      workspaceGeneration: 7,
      sessionId: "session-42",
      fileId: "workspace",
      confirmed: true
    });
    expect(postMessage).not.toHaveBeenCalled();

    const challengeRequestId = "00000000-0000-4000-8000-000000000044";
    bridge.send({
      type: "memory/write",
      requestId: challengeRequestId,
      workspaceGeneration: 7,
      sessionId: "session-42",
      fileId: "workspace",
      content: "safe",
      confirmed: false
    });
    expect(postMessage).toHaveBeenCalledTimes(1);
    messageHandler?.(new MessageEvent("message", {
      data: {
        schemaVersion: 1,
        type: "memory/mutation",
        requestId: challengeRequestId,
        workspaceGeneration: 7,
        sessionId: "session-42",
        operation: "write",
        fileId: "workspace",
        status: "confirmation_required",
        message: "Confirm"
      }
    }));
    expect(received).toEqual([]);
    messageHandler?.(new MessageEvent("message", {
      data: {
        schemaVersion: 1,
        type: "memory/mutation",
        requestId: challengeRequestId,
        workspaceGeneration: 7,
        sessionId: "session-42",
        operation: "write",
        fileId: "workspace",
        status: "confirmation_required",
        message: "Confirm",
        confirmationToken: "B".repeat(64)
      }
    }));
    expect(received).toHaveLength(1);

    messageHandler?.(new MessageEvent("message", {
      data: {
        schemaVersion: 1,
        type: "memory/listed",
        requestId: "00000000-0000-4000-8000-000000000099",
        workspaceGeneration: 7,
        sessionId: "session-42",
        files: [],
        truncated: false,
        workspacePath: "C:\\private"
      }
    }));
    expect(received).toHaveLength(1);
  });

  it("adds the protocol version to commands sent to WebView2", () => {
    const postMessage = vi.fn();
    const bridge = createHostBridge({ postMessage });

    bridge.send({
      type: "engine/prompt",
      text: "检查当前改动",
      executionProfile: "NativeProtected",
      sessionMode: "plan",
      nativeRiskAcknowledged: true,
      workspaceGeneration: 0
    });

    expect(postMessage).toHaveBeenCalledWith({
      schemaVersion: 1,
      type: "engine/prompt",
      text: "检查当前改动",
      executionProfile: "NativeProtected",
      sessionMode: "plan",
      nativeRiskAcknowledged: true,
      workspaceGeneration: 0
    });
  });

  it("parses authoritative session mode changes", () => {
    let messageHandler: ((event: MessageEvent<unknown>) => void) | undefined;
    const bridge = createHostBridge({
      postMessage: vi.fn(),
      addEventListener: (_name, handler) => {
        messageHandler = handler;
      }
    });
    const received: HostEvent[] = [];
    bridge.subscribe((event) => received.push(event));

    messageHandler?.(new MessageEvent("message", {
      data: {
        schemaVersion: 1,
        type: "session/mode/changed",
        sessionId: "session-42",
        mode: "plan",
        planAvailable: true
      }
    }));

    expect(received).toEqual([{
      type: "session/mode/changed",
      sessionId: "session-42",
      mode: "plan",
      planAvailable: true
    }]);
  });

  it("rejects malformed session mode changes", () => {
    let messageHandler: ((event: MessageEvent<unknown>) => void) | undefined;
    const bridge = createHostBridge({
      postMessage: vi.fn(),
      addEventListener: (_name, handler) => {
        messageHandler = handler;
      }
    });
    const received: HostEvent[] = [];
    bridge.subscribe((event) => received.push(event));

    for (const data of [
      { sessionId: "", mode: "plan", planAvailable: true },
      { sessionId: "session-42", mode: "Plan", planAvailable: true },
      { sessionId: "session-42", mode: " plan ", planAvailable: true },
      { sessionId: "session-42", mode: "future", planAvailable: true },
      { sessionId: "session-42", mode: null, planAvailable: true },
      { sessionId: "session-42", mode: 1, planAvailable: true },
      { sessionId: "session-42", mode: "plan", planAvailable: "true" },
      { sessionId: "session-42", mode: "plan" }
    ]) {
      messageHandler?.(new MessageEvent("message", {
        data: { schemaVersion: 1, type: "session/mode/changed", ...data }
      }));
    }

    expect(received).toEqual([]);
  });

  it("sends versioned modal gate changes to WebView2", () => {
    const postMessage = vi.fn();
    const bridge = createHostBridge({ postMessage });

    bridge.send({ type: "ui/modal", isOpen: true });
    bridge.send({ type: "ui/modal", isOpen: false });

    expect(postMessage).toHaveBeenNthCalledWith(1, {
      schemaVersion: 1,
      type: "ui/modal",
      isOpen: true
    });
    expect(postMessage).toHaveBeenNthCalledWith(2, {
      schemaVersion: 1,
      type: "ui/modal",
      isOpen: false
    });
  });

  it("sends strict session list, open, and rename commands", () => {
    const postMessage = vi.fn();
    const bridge = createHostBridge({ postMessage });

    bridge.send({
      type: "session/list",
      requestId: "11111111-1111-4111-8111-111111111111",
      query: "登录",
      cursor: "page-2",
      limit: 50
    });
    bridge.send({
      type: "session/open",
      sessionId: "session-42",
      workspacePath: "C:\\workspace",
      executionProfile: "WslStrict"
    });
    bridge.send({
      type: "session/rename",
      requestId: "33333333-3333-4333-8333-333333333333",
      sessionId: "session-42",
      title: "检查登录流程",
      workspacePath: "C:\\workspace"
    });
    bridge.send({
      type: "session/list",
      requestId: "11111111-1111-4111-8111-111111111111",
      query: "重复请求",
      limit: 50
    });
    bridge.send({
      type: "session/list",
      requestId: "not-a-request-id",
      query: "无效请求",
      limit: 50
    });
    bridge.send({
      type: "session/list",
      requestId: "AAAAAAAA-AAAA-4AAA-8AAA-AAAAAAAAAAAA",
      query: "大写请求",
      limit: 50
    });

    expect(postMessage).toHaveBeenNthCalledWith(1, {
      schemaVersion: 1,
      type: "session/list",
      requestId: "11111111-1111-4111-8111-111111111111",
      query: "登录",
      cursor: "page-2",
      limit: 50
    });
    expect(postMessage).toHaveBeenNthCalledWith(2, {
      schemaVersion: 1,
      type: "session/open",
      sessionId: "session-42",
      workspacePath: "C:\\workspace",
      executionProfile: "WslStrict"
    });
    expect(postMessage).toHaveBeenNthCalledWith(3, {
      schemaVersion: 1,
      type: "session/rename",
      requestId: "33333333-3333-4333-8333-333333333333",
      sessionId: "session-42",
      title: "检查登录流程",
      workspacePath: "C:\\workspace"
    });
    expect(postMessage).toHaveBeenCalledTimes(3);
  });

  it("caps pending session list requests at the shared host limit", () => {
    const { bridge, postMessage } = hostEventHarness();
    for (let index = 0; index <= 256; index += 1) {
      bridge.send({
        type: "session/list",
        requestId: `70000000-0000-4000-8000-${index.toString(16).padStart(12, "0")}`,
        query: "",
        limit: 50
      });
    }

    expect(postMessage).toHaveBeenCalledTimes(256);
  });

  it("does not recycle a session list id while its request is still pending", () => {
    const { bridge, emit, postMessage } = hostEventHarness();
    const pendingRequestId = "71000000-0000-4000-8000-000000000000";
    bridge.send({
      type: "session/list",
      requestId: pendingRequestId,
      query: "pending",
      limit: 50
    });

    for (let index = 1; index <= 4096; index += 1) {
      const requestId = `72000000-0000-4000-8000-${index.toString(16).padStart(12, "0")}`;
      bridge.send({ type: "session/list", requestId, query: "", limit: 50 });
      emit({
        schemaVersion: 1,
        type: "session/list/changed",
        requestId,
        sessions: []
      });
    }

    expect(postMessage).toHaveBeenCalledTimes(4097);
    bridge.send({
      type: "session/list",
      requestId: pendingRequestId,
      query: "reused",
      limit: 50
    });
    expect(postMessage).toHaveBeenCalledTimes(4097);
  });

  it("sends archived session queries and archive mutations", () => {
    const postMessage = vi.fn();
    const bridge = createHostBridge({ postMessage });

    bridge.send({
      type: "session/list",
      requestId: "22222222-2222-4222-8222-222222222222",
      query: "parser",
      cursor: "archive-page-2",
      limit: 50,
      archived: true
    });
    bridge.send({
      type: "session/archive",
      requestId: "33333333-3333-4333-8333-333333333333",
      sessionId: "session-42",
      archived: true
    });
    bridge.send({
      type: "session/archive",
      requestId: "44444444-4444-4444-8444-444444444444",
      sessionId: "session-42",
      archived: false
    });

    expect(postMessage).toHaveBeenNthCalledWith(1, {
      schemaVersion: 1,
      type: "session/list",
      requestId: "22222222-2222-4222-8222-222222222222",
      query: "parser",
      cursor: "archive-page-2",
      limit: 50,
      archived: true
    });
    expect(postMessage).toHaveBeenNthCalledWith(2, {
      schemaVersion: 1,
      type: "session/archive",
      requestId: "33333333-3333-4333-8333-333333333333",
      sessionId: "session-42",
      archived: true
    });
    expect(postMessage).toHaveBeenNthCalledWith(3, {
      schemaVersion: 1,
      type: "session/archive",
      requestId: "44444444-4444-4444-8444-444444444444",
      sessionId: "session-42",
      archived: false
    });
  });

  it("sends strict session history commands", () => {
    const postMessage = vi.fn();
    const bridge = createHostBridge({ postMessage });

    for (const command of [
      {
        type: "session/fork",
        sessionId: "session-42",
        sourceWorkspacePath: "C:\\workspace",
        targetWorkspacePath: "C:\\workspace",
        targetPromptIndex: 3
      },
      { type: "session/compact", sessionId: "session-42", userContext: "Keep the contract" },
      { type: "session/rewind/points", sessionId: "session-42" },
      {
        type: "session/rewind",
        sessionId: "session-42",
        targetPromptIndex: 3,
        mode: "conversation_only",
        force: false
      }
    ] as HostCommand[]) {
      bridge.send(command);
    }

    expect(postMessage.mock.calls.map(([message]) => message)).toEqual([
      {
        schemaVersion: 1,
        type: "session/fork",
        sessionId: "session-42",
        sourceWorkspacePath: "C:\\workspace",
        targetWorkspacePath: "C:\\workspace",
        targetPromptIndex: 3
      },
      {
        schemaVersion: 1,
        type: "session/compact",
        sessionId: "session-42",
        userContext: "Keep the contract"
      },
      { schemaVersion: 1, type: "session/rewind/points", sessionId: "session-42" },
      {
        schemaVersion: 1,
        type: "session/rewind",
        sessionId: "session-42",
        targetPromptIndex: 3,
        mode: "conversation_only",
        force: false
      }
    ]);
  });

  it("sends runtime dashboard commands", () => {
    const postMessage = vi.fn();
    const bridge = createHostBridge({ postMessage });

    for (const command of [
      { type: "runtime/dashboard/refresh", sessionId: "session-42" },
      { type: "runtime/task/kill", sessionId: "session-42", taskId: "task-7" },
      { type: "runtime/subagent/get", sessionId: "session-42", subagentId: "subagent-7" },
      { type: "runtime/subagent/cancel", sessionId: "session-42", subagentId: "subagent-7" }
    ] as HostCommand[]) {
      bridge.send(command);
    }

    expect(postMessage.mock.calls.map(([message]) => message)).toEqual([
      { schemaVersion: 1, type: "runtime/dashboard/refresh", sessionId: "session-42" },
      {
        schemaVersion: 1,
        type: "runtime/task/kill",
        sessionId: "session-42",
        taskId: "task-7"
      },
      {
        schemaVersion: 1,
        type: "runtime/subagent/get",
        sessionId: "session-42",
        subagentId: "subagent-7"
      },
      {
        schemaVersion: 1,
        type: "runtime/subagent/cancel",
        sessionId: "session-42",
        subagentId: "subagent-7"
      }
    ]);
  });

  it("sends all versioned worktree lifecycle commands", () => {
    const postMessage = vi.fn();
    const bridge = createHostBridge({ postMessage });
    const commands = [
      {
        type: "worktree/create",
        workspaceGeneration: 7,
        sessionId: "session-42",
        copyMode: "dirty",
        gitReference: "feature/parser",
        copyIgnoredInBackground: true,
        ignoredSkipPatterns: ["target/**", ".cache/**"],
        creationType: "linked",
        label: "Parser experiment"
      },
      {
        type: "worktree/list",
        workspaceGeneration: 7,
        includeAll: true,
        types: ["session", "manual"]
      },
      {
        type: "worktree/show",
        workspaceGeneration: 7,
        idOrPath: "worktree-7"
      },
      {
        type: "worktree/apply",
        workspaceGeneration: 7,
        sessionId: "session-42",
        worktreePath: "C:\\repo\\.worktrees\\parser",
        mode: "merge"
      },
      {
        type: "worktree/remove",
        workspaceGeneration: 7,
        idOrPath: "worktree-7",
        force: true,
        dryRun: false
      },
      {
        type: "worktree/gc",
        workspaceGeneration: 7,
        dryRun: true,
        maximumAgeSeconds: 86400,
        force: false
      }
    ];

    for (const command of commands) {
      bridge.send(command as unknown as HostCommand);
    }

    expect(postMessage.mock.calls.map(([message]) => message)).toEqual(
      commands.map((command) => ({ schemaVersion: 1, ...command }))
    );
  });

  it("parses all strict worktree lifecycle events", () => {
    const { emit, received } = hostEventHarness();
    const events = worktreeLifecycleEvents();

    for (const event of events) {
      emit(event);
    }

    expect(received).toEqual(events.map(({ schemaVersion: _schemaVersion, ...event }) => event));
  });

  it("rejects unknown fields in worktree envelopes and nested records", () => {
    const { emit, received } = hostEventHarness();
    const events = worktreeLifecycleEvents();

    for (const event of events) {
      emit({ ...event, unexpected: true });
    }
    emit({
      ...events[1],
      worktrees: [{ ...worktreeRecord(), unexpected: true }]
    });
    emit({
      ...events[1],
      worktrees: [{
        ...worktreeRecord(),
        metadata: { label: "Parser experiment", userProvided: true, unexpected: true }
      }]
    });
    emit({
      ...events[3],
      files: [{ ...worktreeFileChange(), unexpected: true }]
    });
    emit({
      ...events[3],
      conflicts: [{ ...worktreeConflict(), unexpected: true }]
    });
    for (const event of events) {
      emit(event);
    }

    expect(received.map((event) => event.type)).toEqual(events.map((event) => event.type));
  });

  it("rejects worktree events above record, change, and text budgets", () => {
    const { emit, received } = hostEventHarness();
    const record = worktreeRecord();
    const file = worktreeFileChange();
    const conflict = worktreeConflict();
    const twoMiB = "x".repeat(2 * 1024 * 1024);

    for (const event of [
      {
        schemaVersion: 1,
        type: "worktree/list/changed",
        workspaceGeneration: 7,
        worktrees: Array.from({ length: 4097 }, () => record)
      },
      {
        schemaVersion: 1,
        type: "worktree/applied",
        workspaceGeneration: 7,
        status: "success",
        files: Array.from({ length: 10001 }, () => file),
        conflicts: []
      },
      {
        schemaVersion: 1,
        type: "worktree/applied",
        workspaceGeneration: 7,
        status: "conflicts",
        files: [],
        conflicts: Array.from({ length: 10001 }, () => conflict)
      },
      {
        schemaVersion: 1,
        type: "worktree/applied",
        workspaceGeneration: 7,
        status: "success",
        files: [{ ...file, patch: `${twoMiB}x` }],
        conflicts: []
      },
      {
        schemaVersion: 1,
        type: "worktree/applied",
        workspaceGeneration: 7,
        status: "success",
        files: Array.from({ length: 9 }, (_, index) => ({
          ...file,
          path: `src/parser-${index}.rs`,
          patch: twoMiB
        })),
        conflicts: []
      }
    ]) {
      emit(event);
    }
    const valid = worktreeLifecycleEvents()[1];
    emit(valid);

    expect(received).toEqual([{
      type: "worktree/list/changed",
      workspaceGeneration: 7,
      worktrees: [record]
    }]);
  });

  it("sends all versioned maintenance commands without local paths", () => {
    const postMessage = vi.fn();
    const bridge = createHostBridge({ postMessage });
    const commands = [
      {
        type: "session/export",
        requestId: "11111111-1111-4111-8111-111111111111",
        sessionId: "session-42"
      },
      {
        type: "session/import",
        requestId: "22222222-2222-4222-8222-222222222222"
      },
      {
        type: "backup/create",
        requestId: "33333333-3333-4333-8333-333333333333"
      },
      {
        type: "backup/restore",
        requestId: "44444444-4444-4444-8444-444444444444"
      },
      {
        type: "update/check",
        requestId: "55555555-5555-4555-8555-555555555555"
      },
      {
        type: "update/apply",
        requestId: "66666666-6666-4666-8666-666666666666"
      }
    ] satisfies HostCommand[];

    for (const command of commands) {
      bridge.send(command);
    }

    expect(postMessage.mock.calls.map(([message]) => message)).toEqual(
      commands.map((command) => ({ schemaVersion: 1, ...command }))
    );
    for (const [message] of postMessage.mock.calls) {
      expect(message).not.toHaveProperty("path");
      expect(message).not.toHaveProperty("workspacePath");
      expect(message).not.toHaveProperty("document");
      expect(message).not.toHaveProperty("secret");
    }
  });

  it("parses strict maintenance summaries only for matching requests", () => {
    const { bridge, emit, received } = hostEventHarness();
    const commands = [
      {
        type: "session/export",
        requestId: "11111111-1111-4111-8111-111111111111",
        sessionId: "session-42"
      },
      {
        type: "session/import",
        requestId: "22222222-2222-4222-8222-222222222222"
      },
      {
        type: "backup/create",
        requestId: "33333333-3333-4333-8333-333333333333"
      },
      {
        type: "backup/restore",
        requestId: "44444444-4444-4444-8444-444444444444"
      },
      {
        type: "update/check",
        requestId: "55555555-5555-4555-8555-555555555555"
      },
      {
        type: "update/apply",
        requestId: "66666666-6666-4666-8666-666666666666"
      }
    ] satisfies HostCommand[];
    for (const command of commands) {
      bridge.send(command);
    }
    const events = [
      {
        schemaVersion: 1,
        type: "session/exported",
        requestId: commands[0].requestId,
        sessionId: "session-42",
        fileName: "session-42.agentdesk-session.json"
      },
      {
        schemaVersion: 1,
        type: "session/imported",
        requestId: commands[1].requestId,
        sessionId: "session-imported",
        workspacePath: "C:\\workspace"
      },
      {
        schemaVersion: 1,
        type: "backup/completed",
        requestId: commands[2].requestId,
        operation: "create",
        fileCount: 27,
        totalBytes: 65_536,
        restartRequired: false
      },
      {
        schemaVersion: 1,
        type: "backup/completed",
        requestId: commands[3].requestId,
        operation: "restore",
        fileCount: 27,
        totalBytes: 65_536,
        restartRequired: true
      },
      {
        schemaVersion: 1,
        type: "update/status",
        requestId: commands[4].requestId,
        status: "checking"
      },
      {
        schemaVersion: 1,
        type: "update/status",
        requestId: commands[4].requestId,
        status: "available",
        version: "1.2.0"
      },
      {
        schemaVersion: 1,
        type: "update/status",
        requestId: commands[5].requestId,
        status: "launching",
        version: "1.2.0"
      }
    ];

    for (const event of events) {
      emit(event);
    }

    expect(received).toEqual(events.map(({ schemaVersion: _schemaVersion, ...event }) => event));
  });

  it("rejects uncorrelated, duplicate, malformed, and sensitive maintenance events", () => {
    const { bridge, emit, received } = hostEventHarness();
    const exportId = "11111111-1111-4111-8111-111111111111";
    const importId = "22222222-2222-4222-8222-222222222222";
    const backupId = "33333333-3333-4333-8333-333333333333";
    const updateId = "44444444-4444-4444-8444-444444444444";
    const errorId = "55555555-5555-4555-8555-555555555555";
    for (const command of [
      { type: "session/export", requestId: exportId, sessionId: "session-42" },
      { type: "session/import", requestId: importId },
      { type: "backup/create", requestId: backupId },
      { type: "update/check", requestId: updateId },
      { type: "backup/restore", requestId: errorId }
    ] satisfies HostCommand[]) {
      bridge.send(command);
    }

    for (const event of [
      {
        schemaVersion: 1,
        type: "session/exported",
        requestId: "99999999-9999-4999-8999-999999999999",
        sessionId: "session-42",
        fileName: "unknown.agentdesk-session.json"
      },
      {
        schemaVersion: 1,
        type: "session/exported",
        requestId: exportId,
        sessionId: "session-42",
        fileName: "C:\\Users\\private\\session.json"
      },
      {
        schemaVersion: 1,
        type: "session/exported",
        requestId: exportId,
        sessionId: "session-42",
        fileName: "session.json",
        document: { messages: ["private prompt"] }
      },
      {
        schemaVersion: 1,
        type: "session/imported",
        requestId: importId,
        sessionId: "session-imported",
        workspacePath: "C:\\workspace",
        sourcePath: "C:\\Users\\private\\session.json"
      },
      {
        schemaVersion: 1,
        type: "backup/completed",
        requestId: backupId,
        operation: "create",
        fileCount: 3,
        totalBytes: 1024,
        restartRequired: false,
        archivePath: "C:\\Users\\private\\backup.zip"
      },
      {
        schemaVersion: 1,
        type: "update/status",
        requestId: updateId,
        status: "available",
        version: "1.2.0",
        packageUrl: "https://example.invalid/private.zip",
        sha256: "secret-package-hash"
      },
      {
        schemaVersion: 1,
        type: "maintenance/error",
        requestId: errorId,
        operation: "backup-restore",
        message: "C:\\Users\\private\\backup.zip failed",
        exception: "private stack trace"
      },
      {
        schemaVersion: 1,
        type: "maintenance/cancelled",
        requestId: `bad\nrequest`,
        operation: "backup-restore"
      },
      {
        schemaVersion: 1,
        type: "maintenance/cancelled",
        requestId: "x".repeat(1025),
        operation: "backup-restore"
      }
    ]) {
      emit(event);
    }

    const validExport = {
      schemaVersion: 1,
      type: "session/exported",
      requestId: exportId,
      sessionId: "session-42",
      fileName: "session.json"
    };
    emit(validExport);
    emit(validExport);
    emit({
      schemaVersion: 1,
      type: "maintenance/error",
      requestId: errorId,
      operation: "backup-restore"
    });
    emit({
      schemaVersion: 1,
      type: "maintenance/cancelled",
      requestId: errorId,
      operation: "backup-restore"
    });

    expect(received).toEqual([
      {
        type: "session/exported",
        requestId: exportId,
        sessionId: "session-42",
        fileName: "session.json"
      },
      {
        type: "maintenance/error",
        requestId: errorId,
        operation: "backup-restore"
      }
    ]);
  });

  it("does not post maintenance commands with invalid or reused request identifiers", () => {
    const postMessage = vi.fn();
    const bridge = createHostBridge({ postMessage });
    const requestId = "11111111-1111-4111-8111-111111111111";

    bridge.send({ type: "update/check", requestId } as HostCommand);
    bridge.send({ type: "backup/create", requestId } as HostCommand);

    for (const invalidRequestId of [
      "not-a-uuid",
      "11111111-1111-4111-8111-11111111111\n",
      "x".repeat(1025)
    ]) {
      bridge.send({ type: "update/check", requestId: invalidRequestId } as HostCommand);
    }

    expect(postMessage).toHaveBeenCalledOnce();
    expect(postMessage).toHaveBeenCalledWith({
      schemaVersion: 1,
      type: "update/check",
      requestId
    });
  });

  it("sends every cloud command without web-visible secrets or native paths", () => {
    const postMessage = vi.fn();
    const bridge = createHostBridge({ postMessage });
    const commands = [
      { type: "cloud/profile/get", requestId: "10000000-0000-4000-8000-000000000001" },
      { type: "cloud/profile/save-local", requestId: "10000000-0000-4000-8000-000000000002" },
      {
        type: "cloud/profile/save-remote",
        requestId: "10000000-0000-4000-8000-000000000003",
        baseUri: "https://cloud.example.test/",
        teamId: "team-1",
        deviceId: "device-1"
      },
      { type: "cloud/pairing/export", requestId: "10000000-0000-4000-8000-000000000004" },
      { type: "cloud/pairing/import", requestId: "10000000-0000-4000-8000-000000000005" },
      {
        type: "cloud/session/upload",
        requestId: "10000000-0000-4000-8000-000000000006",
        sessionId: "session-1"
      },
      {
        type: "cloud/session/download",
        requestId: "10000000-0000-4000-8000-000000000007",
        remoteSessionId: "remote-session-1"
      },
      {
        type: "cloud/handoff/create",
        requestId: "10000000-0000-4000-8000-000000000008",
        sessionId: "session-1",
        targetDeviceId: "device-2"
      },
      { type: "cloud/handoff/receive", requestId: "10000000-0000-4000-8000-000000000009" },
      { type: "cloud/policy/get", requestId: "10000000-0000-4000-8000-00000000000a" },
      {
        type: "cloud/policy/update",
        requestId: "10000000-0000-4000-8000-00000000000b",
        allowedExecutionProfiles: ["NativeProtected", "WslStrict"],
        remoteRunnerEnabled: true,
        uiAutomationEnabled: false,
        maximumConcurrentJobs: 4,
        allowedPluginPublishers: ["agentdesk.official"]
      },
      {
        type: "cloud/runner/register",
        requestId: "10000000-0000-4000-8000-00000000000c",
        runnerId: "runner-1",
        capabilities: ["shell", "git"]
      },
      { type: "cloud/automation/list", requestId: "10000000-0000-4000-8000-00000000000d" },
      {
        type: "cloud/automation/disable",
        requestId: "10000000-0000-4000-8000-00000000000e",
        automationId: "automation-1"
      },
      {
        type: "cloud/session/delete",
        requestId: "10000000-0000-4000-8000-00000000000f",
        remoteSessionId: "remote-session-1"
      },
      {
        type: "cloud/session/export",
        requestId: "10000000-0000-4000-8000-000000000010",
        sessionId: "session-1"
      }
    ] satisfies HostCommand[];

    for (const command of commands) {
      bridge.send(command);
    }

    expect(postMessage.mock.calls.map(([message]) => message)).toEqual(
      commands.map((command) => ({ schemaVersion: 1, ...command }))
    );
    for (const [message] of postMessage.mock.calls) {
      for (const forbidden of [
        "token", "accessToken", "passphrase", "path", "package", "envelope", "ciphertext"
      ]) {
        expect(message).not.toHaveProperty(forbidden);
      }
    }
  });

  it("parses correlated cloud summaries and rejects duplicate terminal events", () => {
    const { bridge, emit, received } = hostEventHarness();
    const commands = [
      { type: "cloud/profile/get", requestId: "20000000-0000-4000-8000-000000000001" },
      { type: "cloud/pairing/export", requestId: "20000000-0000-4000-8000-000000000002" },
      {
        type: "cloud/session/upload",
        requestId: "20000000-0000-4000-8000-000000000003",
        sessionId: "session-1"
      },
      {
        type: "cloud/session/download",
        requestId: "20000000-0000-4000-8000-000000000004",
        remoteSessionId: "remote-session-1"
      },
      {
        type: "cloud/handoff/create",
        requestId: "20000000-0000-4000-8000-000000000005",
        sessionId: "session-1",
        targetDeviceId: "device-2"
      },
      { type: "cloud/handoff/receive", requestId: "20000000-0000-4000-8000-000000000006" },
      { type: "cloud/policy/get", requestId: "20000000-0000-4000-8000-000000000007" },
      {
        type: "cloud/runner/register",
        requestId: "20000000-0000-4000-8000-000000000008",
        runnerId: "runner-1",
        capabilities: ["shell", "git"]
      },
      { type: "cloud/automation/list", requestId: "20000000-0000-4000-8000-000000000009" },
      {
        type: "cloud/automation/disable",
        requestId: "20000000-0000-4000-8000-00000000000a",
        automationId: "automation-1"
      },
      {
        type: "cloud/session/delete",
        requestId: "20000000-0000-4000-8000-00000000000b",
        remoteSessionId: "remote-session-1"
      },
      {
        type: "cloud/session/export",
        requestId: "20000000-0000-4000-8000-00000000000c",
        sessionId: "session-1"
      }
    ] satisfies HostCommand[];
    for (const command of commands) {
      bridge.send(command);
    }
    const events = [
      {
        schemaVersion: 1,
        type: "cloud/profile",
        requestId: commands[0].requestId,
        localOnly: false,
        baseUri: "https://cloud.example.test/",
        teamId: "team-1",
        deviceId: "device-1",
        hasAccessToken: true
      },
      {
        schemaVersion: 1,
        type: "cloud/pairing/completed",
        requestId: commands[1].requestId,
        operation: "export"
      },
      {
        schemaVersion: 1,
        type: "cloud/session/uploaded",
        requestId: commands[2].requestId,
        sessionId: "session-1",
        revision: 2
      },
      {
        schemaVersion: 1,
        type: "cloud/session/imported",
        requestId: commands[3].requestId,
        remoteSessionId: "remote-session-1",
        found: true,
        revision: 3,
        importedSessionId: "local-session-1"
      },
      {
        schemaVersion: 1,
        type: "cloud/handoff/created",
        requestId: commands[4].requestId,
        handoffId: "handoff-1",
        sessionId: "session-1",
        targetDeviceId: "device-2"
      },
      {
        schemaVersion: 1,
        type: "cloud/handoffs/received",
        requestId: commands[5].requestId,
        imports: [{
          handoffId: "handoff-2",
          sourceDeviceId: "device-3",
          remoteSessionId: "remote-session-2",
          importedSessionId: "local-session-2"
        }]
      },
      {
        schemaVersion: 1,
        type: "cloud/policy",
        requestId: commands[6].requestId,
        version: 5,
        allowedExecutionProfiles: ["NativeProtected", "WslStrict"],
        remoteRunnerEnabled: true,
        uiAutomationEnabled: false,
        maximumConcurrentJobs: 4,
        allowedPluginPublishers: ["agentdesk.official"]
      },
      {
        schemaVersion: 1,
        type: "cloud/runner/registered",
        requestId: commands[7].requestId,
        runnerId: "runner-1",
        capabilities: ["shell", "git"]
      },
      {
        schemaVersion: 1,
        type: "cloud/automations",
        requestId: commands[8].requestId,
        automations: [{
          automationId: "automation-1",
          name: "Nightly review",
          intervalSeconds: 3600,
          enabled: true,
          nextRunAt: "2026-07-18T12:00:00Z"
        }]
      },
      {
        schemaVersion: 1,
        type: "cloud/automation/disabled",
        requestId: commands[9].requestId,
        automationId: "automation-1",
        disabled: true
      },
      {
        schemaVersion: 1,
        type: "cloud/session/deleted",
        requestId: commands[10].requestId,
        remoteSessionId: "remote-session-1",
        found: true,
        revision: 4
      },
      {
        schemaVersion: 1,
        type: "cloud/session/exported",
        requestId: commands[11].requestId,
        sessionId: "session-1",
        fileName: "session-1.agentdesk-session.json"
      }
    ];
    for (const event of events) {
      emit(event);
      emit(event);
    }

    expect(received).toEqual(events.map(({ schemaVersion: _schemaVersion, ...event }) => event));
  });

  it("parses strict uncorrelated cloud notifications without sensitive fields", () => {
    const { emit, received } = hostEventHarness();

    emit({
      schemaVersion: 1,
      type: "cloud/notification",
      kind: "handoff-changed",
      resourceId: "handoff-1",
      policyVersion: null
    });
    emit({
      schemaVersion: 1,
      type: "cloud/notification",
      kind: "job-changed",
      resourceId: "job-1",
      policyVersion: null
    });
    emit({
      schemaVersion: 1,
      type: "cloud/notification",
      kind: "policy-changed",
      resourceId: null,
      policyVersion: 7
    });
    for (const invalid of [
      {
        kind: "handoff-changed",
        resourceId: null,
        policyVersion: null
      },
      {
        kind: "policy-changed",
        resourceId: "policy-secret",
        policyVersion: 7
      },
      {
        kind: "job-changed",
        resourceId: "job-1",
        policyVersion: null,
        ciphertext: "must-not-cross"
      }
    ]) {
      emit({ schemaVersion: 1, type: "cloud/notification", ...invalid });
    }

    expect(received).toEqual([
      { type: "cloud/notification", kind: "handoff-changed", resourceId: "handoff-1" },
      { type: "cloud/notification", kind: "job-changed", resourceId: "job-1" },
      { type: "cloud/notification", kind: "policy-changed", policyVersion: 7 }
    ]);
  });

  it("correlates runner jobs and automation creation without exposing task results", () => {
    const { bridge, emit, received, postMessage } = hostEventHarness();
    const commands = [
      {
        type: "cloud/runner/queue",
        requestId: "24000000-0000-4000-8000-000000000001",
        requiredCapability: "windows",
        task: "inspect workspace"
      },
      {
        type: "cloud/runner/claim",
        requestId: "24000000-0000-4000-8000-000000000002",
        runnerId: "runner-1",
        leaseSeconds: 60
      },
      {
        type: "cloud/runner/complete",
        requestId: "24000000-0000-4000-8000-000000000003",
        claimHandle: "claim-1",
        jobId: "job-1",
        result: "completed locally"
      },
      {
        type: "cloud/automation/create",
        requestId: "24000000-0000-4000-8000-000000000004",
        name: "Nightly review",
        intervalSeconds: 3600,
        requiredCapability: "windows",
        task: "review branch"
      },
      {
        type: "cloud/runner/claim",
        requestId: "24000000-0000-4000-8000-000000000005",
        runnerId: "runner-1",
        leaseSeconds: 60
      }
    ] satisfies HostCommand[];
    for (const command of commands) {
      bridge.send(command);
    }
    expect(postMessage.mock.calls.map(([message]) => message)).toEqual(
      commands.map((command) => ({ schemaVersion: 1, ...command }))
    );

    const events = [
      {
        schemaVersion: 1,
        type: "cloud/runner/queued",
        requestId: commands[0].requestId,
        jobId: "job-1"
      },
      {
        schemaVersion: 1,
        type: "cloud/runner/claimed",
        requestId: commands[1].requestId,
        found: true,
        claimHandle: "claim-1",
        jobId: "job-1",
        requiredCapability: "windows",
        task: "inspect workspace",
        leaseExpiresAt: "2026-07-18T12:05:00Z"
      },
      {
        schemaVersion: 1,
        type: "cloud/runner/completed",
        requestId: commands[2].requestId,
        claimHandle: "claim-1",
        jobId: "job-1"
      },
      {
        schemaVersion: 1,
        type: "cloud/automation/created",
        requestId: commands[3].requestId,
        automation: {
          automationId: "automation-2",
          name: "Nightly review",
          intervalSeconds: 3600,
          enabled: true,
          nextRunAt: "2026-07-19T00:00:00Z"
        }
      },
      {
        schemaVersion: 1,
        type: "cloud/runner/claimed",
        requestId: commands[4].requestId,
        found: false,
        claimHandle: null,
        jobId: null,
        requiredCapability: null,
        task: null,
        leaseExpiresAt: null
      }
    ];
    for (const event of events) {
      emit(event);
      emit(event);
    }

    expect(received).toEqual([
      { type: "cloud/runner/queued", requestId: commands[0].requestId, jobId: "job-1" },
      {
        type: "cloud/runner/claimed",
        requestId: commands[1].requestId,
        found: true,
        claimHandle: "claim-1",
        jobId: "job-1",
        requiredCapability: "windows",
        task: "inspect workspace",
        leaseExpiresAt: "2026-07-18T12:05:00Z"
      },
      {
        type: "cloud/runner/completed",
        requestId: commands[2].requestId,
        claimHandle: "claim-1",
        jobId: "job-1"
      },
      {
        type: "cloud/automation/created",
        requestId: commands[3].requestId,
        automation: {
          automationId: "automation-2",
          name: "Nightly review",
          intervalSeconds: 3600,
          enabled: true,
          nextRunAt: "2026-07-19T00:00:00Z"
        }
      },
      { type: "cloud/runner/claimed", requestId: commands[4].requestId, found: false }
    ]);
    expect(received).not.toContainEqual(expect.objectContaining({ result: expect.anything() }));
  });

  it("rejects mismatched runner and automation terminal events", () => {
    const { bridge, emit, received } = hostEventHarness();
    const completeId = "25000000-0000-4000-8000-000000000001";
    const claimId = "25000000-0000-4000-8000-000000000002";
    const automationId = "25000000-0000-4000-8000-000000000003";
    bridge.send({
      type: "cloud/runner/complete",
      requestId: completeId,
      claimHandle: "claim-1",
      jobId: "job-1",
      result: "done"
    });
    bridge.send({
      type: "cloud/runner/claim",
      requestId: claimId,
      runnerId: "runner-1",
      leaseSeconds: 60
    });
    bridge.send({
      type: "cloud/automation/create",
      requestId: automationId,
      name: "Nightly review",
      intervalSeconds: 3600,
      requiredCapability: "windows",
      task: "review branch"
    });

    emit({
      schemaVersion: 1,
      type: "cloud/runner/completed",
      requestId: completeId,
      claimHandle: "claim-2",
      jobId: "job-2"
    });
    emit({
      schemaVersion: 1,
      type: "cloud/runner/claimed",
      requestId: claimId,
      found: false,
      jobId: null,
      requiredCapability: null,
      task: "must-not-cross",
      leaseExpiresAt: null
    });
    emit({
      schemaVersion: 1,
      type: "cloud/automation/created",
      requestId: automationId,
      automation: {
        automationId: "automation-2",
        name: "Different automation",
        intervalSeconds: 3600,
        enabled: true,
        nextRunAt: "2026-07-19T00:00:00Z"
      }
    });

    expect(received).toEqual([]);
  });

  it("rejects mismatched, malformed, uncorrelated, and sensitive cloud events", () => {
    const { bridge, emit, received } = hostEventHarness();
    const uploadId = "30000000-0000-4000-8000-000000000001";
    const profileId = "30000000-0000-4000-8000-000000000002";
    const errorId = "30000000-0000-4000-8000-000000000003";
    bridge.send({ type: "cloud/session/upload", requestId: uploadId, sessionId: "session-1" });
    bridge.send({ type: "cloud/profile/get", requestId: profileId });
    bridge.send({ type: "cloud/policy/get", requestId: errorId });

    for (const event of [
      {
        schemaVersion: 1,
        type: "cloud/session/uploaded",
        requestId: uploadId,
        sessionId: "different-session",
        revision: 1
      },
      {
        schemaVersion: 1,
        type: "cloud/profile",
        requestId: profileId,
        localOnly: false,
        baseUri: "https://cloud.example.test/",
        teamId: "team-1",
        deviceId: "device-1",
        hasAccessToken: true,
        accessToken: "must-not-cross-web"
      },
      {
        schemaVersion: 1,
        type: "cloud/profile",
        requestId: "39999999-9999-4999-8999-999999999999",
        localOnly: true,
        baseUri: null,
        teamId: null,
        deviceId: null,
        hasAccessToken: false
      },
      {
        schemaVersion: 1,
        type: "cloud/error",
        requestId: errorId,
        operation: "policy-get",
        message: "private exception text",
        path: "C:\\Users\\private"
      },
      {
        schemaVersion: 1,
        type: "cloud/cancelled",
        requestId: errorId,
        operation: "session-upload"
      }
    ]) {
      emit(event);
    }
    emit({
      schemaVersion: 1,
      type: "cloud/error",
      requestId: errorId,
      operation: "policy-get"
    });
    emit({
      schemaVersion: 1,
      type: "cloud/cancelled",
      requestId: errorId,
      operation: "policy-get"
    });

    expect(received).toEqual([{
      type: "cloud/error",
      requestId: errorId,
      operation: "policy-get"
    }]);
  });

  it("does not post cloud commands with invalid or reused request identifiers", () => {
    const postMessage = vi.fn();
    const bridge = createHostBridge({ postMessage });
    const requestId = "40000000-0000-4000-8000-000000000001";

    bridge.send({ type: "cloud/profile/get", requestId } as HostCommand);
    bridge.send({ type: "cloud/policy/get", requestId } as HostCommand);
    bridge.send({ type: "cloud/profile/get", requestId: "not-a-uuid" } as HostCommand);

    expect(postMessage).toHaveBeenCalledOnce();
    expect(postMessage).toHaveBeenCalledWith({
      schemaVersion: 1,
      type: "cloud/profile/get",
      requestId
    });
  });

  it("correlates strict extension catalogs and action outcomes", () => {
    const { bridge, emit, received } = hostEventHarness();
    const listId = "41000000-0000-4000-8000-000000000001";
    const actionId = "41000000-0000-4000-8000-000000000002";
    bridge.send({
      type: "extensions/list",
      requestId: listId,
      workspaceGeneration: 3,
      sessionId: "session-42",
      useCache: false
    });
    bridge.send({
      type: "extensions/action",
      requestId: actionId,
      workspaceGeneration: 3,
      sessionId: "session-42",
      scope: "mcp",
      action: "toggle",
      confirmed: true,
      payload: { serverName: "github", enabled: false }
    });

    const catalog = {
      schemaVersion: 1,
      type: "extensions/catalog",
      requestId: listId,
      sessionId: "session-42",
      mcp: { servers: [] },
      skills: {
        skills: [],
        configuration: { paths: [], ignoredPaths: [], totalSkills: 0 }
      },
      hooks: { hooks: [], projectTrusted: false, loadErrorCount: 0 },
      plugins: { plugins: [] },
      marketplace: { sources: [] }
    };
    const completed = {
      schemaVersion: 1,
      type: "extensions/action/completed",
      requestId: actionId,
      sessionId: "session-42",
      scope: "mcp",
      action: "toggle",
      status: "success",
      message: "updated",
      requiresReload: true,
      requiresRestart: false
    };
    emit(catalog);
    emit(catalog);
    emit(completed);
    emit(completed);

    expect(received).toEqual([
      { ...catalog, schemaVersion: undefined },
      { ...completed, schemaVersion: undefined }
    ].map(({ schemaVersion: _schemaVersion, ...event }) => event));
  });

  it("rejects uncorrelated or secret-bearing extension payloads and reused ids", () => {
    const { bridge, emit, received, postMessage } = hostEventHarness();
    const requestId = "42000000-0000-4000-8000-000000000001";
    bridge.send({
      type: "extensions/list",
      requestId,
      workspaceGeneration: 1,
      useCache: true
    });
    bridge.send({
      type: "extensions/list",
      requestId,
      workspaceGeneration: 1,
      useCache: false
    });
    emit({
      schemaVersion: 1,
      type: "extensions/catalog",
      requestId,
      sessionId: "session-42",
      mcp: { servers: [] },
      skills: { skills: [{ metadata: { secret: "must-not-cross-web" } }] },
      hooks: { hooks: [], projectTrusted: false, loadErrorCount: 0 },
      plugins: { plugins: [] },
      marketplace: { sources: [] }
    });

    expect(postMessage).toHaveBeenCalledOnce();
    expect(received).toEqual([]);
  });

  it("parses strict runtime dashboard state and outcomes", () => {
    let messageHandler: ((event: MessageEvent<unknown>) => void) | undefined;
    const bridge = createHostBridge({
      postMessage: vi.fn(),
      addEventListener: (_name, handler) => {
        messageHandler = handler;
      }
    });
    const received: HostEvent[] = [];
    bridge.subscribe((event) => received.push(event));

    for (const data of [
      {
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
          description: "Run desktop tests",
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
      },
      {
        type: "runtime/task/killed",
        sessionId: "session-42",
        taskId: "task-7",
        outcome: "killed"
      },
      {
        type: "runtime/subagent/cancelled",
        sessionId: "session-42",
        subagentId: "subagent-7",
        outcome: "already_finished",
        terminalStatus: "completed"
      },
      {
        type: "runtime/dashboard/error",
        sessionId: "session-42",
        message: "Unable to stop task",
        operation: "task_kill",
        itemId: "task-7"
      }
    ]) {
      messageHandler?.(new MessageEvent("message", {
        data: { schemaVersion: 1, ...data }
      }));
    }

    expect(received).toHaveLength(4);
    expect(received[0]).toMatchObject({
      type: "runtime/dashboard/changed",
      sessionId: "session-42",
      backgroundTasks: [{ taskId: "task-7", kind: "bash" }],
      subagents: [{ subagentId: "subagent-7", status: "running", durationMs: 2000 }]
    });
    expect(received[1]).toEqual({
      type: "runtime/task/killed",
      sessionId: "session-42",
      taskId: "task-7",
      outcome: "killed"
    });
    expect(received[2]).toEqual({
      type: "runtime/subagent/cancelled",
      sessionId: "session-42",
      subagentId: "subagent-7",
      outcome: "already_finished",
      terminalStatus: "completed"
    });
    expect(received[3]).toEqual({
      type: "runtime/dashboard/error",
      sessionId: "session-42",
      message: "Unable to stop task",
      operation: "task_kill",
      itemId: "task-7"
    });
  });

  it("rejects runtime operation errors without the required target id", () => {
    let messageHandler: ((event: MessageEvent<unknown>) => void) | undefined;
    const bridge = createHostBridge({
      postMessage: vi.fn(),
      addEventListener: (_name, handler) => {
        messageHandler = handler;
      }
    });
    const received: HostEvent[] = [];
    bridge.subscribe((event) => received.push(event));

    messageHandler?.(new MessageEvent("message", {
      data: {
        schemaVersion: 1,
        type: "runtime/dashboard/error",
        sessionId: "session-42",
        message: "Unable to stop task",
        operation: "task_kill"
      }
    }));

    expect(received).toEqual([]);
  });

  it("rejects malformed runtime dashboard rows atomically", () => {
    let messageHandler: ((event: MessageEvent<unknown>) => void) | undefined;
    const bridge = createHostBridge({
      postMessage: vi.fn(),
      addEventListener: (_name, handler) => {
        messageHandler = handler;
      }
    });
    const received: HostEvent[] = [];
    bridge.subscribe((event) => received.push(event));
    const validTask = {
      taskId: "task-7",
      command: "dotnet test",
      workingDirectory: "C:\\workspace",
      startedAt: "2026-07-17T08:00:00Z",
      output: "",
      truncated: false,
      completed: false,
      kind: "bash",
      explicitlyKilled: false
    };

    for (const backgroundTasks of [
      [{ ...validTask, taskId: "" }],
      [{ ...validTask, startedAt: "today" }],
      [{ ...validTask, completed: "false" }],
      [{ ...validTask, kind: "future" }]
    ]) {
      messageHandler?.(new MessageEvent("message", {
        data: {
          schemaVersion: 1,
          type: "runtime/dashboard/changed",
          sessionId: "session-42",
          backgroundTasks,
          subagents: []
        }
      }));
    }

    expect(received).toEqual([]);
  });

  it("parses strict session history results", () => {
    let messageHandler: ((event: MessageEvent<unknown>) => void) | undefined;
    const bridge = createHostBridge({
      postMessage: vi.fn(),
      addEventListener: (_name, handler) => {
        messageHandler = handler;
      }
    });
    const received: HostEvent[] = [];
    bridge.subscribe((event) => received.push(event));

    for (const data of [
      {
        type: "session/forked",
        sessionId: "fork-session",
        workspacePath: "C:\\workspace",
        parentSessionId: "session-42",
        chatMessagesCopied: 8,
        updatesCopied: 20,
        planStateCopied: true,
        modelId: "grok-4.5"
      },
      { type: "session/compacted", sessionId: "session-42" },
      {
        type: "session/rewind/points",
        sessionId: "session-42",
        points: [{
          promptIndex: 3,
          createdAt: "2026-07-16T09:30:00Z",
          fileSnapshotCount: 2,
          hasFileChanges: true,
          promptPreview: "Refactor parser"
        }]
      },
      {
        type: "session/rewind/points/error",
        sessionId: "session-42",
        message: "Rewind checkpoints could not be loaded."
      },
      {
        type: "session/rewound",
        sessionId: "session-42",
        success: false,
        targetPromptIndex: 3,
        mode: "all",
        revertedFiles: [],
        cleanFiles: ["src/parser.rs"],
        conflicts: [{ path: "src/parser.rs", conflictType: "content_mismatch" }],
        promptText: "Refactor parser",
        error: "Files changed after the checkpoint"
      }
    ]) {
      messageHandler?.(new MessageEvent("message", {
        data: { schemaVersion: 1, ...data }
      }));
    }

    expect(received).toEqual([
      {
        type: "session/forked",
        sessionId: "fork-session",
        workspacePath: "C:\\workspace",
        parentSessionId: "session-42",
        chatMessagesCopied: 8,
        updatesCopied: 20,
        planStateCopied: true,
        modelId: "grok-4.5"
      },
      { type: "session/compacted", sessionId: "session-42" },
      {
        type: "session/rewind/points",
        sessionId: "session-42",
        points: [{
          promptIndex: 3,
          createdAt: "2026-07-16T09:30:00Z",
          fileSnapshotCount: 2,
          hasFileChanges: true,
          promptPreview: "Refactor parser"
        }]
      },
      {
        type: "session/rewind/points/error",
        sessionId: "session-42",
        message: "Rewind checkpoints could not be loaded."
      },
      {
        type: "session/rewound",
        sessionId: "session-42",
        success: false,
        targetPromptIndex: 3,
        mode: "all",
        revertedFiles: [],
        cleanFiles: ["src/parser.rs"],
        conflicts: [{ path: "src/parser.rs", conflictType: "content_mismatch" }],
        promptText: "Refactor parser",
        error: "Files changed after the checkpoint"
      }
    ]);
  });

  it("rejects malformed session history results atomically", () => {
    let messageHandler: ((event: MessageEvent<unknown>) => void) | undefined;
    const bridge = createHostBridge({
      postMessage: vi.fn(),
      addEventListener: (_name, handler) => {
        messageHandler = handler;
      }
    });
    const received: HostEvent[] = [];
    bridge.subscribe((event) => received.push(event));

    for (const data of [
      {
        type: "session/forked",
        sessionId: "",
        workspacePath: "C:\\workspace",
        parentSessionId: "session-42",
        chatMessagesCopied: 8,
        updatesCopied: 20,
        planStateCopied: true
      },
      { type: "session/compacted", sessionId: 42 },
      {
        type: "session/rewind/points",
        sessionId: "session-42",
        points: [{
          promptIndex: -1,
          createdAt: "2026-07-16T09:30:00Z",
          fileSnapshotCount: 2,
          hasFileChanges: true
        }]
      },
      {
        type: "session/rewound",
        sessionId: "session-42",
        success: true,
        targetPromptIndex: 3,
        mode: "future",
        revertedFiles: [],
        cleanFiles: [],
        conflicts: []
      },
      {
        type: "session/rewound",
        sessionId: "session-42",
        success: false,
        targetPromptIndex: 3,
        mode: "all",
        revertedFiles: [],
        cleanFiles: [],
        conflicts: [{ path: "src/parser.rs" }]
      }
    ]) {
      messageHandler?.(new MessageEvent("message", {
        data: { schemaVersion: 1, ...data }
      }));
    }

    expect(received).toEqual([]);
  });

  it("parses strict session archive changes", () => {
    let messageHandler: ((event: MessageEvent<unknown>) => void) | undefined;
    const bridge = createHostBridge({
      postMessage: vi.fn(),
      addEventListener: (_name, handler) => {
        messageHandler = handler;
      }
    });
    const received: HostEvent[] = [];
    bridge.subscribe((event) => received.push(event));

    for (const data of [
      { requestId: "33333333-3333-4333-8333-333333333333", sessionId: "", archived: true },
      {
        requestId: "33333333-3333-4333-8333-333333333333",
        sessionId: "session-42",
        archived: "true"
      },
      { requestId: "33333333-3333-4333-8333-333333333333", sessionId: "session-42" }
    ]) {
      messageHandler?.(new MessageEvent("message", {
        data: { schemaVersion: 1, type: "session/archive/changed", ...data }
      }));
    }
    messageHandler?.(new MessageEvent("message", {
      data: {
        schemaVersion: 1,
        type: "session/archive/changed",
        requestId: "33333333-3333-4333-8333-333333333333",
        sessionId: "session-42",
        archived: true
      }
    }));

    expect(received).toEqual([{
      type: "session/archive/changed",
      requestId: "33333333-3333-4333-8333-333333333333",
      sessionId: "session-42",
      archived: true
    }]);
  });

  it("preserves the engine epoch on session lifecycle events", () => {
    const { bridge, emit, received } = hostEventHarness();

    emit({
      schemaVersion: 1,
      type: "session/active/changed",
      sessionId: "session-42",
      workspacePath: "C:\\workspace",
      engineEpoch: 7
    });
    emit({
      schemaVersion: 1,
      type: "session/update",
      sessionId: "session-42",
      updateKind: "agent_message_chunk",
      text: "hello",
      engineEpoch: 7
    });

    expect(received).toEqual([
      {
        type: "session/active/changed",
        sessionId: "session-42",
        workspacePath: "C:\\workspace",
        engineEpoch: 7
      },
      {
        type: "session/update",
        sessionId: "session-42",
        updateKind: "agent_message_chunk",
        text: "hello",
        engineEpoch: 7
      }
    ]);
  });

  it("parses a strict paged session list", () => {
    let messageHandler: ((event: MessageEvent<unknown>) => void) | undefined;
    const bridge = createHostBridge({
      postMessage: vi.fn(),
      addEventListener: (_name, handler) => {
        messageHandler = handler;
      }
    });
    const received: HostEvent[] = [];
    bridge.subscribe((event) => received.push(event));
    bridge.send({
      type: "session/list",
      requestId: "11111111-1111-4111-8111-111111111111",
      query: "",
      limit: 50
    });

    messageHandler?.(new MessageEvent("message", {
      data: {
        schemaVersion: 1,
        type: "session/list/changed",
        requestId: "11111111-1111-4111-8111-111111111111",
        sessions: [{
          sessionId: "session-42",
          title: "检查登录流程",
          workspacePath: "C:\\workspace",
          createdAt: "2026-07-16T10:00:00Z",
          updatedAt: "2026-07-17T08:30:00+08:00",
          messageCount: 12,
          modelId: "grok-4.5",
          parentSessionId: "session-parent",
          branch: "feature/login",
          worktreeLabel: "login-flow",
          sourceWorkspacePath: "D:\\source"
        }],
        nextCursor: "page-2"
      }
    }));

    expect(received).toEqual([{
      type: "session/list/changed",
      requestId: "11111111-1111-4111-8111-111111111111",
      sessions: [{
        sessionId: "session-42",
        title: "检查登录流程",
        workspacePath: "C:\\workspace",
        createdAt: "2026-07-16T10:00:00Z",
        updatedAt: "2026-07-17T08:30:00+08:00",
        messageCount: 12,
        modelId: "grok-4.5",
        parentSessionId: "session-parent",
        branch: "feature/login",
        worktreeLabel: "login-flow",
        sourceWorkspacePath: "D:\\source"
      }],
      nextCursor: "page-2"
    }]);
  });

  it("correlates session list failures and rejects malformed or duplicate terminals", () => {
    const { bridge, emit, received } = hostEventHarness();
    const requestId = "11111111-1111-4111-8111-111111111112";
    bridge.send({ type: "session/list", requestId, query: "", limit: 50 });

    emit({
      schemaVersion: 1,
      type: "session/list/error",
      requestId,
      message: ""
    });
    emit({
      schemaVersion: 1,
      type: "session/list/error",
      requestId,
      message: "无法加载会话。"
    });
    emit({
      schemaVersion: 1,
      type: "session/list/error",
      requestId,
      message: "重复终止事件"
    });

    expect(received).toEqual([{
      type: "session/list/error",
      requestId,
      message: "无法加载会话。"
    }]);
  });

  it("keeps session list correlation across unrelated engine errors", () => {
    const { bridge, emit, received } = hostEventHarness();
    const requestId = "11111111-1111-4111-8111-111111111113";
    bridge.send({ type: "session/list", requestId, query: "", limit: 50 });

    emit({
      schemaVersion: 1,
      type: "engine/status",
      status: "error",
      message: "Another engine operation failed."
    });
    emit({
      schemaVersion: 1,
      type: "session/list/changed",
      requestId,
      sessions: []
    });

    expect(received).toEqual([
      {
        type: "engine/status",
        status: "error",
        message: "Another engine operation failed."
      },
      { type: "session/list/changed", requestId, sessions: [] }
    ]);
  });

  it("rejects malformed session lists atomically", () => {
    let messageHandler: ((event: MessageEvent<unknown>) => void) | undefined;
    const bridge = createHostBridge({
      postMessage: vi.fn(),
      addEventListener: (_name, handler) => {
        messageHandler = handler;
      }
    });
    const received: HostEvent[] = [];
    bridge.subscribe((event) => received.push(event));
    const validSession = {
      sessionId: "session-42",
      title: "检查登录流程",
      workspacePath: "C:\\workspace",
      createdAt: "2026-07-16T10:00:00Z",
      updatedAt: "2026-07-17T08:30:00Z",
      messageCount: 12
    };

    for (const data of [
      { sessions: null },
      { sessions: [{ ...validSession, sessionId: "" }] },
      { sessions: [{ ...validSession, title: " " }] },
      { sessions: [{ ...validSession, createdAt: "yesterday" }] },
      { sessions: [{ ...validSession, updatedAt: null }] },
      { sessions: [{ ...validSession, messageCount: -1 }] },
      { sessions: [{ ...validSession, messageCount: 1.5 }] },
      { sessions: [{ ...validSession, modelId: "" }] },
      { requestId: "not-a-request-id", sessions: [validSession] },
      { requestId: "a".repeat(65), sessions: [validSession] },
      { sessions: [validSession], nextCursor: "" },
      { sessions: [validSession], nextCursor: null }
    ]) {
      messageHandler?.(new MessageEvent("message", {
        data: { schemaVersion: 1, type: "session/list/changed", ...data }
      }));
    }

    expect(received).toEqual([]);
  });

  it("parses active session changes and rejects malformed identities", () => {
    let messageHandler: ((event: MessageEvent<unknown>) => void) | undefined;
    const bridge = createHostBridge({
      postMessage: vi.fn(),
      addEventListener: (_name, handler) => {
        messageHandler = handler;
      }
    });
    const received: HostEvent[] = [];
    bridge.subscribe((event) => received.push(event));

    for (const data of [
      { sessionId: "", workspacePath: "C:\\workspace", engineEpoch: 3 },
      { sessionId: "session-42", workspacePath: " ", engineEpoch: 3 },
      { sessionId: 42, workspacePath: "C:\\workspace", engineEpoch: 3 }
    ]) {
      messageHandler?.(new MessageEvent("message", {
        data: { schemaVersion: 1, type: "session/active/changed", ...data }
      }));
    }
    messageHandler?.(new MessageEvent("message", {
      data: {
        schemaVersion: 1,
        type: "session/active/changed",
        sessionId: "session-42",
        workspacePath: "C:\\workspace",
        engineEpoch: 3
      }
    }));

    expect(received).toEqual([{
      type: "session/active/changed",
      sessionId: "session-42",
      workspacePath: "C:\\workspace",
      engineEpoch: 3
    }]);
  });

  it("sends versioned provider settings to WebView2", () => {
    const postMessage = vi.fn();
    const bridge = createHostBridge({ postMessage });

    bridge.send({
      type: "provider/save",
      baseUrl: "http://localhost:8081/v1",
      model: "grok-4.5",
      backend: "chat_completions",
      allowInsecureTransport: true,
      useExistingCredential: false,
      replaceCredential: true
    });

    expect(postMessage).toHaveBeenCalledWith({
      schemaVersion: 1,
      type: "provider/save",
      baseUrl: "http://localhost:8081/v1",
      model: "grok-4.5",
      backend: "chat_completions",
      allowInsecureTransport: true,
      useExistingCredential: false,
      replaceCredential: true
    });
    expect(postMessage.mock.calls[0]?.[0]).not.toHaveProperty("secret");
    expect(postMessage.mock.calls[0]?.[0]).not.toHaveProperty("apiKey");
  });

  it("rejects provider status envelopes containing unknown credential material", () => {
    let messageHandler: ((event: MessageEvent<unknown>) => void) | undefined;
    const bridge = createHostBridge({
      postMessage: vi.fn(),
      addEventListener: (_name, handler) => {
        messageHandler = handler;
      }
    });
    const received: HostEvent[] = [];
    bridge.subscribe((event) => received.push(event));

    messageHandler?.(new MessageEvent("message", {
      data: {
        schemaVersion: 1,
        type: "provider/status",
        status: "loaded",
        baseUrl: "https://example.com/v1",
        model: "grok-4.5",
        backend: "chat_completions",
        allowInsecureTransport: false,
        hasCredential: true,
        message: "Provider loaded",
        secret: "host-must-not-echo-this"
      }
    }));

    expect(received).toEqual([]);
    expect(JSON.stringify(received)).not.toContain("host-must-not-echo-this");
  });

  it("delivers responses provider status without credential material", () => {
    let messageHandler: ((event: MessageEvent<unknown>) => void) | undefined;
    const bridge = createHostBridge({
      postMessage: vi.fn(),
      addEventListener: (_name, handler) => {
        messageHandler = handler;
      }
    });
    const received: HostEvent[] = [];
    bridge.subscribe((event) => received.push(event));

    messageHandler?.(new MessageEvent("message", {
      data: {
        schemaVersion: 1,
        type: "provider/status",
        status: "loaded",
        baseUrl: "https://example.com/v1",
        model: "grok-4.5",
        backend: "responses",
        allowInsecureTransport: false,
        hasCredential: true
      }
    }));

    expect(received).toEqual([{
      type: "provider/status",
      status: "loaded",
      baseUrl: "https://example.com/v1",
      model: "grok-4.5",
      backend: "responses",
      allowInsecureTransport: false,
      hasCredential: true
    }]);
  });

  it("sends only attachment tokens and metadata with schema v1", () => {
    const postMessage = vi.fn();
    const bridge = createHostBridge({ postMessage });
    bridge.send({
      type: "attachment/select",
      requestId: "5f70f2bf-c3ad-4a13-9ca0-61b847f52f0d"
    });
    bridge.send({
      type: "engine/prompt",
      text: "inspect",
      executionProfile: "NativeProtected",
      sessionMode: "default",
      nativeRiskAcknowledged: true,
      workspaceGeneration: 7,
      attachments: [{
        token: "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF",
        name: "pixel.png",
        mimeType: "image/png",
        size: 68
      }]
    });
    bridge.send({ type: "runtime/commands/list", workspaceGeneration: 7 });
    bridge.send({ type: "runtime/memory/flush", sessionId: "session-42" });
    bridge.send({
      type: "ui/preferences/save",
      language: "en-US",
      composerDraft: "continue",
      sessionMode: "plan",
      executionProfile: "WslStrict",
      notificationsEnabled: true,
      windowsAutomationEnabled: true,
      backgroundUpdateChecksEnabled: true,
      fullAccessEnabled: true,
      fontScalePercent: 125
    });

    expect(postMessage).toHaveBeenNthCalledWith(1, {
      schemaVersion: 1,
      type: "attachment/select",
      requestId: "5f70f2bf-c3ad-4a13-9ca0-61b847f52f0d"
    });
    expect(postMessage).toHaveBeenNthCalledWith(2, expect.objectContaining({
      schemaVersion: 1,
      type: "engine/prompt",
      attachments: [expect.objectContaining({
        token: "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF",
        name: "pixel.png",
        size: 68
      })]
    }));
    expect(JSON.stringify(postMessage.mock.calls)).not.toContain("base64Data");
    expect(postMessage).toHaveBeenNthCalledWith(3, {
      schemaVersion: 1,
      type: "runtime/commands/list",
      workspaceGeneration: 7
    });
    expect(postMessage).toHaveBeenNthCalledWith(4, {
      schemaVersion: 1,
      type: "runtime/memory/flush",
      sessionId: "session-42"
    });
    expect(postMessage).toHaveBeenNthCalledWith(5, {
      schemaVersion: 1,
      type: "ui/preferences/save",
      language: "en-US",
      composerDraft: "continue",
      sessionMode: "plan",
      executionProfile: "WslStrict",
      notificationsEnabled: true,
      windowsAutomationEnabled: true,
      backgroundUpdateChecksEnabled: true,
      fullAccessEnabled: true,
      fontScalePercent: 125
    });
  });

  it("parses native attachment metadata without accepting file content", () => {
    let messageHandler: ((event: MessageEvent<unknown>) => void) | undefined;
    const received: HostEvent[] = [];
    const bridge = createHostBridge({
      postMessage: vi.fn(),
      addEventListener: (_name, handler) => { messageHandler = handler; }
    });
    bridge.subscribe((event) => received.push(event));

    messageHandler?.({ data: {
      schemaVersion: 1,
      type: "attachment/changed",
      requestId: "5f70f2bf-c3ad-4a13-9ca0-61b847f52f0d",
      attachments: [{
        token: "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF",
        name: "pixel.png",
        mimeType: "image/png",
        size: 68
      }]
    }} as MessageEvent<unknown>);

    expect(received).toEqual([expect.objectContaining({
      type: "attachment/changed",
      attachments: [expect.objectContaining({ name: "pixel.png", size: 68 })]
    })]);
    expect(JSON.stringify(received)).not.toContain("base64Data");
  });

  it("parses engine capabilities, runtime commands, memory status, and UI preferences", () => {
    let messageHandler: ((event: MessageEvent<unknown>) => void) | undefined;
    const bridge = createHostBridge({
      postMessage: vi.fn(),
      addEventListener: (_name, handler) => { messageHandler = handler; }
    });
    const received: HostEvent[] = [];
    bridge.subscribe((event) => received.push(event));

    for (const data of [
      {
        schemaVersion: 1,
        type: "engine/capabilities",
        sessionId: "session-42",
        imagePrompts: true,
        sessionModes: ["default", "plan"]
      },
      {
        schemaVersion: 1,
        type: "runtime/commands/changed",
        workspaceGeneration: 7,
        commands: [{
          name: "skill",
          description: "Run a repository skill",
          input: { hint: "skill-name" },
          skill: { scope: "repo", path: "C:\\workspace\\skill.md" }
        }]
      },
      {
        schemaVersion: 1,
        type: "runtime/memory/status",
        sessionId: "session-42",
        status: "succeeded"
      },
      {
        schemaVersion: 1,
        type: "ui/preferences/changed",
        language: "en-US",
        composerDraft: "continue",
        sessionMode: "plan",
        executionProfile: "WslStrict",
        notificationsEnabled: true,
        windowsAutomationEnabled: true,
        backgroundUpdateChecksEnabled: true,
        fullAccessEnabled: true,
        fontScalePercent: 125,
        restartRequired: true
      }
    ]) {
      messageHandler?.(new MessageEvent("message", { data }));
    }

    expect(received).toEqual([
      {
        type: "engine/capabilities",
        sessionId: "session-42",
        imagePrompts: true,
        sessionModes: ["default", "plan"]
      },
      {
        type: "runtime/commands/changed",
        workspaceGeneration: 7,
        commands: [{
          name: "skill",
          description: "Run a repository skill",
          input: { hint: "skill-name" },
          skill: { scope: "repo", path: "C:\\workspace\\skill.md" }
        }]
      },
      {
        type: "runtime/memory/status",
        sessionId: "session-42",
        status: "succeeded"
      },
      {
        type: "ui/preferences/changed",
        language: "en-US",
        composerDraft: "continue",
        sessionMode: "plan",
        executionProfile: "WslStrict",
        notificationsEnabled: true,
        windowsAutomationEnabled: true,
        backgroundUpdateChecksEnabled: true,
        fullAccessEnabled: true,
        fontScalePercent: 125,
        restartRequired: true
      }
    ]);
  });

  it("rejects unsupported authoritative font scales", () => {
    const { bridge, emit, received } = hostEventHarness();
    bridge.subscribe(() => undefined);

    emit({
      schemaVersion: 1,
      type: "ui/preferences/changed",
      language: "zh-CN",
      composerDraft: "",
      sessionMode: "default",
      executionProfile: "NativeProtected",
      notificationsEnabled: false,
      windowsAutomationEnabled: false,
      backgroundUpdateChecksEnabled: false,
      fullAccessEnabled: false,
      fontScalePercent: 105,
      restartRequired: false
    });

    expect(received).toHaveLength(0);
  });

  it("correlates Windows Automation terminals and rejects duplicates or echoed values", () => {
    const { bridge, emit, postMessage, received } = hostEventHarness();
    const completedId = "51000000-0000-4000-8000-000000000001";
    const cancelledId = "51000000-0000-4000-8000-000000000002";
    const errorId = "51000000-0000-4000-8000-000000000003";
    bridge.send({
      type: "windows/automation/execute",
      requestId: completedId,
      action: "focus-window",
      processId: 100
    });
    bridge.send({
      type: "windows/automation/execute",
      requestId: cancelledId,
      action: "invoke",
      processId: 200,
      automationId: "RunButton"
    });
    bridge.send({
      type: "windows/automation/execute",
      requestId: errorId,
      action: "set-value",
      processId: 300,
      name: "Search",
      value: "must-not-return"
    });

    expect(postMessage).toHaveBeenNthCalledWith(3, expect.objectContaining({
      schemaVersion: 1,
      type: "windows/automation/execute",
      value: "must-not-return"
    }));
    emit({
      schemaVersion: 1,
      type: "windows/automation/completed",
      requestId: completedId,
      action: "focus-window",
      processId: 999,
      target: "Window"
    });
    emit({
      schemaVersion: 1,
      type: "windows/automation/completed",
      requestId: completedId,
      action: "focus-window",
      processId: 100,
      target: "Window",
      value: "must-not-return"
    });
    emit({
      schemaVersion: 1,
      type: "windows/automation/completed",
      requestId: completedId,
      action: "focus-window",
      processId: 100,
      target: "Window"
    });
    emit({
      schemaVersion: 1,
      type: "windows/automation/completed",
      requestId: completedId,
      action: "focus-window",
      processId: 100,
      target: "Window"
    });
    emit({
      schemaVersion: 1,
      type: "windows/automation/cancelled",
      requestId: cancelledId
    });
    emit({
      schemaVersion: 1,
      type: "windows/automation/error",
      requestId: errorId,
      reason: "failed"
    });
    emit({
      schemaVersion: 1,
      type: "windows/automation/error",
      requestId: errorId,
      reason: "failed"
    });

    expect(received).toEqual([
      {
        type: "windows/automation/completed",
        requestId: completedId,
        action: "focus-window",
        processId: 100,
        target: "Window"
      },
      { type: "windows/automation/cancelled", requestId: cancelledId },
      { type: "windows/automation/error", requestId: errorId, reason: "failed" }
    ]);
    expect(JSON.stringify(received)).not.toContain("must-not-return");
  });

  it("delivers provider errors when configuration fields are unavailable", () => {
    let messageHandler: ((event: MessageEvent<unknown>) => void) | undefined;
    const bridge = createHostBridge({
      postMessage: vi.fn(),
      addEventListener: (_name, handler) => {
        messageHandler = handler;
      }
    });
    const received: HostEvent[] = [];
    bridge.subscribe((event) => received.push(event));

    messageHandler?.(new MessageEvent("message", {
      data: {
        schemaVersion: 1,
        type: "provider/status",
        status: "error",
        baseUrl: "",
        model: "",
        backend: "chat_completions",
        allowInsecureTransport: false,
        hasCredential: false,
        message: "Provider settings could not be loaded"
      }
    }));

    expect(received).toEqual([{
      type: "provider/status",
      status: "error",
      baseUrl: "",
      model: "",
      backend: "chat_completions",
      allowInsecureTransport: false,
      hasCredential: false,
      message: "Provider settings could not be loaded"
    }]);
  });

  it("rejects unknown, overlong, and duplicate runtime command data", () => {
    let messageHandler: ((event: MessageEvent<unknown>) => void) | undefined;
    const bridge = createHostBridge({
      postMessage: vi.fn(),
      addEventListener: (_name, handler) => { messageHandler = handler; }
    });
    const received: HostEvent[] = [];
    bridge.subscribe((event) => received.push(event));
    const command = { name: "review", description: "Review changes" };

    for (const data of [
      {
        schemaVersion: 1,
        type: "runtime/commands/changed",
        workspaceGeneration: 1,
        commands: [command],
        unknown: true
      },
      {
        schemaVersion: 1,
        type: "runtime/commands/changed",
        workspaceGeneration: 1,
        commands: [{ name: "x".repeat(257), description: "too long" }]
      },
      {
        schemaVersion: 1,
        type: "runtime/commands/changed",
        workspaceGeneration: 1,
        commands: [command, command]
      }
    ]) {
      messageHandler?.(new MessageEvent("message", { data }));
    }

    expect(received).toEqual([]);
  });

  it("ignores malformed provider statuses", () => {
    let messageHandler: ((event: MessageEvent<unknown>) => void) | undefined;
    const bridge = createHostBridge({
      postMessage: vi.fn(),
      addEventListener: (_name, handler) => {
        messageHandler = handler;
      }
    });
    const received: HostEvent[] = [];
    bridge.subscribe((event) => received.push(event));

    for (const data of [
      { schemaVersion: 1, type: "provider/status", status: "unknown" },
      {
        schemaVersion: 1,
        type: "provider/status",
        status: "loaded",
        baseUrl: "https://example.com/v1",
        model: "grok-4.5",
        backend: "chat_completions",
        allowInsecureTransport: "true",
        hasCredential: true
      },
      {
        schemaVersion: 1,
        type: "provider/status",
        status: "loaded",
        baseUrl: "https://example.com/v1",
        model: "grok-4.5",
        backend: "chat_completions",
        allowInsecureTransport: false,
        hasCredential: "true"
      }
    ]) {
      messageHandler?.(new MessageEvent("message", { data }));
    }

    expect(received).toEqual([]);
  });

  it("delivers supported host events and ignores malformed envelopes", () => {
    let messageHandler: ((event: MessageEvent<unknown>) => void) | undefined;
    const webView = {
      postMessage: vi.fn(),
      addEventListener: vi.fn((_name: "message", handler: (event: MessageEvent<unknown>) => void) => {
        messageHandler = handler;
      }),
      removeEventListener: vi.fn()
    };
    const bridge = createHostBridge(webView);
    const received: HostEvent[] = [];
    const unsubscribe = bridge.subscribe((event) => received.push(event));

    messageHandler?.(new MessageEvent("message", {
      data: { schemaVersion: 2, type: "engine/status", status: "ready" }
    }));
    messageHandler?.(new MessageEvent("message", {
      data: {
        schemaVersion: 1,
        type: "engine/status",
        status: "ready",
        message: "已连接",
        capabilities: {
          executionProfiles: ["NativeProtected", "WslStrict"],
          wslStrictReason: "WSL2 可用"
        }
      }
    }));
    messageHandler?.(new MessageEvent("message", {
      data: { schemaVersion: 1, type: "workspace/selected", path: "D:\\ignored" }
    }));
    messageHandler?.(new MessageEvent("message", {
      data: {
        schemaVersion: 1,
        type: "workspace/selected",
        path: "D:\\workspace",
        workspaceGeneration: 7
      }
    }));

    expect(received).toEqual([
      {
        type: "engine/status",
        status: "ready",
        message: "已连接",
        capabilities: {
          executionProfiles: ["NativeProtected", "WslStrict"],
          wslStrictReason: "WSL2 可用"
        }
      },
      { type: "workspace/selected", path: "D:\\workspace", workspaceGeneration: 7 }
    ]);

    unsubscribe();
    expect(webView.removeEventListener).toHaveBeenCalledWith("message", messageHandler);
  });

  it("drops malformed execution capabilities without dropping engine status", () => {
    let messageHandler: ((event: MessageEvent<unknown>) => void) | undefined;
    const bridge = createHostBridge({
      postMessage: vi.fn(),
      addEventListener: (_name, handler) => {
        messageHandler = handler;
      }
    });
    const received: HostEvent[] = [];
    bridge.subscribe((event) => received.push(event));

    messageHandler?.(new MessageEvent("message", {
      data: {
        schemaVersion: 1,
        type: "engine/status",
        status: "ready",
        capabilities: { executionProfiles: ["NativeProtected", "UnknownProfile"] }
      }
    }));

    expect(received).toEqual([{ type: "engine/status", status: "ready" }]);
  });

  it("parses permission requests and sends selected or cancelled responses", () => {
    let messageHandler: ((event: MessageEvent<unknown>) => void) | undefined;
    const webView = {
      postMessage: vi.fn(),
      addEventListener: vi.fn((_name: "message", handler: (event: MessageEvent<unknown>) => void) => {
        messageHandler = handler;
      }),
      removeEventListener: vi.fn()
    };
    const bridge = createHostBridge(webView);
    const received: HostEvent[] = [];
    bridge.subscribe((event) => received.push(event));

    messageHandler?.(new MessageEvent("message", {
      data: {
        schemaVersion: 1,
        type: "permission/requested",
        requestId: "permission-1",
        sessionId: "session-42",
        toolCallId: "tool-7",
        title: "写入 README.md",
        toolKind: "execute",
        rawInput: { command: "pwsh -File install.ps1", timeoutMs: 30000 },
        options: [
          { optionId: "allow-once", name: "允许一次", kind: "allow_once" },
          { optionId: "reject-once", name: "拒绝", kind: "reject_once" }
        ],
        locations: ["C:\\work\\README.md:12"]
      }
    }));

    expect(received).toEqual([{
      type: "permission/requested",
      requestId: "permission-1",
      sessionId: "session-42",
      toolCallId: "tool-7",
      title: "写入 README.md",
      toolKind: "execute",
      rawInput: { command: "pwsh -File install.ps1", timeoutMs: 30000 },
      options: [
        { optionId: "allow-once", name: "允许一次", kind: "allow_once" },
        { optionId: "reject-once", name: "拒绝", kind: "reject_once" }
      ],
      locations: ["C:\\work\\README.md:12"]
    }]);

    bridge.send({
      type: "permission/respond",
      requestId: "permission-1",
      outcome: "selected",
      optionId: "allow-once"
    });
    bridge.send({
      type: "permission/respond",
      requestId: "permission-2",
      outcome: "cancelled"
    });

    expect(webView.postMessage).toHaveBeenNthCalledWith(1, {
      schemaVersion: 1,
      type: "permission/respond",
      requestId: "permission-1",
      outcome: "selected",
      optionId: "allow-once"
    });
    expect(webView.postMessage).toHaveBeenNthCalledWith(2, {
      schemaVersion: 1,
      type: "permission/respond",
      requestId: "permission-2",
      outcome: "cancelled"
    });
  });

  it("ignores permission requests with unknown option kinds", () => {
    let messageHandler: ((event: MessageEvent<unknown>) => void) | undefined;
    const webView = {
      postMessage: vi.fn(),
      addEventListener: vi.fn((_name: "message", handler: (event: MessageEvent<unknown>) => void) => {
        messageHandler = handler;
      })
    };
    const bridge = createHostBridge(webView);
    const received: HostEvent[] = [];
    bridge.subscribe((event) => received.push(event));

    messageHandler?.(new MessageEvent("message", {
      data: {
        schemaVersion: 1,
        type: "permission/requested",
        requestId: "permission-1",
        sessionId: "session-42",
        toolCallId: "tool-7",
        title: "Future operation",
        options: [
          { optionId: "future", name: "Future", kind: "allow_for_project" }
        ],
        locations: []
      }
    }));

    expect(received).toEqual([]);
  });
});

function hostEventHarness() {
  let messageHandler: ((event: MessageEvent<unknown>) => void) | undefined;
  const postMessage = vi.fn();
  const bridge = createHostBridge({
    postMessage,
    addEventListener: (_name, handler) => {
      messageHandler = handler;
    }
  });
  const received: HostEvent[] = [];
  bridge.subscribe((event) => received.push(event));
  return {
    bridge,
    postMessage,
    received,
    emit(data: unknown) {
      messageHandler?.(new MessageEvent("message", { data }));
    }
  };
}

function hostBridgeWithoutToken() {
  let messageHandler: ((event: MessageEvent<unknown>) => void) | undefined;
  const postMessage = vi.fn();
  const bridge = createUninitializedHostBridge({
    postMessage,
    addEventListener: (_name, handler) => {
      messageHandler = handler;
    }
  });
  bridge.subscribe(() => undefined);
  return {
    bridge,
    postMessage,
    initialize(documentToken: string) {
      messageHandler?.(new MessageEvent("message", {
        data: { schemaVersion: 1, type: "host/document-token", documentToken }
      }));
    }
  };
}

function worktreeLifecycleEvents() {
  const record = worktreeRecord();
  return [
    {
      schemaVersion: 1,
      type: "worktree/created",
      workspaceGeneration: 7,
      status: "creating",
      sessionId: "session-42",
      worktreePath: "C:\\repo\\.worktrees\\parser",
      sourceGitRoot: "C:\\repo",
      commit: "abc123"
    },
    {
      schemaVersion: 1,
      type: "worktree/list/changed",
      workspaceGeneration: 7,
      worktrees: [record]
    },
    {
      schemaVersion: 1,
      type: "worktree/detail",
      workspaceGeneration: 7,
      worktree: record
    },
    {
      schemaVersion: 1,
      type: "worktree/applied",
      workspaceGeneration: 7,
      status: "conflicts",
      files: [worktreeFileChange()],
      conflicts: [worktreeConflict()],
      gitRoot: "C:\\repo"
    },
    {
      schemaVersion: 1,
      type: "worktree/removed",
      workspaceGeneration: 7,
      idOrPath: "worktree-7",
      removed: true,
      resolvedPath: "C:\\repo\\.worktrees\\parser"
    },
    {
      schemaVersion: 1,
      type: "worktree/gc/completed",
      workspaceGeneration: 7,
      deadRemoved: 1,
      expiredRemoved: 2,
      skippedAlive: 3,
      removeFailed: 4
    },
    {
      schemaVersion: 1,
      type: "worktree/error",
      workspaceGeneration: 7,
      message: "Unable to apply worktree.",
      operation: "apply",
      itemId: "worktree-7"
    }
  ] as const;
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
  } as const;
}

function worktreeFileChange() {
  return {
    path: "src/parser.rs",
    oldPath: "src/old-parser.rs",
    changeType: "edit",
    staged: false,
    additions: 4,
    deletions: 2,
    patch: "@@ parser @@",
    patchBytes: 12,
    patchLines: 1,
    oldText: "old parser",
    newText: "new parser"
  } as const;
}

function worktreeConflict() {
  return {
    path: "src/parser.rs",
    changeType: "edit",
    base: "base",
    ours: "ours",
    theirs: "theirs"
  } as const;
}
