import { spawn } from "node:child_process";
import { access, mkdtemp, rm } from "node:fs/promises";
import { createServer } from "node:net";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { pathToFileURL } from "node:url";

export const EXPECTED_TARGET_URLS = Object.freeze({
  workbench: "https://workbench.agentdesk.local/index.html?surface=workbench",
  inspector: "https://inspector.agentdesk.local/index.html?surface=inspector",
});

const REMOTE_DEBUGGING_ARGUMENT = "WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS";
const TEST_MODE_ARGUMENT = "AGENTDESK_WEBVIEW2_TEST_MODE";
const TEST_ROOT_ARGUMENT = "AGENTDESK_WEBVIEW2_TEST_USER_DATA_ROOT";
const PROVIDER_SECRET_ARGUMENT = "GROK_THIRD_PARTY_API_KEY";
const TEST_USER_DATA_PREFIX = "AgentDesk-WebView2-CDP-";
const DEFAULT_TIMEOUT_MS = 60_000;
const TARGET_POLL_INTERVAL_MS = 250;
const SURFACE_POLL_INTERVAL_MS = 100;
const CDP_COMMAND_TIMEOUT_MS = 5_000;
const PROCESS_TERMINATION_TIMEOUT_MS = 5_000;

function assertPort(port) {
  if (!Number.isInteger(port) || port < 1 || port > 65_535) {
    throw new RangeError(`Remote debugging port is invalid: ${port}`);
  }
}

export function appendRemoteDebuggingPort(existing, port) {
  assertPort(port);
  const current = String(existing ?? "")
    // WebView2 accepts Chromium switches separated by whitespace. Remove both
    // forms so a caller's stale fixed port can never win over the test port.
    .replace(/(?:^|\s)--remote-debugging-port(?:=\S+|\s+\S+)?/g, " ")
    .replace(/\s+/g, " ")
    .trim();
  return [current, `--remote-debugging-port=${port}`].filter(Boolean).join(" ");
}

function deleteEnvironmentVariable(environment, name) {
  for (const key of Object.keys(environment)) {
    if (key.toUpperCase() === name.toUpperCase()) {
      delete environment[key];
    }
  }
}

function readEnvironmentVariable(environment, name) {
  return Object.entries(environment)
    .filter(([key, value]) =>
      key.toUpperCase() === name.toUpperCase() && value !== undefined)
    .map(([, value]) => String(value))
    .filter(Boolean)
    .join(" ");
}

export async function withIsolatedWebViewTestEnvironment(
  parentEnvironment,
  port,
  action,
) {
  assertPort(port);
  if (!parentEnvironment || typeof parentEnvironment !== "object") {
    throw new TypeError("A parent environment object is required.");
  }
  if (typeof action !== "function") {
    throw new TypeError("An isolated WebView2 test action is required.");
  }

  // The unique directory and owned child lifecycle provide test isolation.
  // The desktop-side path gate is only a guard against misconfiguration and
  // ordinary reparse points, not a same-account TOCTOU security boundary.
  const userDataRoot = await mkdtemp(join(tmpdir(), TEST_USER_DATA_PREFIX));
  try {
    const childEnvironment = { ...parentEnvironment };
    const inheritedBrowserArguments = readEnvironmentVariable(
      parentEnvironment,
      REMOTE_DEBUGGING_ARGUMENT,
    );
    deleteEnvironmentVariable(childEnvironment, REMOTE_DEBUGGING_ARGUMENT);
    deleteEnvironmentVariable(childEnvironment, TEST_MODE_ARGUMENT);
    deleteEnvironmentVariable(childEnvironment, TEST_ROOT_ARGUMENT);
    deleteEnvironmentVariable(childEnvironment, PROVIDER_SECRET_ARGUMENT);
    childEnvironment[REMOTE_DEBUGGING_ARGUMENT] = appendRemoteDebuggingPort(
      inheritedBrowserArguments,
      port,
    );
    childEnvironment[TEST_MODE_ARGUMENT] = "1";
    childEnvironment[TEST_ROOT_ARGUMENT] = userDataRoot;
    return await action({ childEnvironment, userDataRoot });
  } finally {
    await rm(userDataRoot, {
      recursive: true,
      force: true,
      maxRetries: 5,
      retryDelay: 200,
    });
  }
}

function isSurfaceHost(host) {
  return host === "workbench.agentdesk.local" || host === "inspector.agentdesk.local";
}

function assertSurfaceUrl(url) {
  let parsed;
  try {
    parsed = new URL(url);
  } catch {
    return;
  }
  if (isSurfaceHost(parsed.hostname) && parsed.protocol !== "https:") {
    throw new Error(`AgentDesk surface target must use HTTPS: ${url}`);
  }
}

function validateWebSocketUrl(target, name) {
  if (typeof target.webSocketDebuggerUrl !== "string" || target.webSocketDebuggerUrl.length === 0) {
    return false;
  }
  let parsed;
  try {
    parsed = new URL(target.webSocketDebuggerUrl);
  } catch {
    throw new Error(`The ${name} WebView2 target has an invalid CDP WebSocket URL.`);
  }
  if (parsed.protocol !== "ws:" && parsed.protocol !== "wss:") {
    throw new Error(`The ${name} WebView2 target has a non-WebSocket CDP URL.`);
  }
  if (!["127.0.0.1", "localhost", "::1"].includes(parsed.hostname)) {
    throw new Error(`The ${name} CDP WebSocket URL is not loopback-bound.`);
  }
  return true;
}

/**
 * Returns the two expected targets once both are present, or null while the
 * WebView2 controller is still publishing its targets.
 */
export function selectExpectedTargets(targets) {
  if (!Array.isArray(targets)) {
    throw new Error("The WebView2 /json/list response is not an array.");
  }

  const pages = targets.filter(target => target && target.type === "page");
  for (const page of pages) {
    assertSurfaceUrl(page.url);
  }

  const selected = {};
  for (const [name, expectedUrl] of Object.entries(EXPECTED_TARGET_URLS)) {
    const matches = pages.filter(page => page.url === expectedUrl);
    if (matches.length > 1) {
      throw new Error(`Expected exactly one ${name} WebView2 target, found ${matches.length}.`);
    }
    if (matches.length === 0 || !validateWebSocketUrl(matches[0], name)) {
      return null;
    }
    selected[name] = matches[0];
  }
  return selected;
}

export function evaluateSurfaceSnapshot(snapshot) {
  return snapshot?.readyState === "complete" &&
    Number.isInteger(snapshot.rootChildren) &&
    snapshot.rootChildren > 0 &&
    Array.isArray(snapshot.runtimeErrors) &&
    snapshot.runtimeErrors.length === 0;
}

function delay(milliseconds) {
  return new Promise(resolveDelay => setTimeout(resolveDelay, milliseconds));
}

function reserveLocalPort() {
  return new Promise((resolvePort, reject) => {
    const server = createServer();
    server.once("error", reject);
    server.listen(0, "127.0.0.1", () => {
      const address = server.address();
      const port = typeof address === "object" && address !== null ? address.port : 0;
      server.close(error => {
        if (error) {
          reject(error);
          return;
        }
        assertPort(port);
        resolvePort(port);
      });
    });
  });
}

async function fetchTargetList(port) {
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), 2_000);
  try {
    const response = await fetch(`http://127.0.0.1:${port}/json/list`, {
      signal: controller.signal,
    });
    if (!response.ok) {
      throw new Error(`CDP endpoint returned HTTP ${response.status}.`);
    }
    return await response.json();
  } finally {
    clearTimeout(timeout);
  }
}

function safeError(message) {
  return message instanceof Error ? message : new Error(String(message));
}

class CdpSession {
  constructor(target, name) {
    this.target = target;
    this.name = name;
    this.socket = null;
    this.nextCommandId = 1;
    this.pending = new Map();
    this.runtimeErrors = [];
    this.openPromise = null;
    this.closePromise = null;
  }

  async connect() {
    if (typeof WebSocket !== "function") {
      throw new Error("Node.js WebSocket support is required for the WebView2 CDP smoke test.");
    }
    this.socket = new WebSocket(this.target.webSocketDebuggerUrl);
    this.openPromise = new Promise((resolveOpen, rejectOpen) => {
      let settled = false;
      const resolveOnce = () => {
        if (!settled) {
          settled = true;
          resolveOpen();
        }
      };
      const rejectOnce = error => {
        if (!settled) {
          settled = true;
          rejectOpen(safeError(error));
        }
      };
      this.socket.onopen = resolveOnce;
      this.socket.onerror = () => rejectOnce(
        new Error(`Unable to connect to the ${this.name} CDP target.`),
      );
      this.socket.onclose = () => {
        const error = new Error(`The ${this.name} CDP target closed unexpectedly.`);
        rejectOnce(error);
        this.rejectPending(error);
      };
      this.socket.onmessage = event => {
        void this.handleMessage(event);
      };
    });
    await this.openPromise;
  }

  async handleMessage(event) {
    try {
      let raw = event.data;
      if (typeof raw !== "string") {
        if (raw instanceof ArrayBuffer) {
          raw = Buffer.from(raw).toString("utf8");
        } else if (ArrayBuffer.isView(raw)) {
          raw = Buffer.from(raw.buffer, raw.byteOffset, raw.byteLength).toString("utf8");
        } else if (raw && typeof raw.text === "function") {
          raw = await raw.text();
        }
      }
      if (typeof raw !== "string") {
        return;
      }
      const message = JSON.parse(raw);
      if (typeof message.id === "number") {
        const pending = this.pending.get(message.id);
        if (!pending) {
          return;
        }
        this.pending.delete(message.id);
        clearTimeout(pending.timeout);
        if (message.error) {
          pending.reject(new Error(`CDP ${pending.method} failed.`));
        } else {
          pending.resolve(message);
        }
        return;
      }

      if (message.method === "Runtime.exceptionThrown") {
        this.recordRuntimeError("Runtime.exceptionThrown");
      } else if (
        message.method === "Runtime.consoleAPICalled" &&
        ["error", "warning"].includes(message.params?.type)
      ) {
        this.recordRuntimeError(`Runtime.consoleAPICalled:${message.params.type}`);
      } else if (
        message.method === "Log.entryAdded" &&
        ["error", "warning"].includes(message.params?.entry?.level)
      ) {
        this.recordRuntimeError(`Log.entryAdded:${message.params.entry.level}`);
      }
    } catch {
      // Ignore malformed asynchronous messages; command responses remain authoritative.
    }
  }

  recordRuntimeError(kind) {
    if (!this.runtimeErrors.includes(kind)) {
      this.runtimeErrors.push(kind);
    }
  }

  rejectPending(error) {
    for (const [id, pending] of this.pending) {
      this.pending.delete(id);
      clearTimeout(pending.timeout);
      pending.reject(error);
    }
  }

  sendCommand(method, params = {}) {
    if (!this.socket || this.socket.readyState !== 1) {
      return Promise.reject(new Error(`The ${this.name} CDP target is not connected.`));
    }
    const id = this.nextCommandId++;
    return new Promise((resolveCommand, rejectCommand) => {
      const timeout = setTimeout(() => {
        this.pending.delete(id);
        rejectCommand(new Error(`CDP ${method} timed out for ${this.name}.`));
      }, CDP_COMMAND_TIMEOUT_MS);
      this.pending.set(id, {
        method,
        timeout,
        resolve: resolveCommand,
        reject: rejectCommand,
      });
      try {
        this.socket.send(JSON.stringify({ id, method, params }));
      } catch (error) {
        this.pending.delete(id);
        clearTimeout(timeout);
        rejectCommand(safeError(error));
      }
    });
  }

  async readSnapshot() {
    const response = await this.sendCommand("Runtime.evaluate", {
      expression: `(() => ({
        readyState: document.readyState,
        rootChildren: document.querySelector("#root")?.children.length ?? 0,
      }))()`,
      awaitPromise: true,
      returnByValue: true,
    });
    if (response.result?.exceptionDetails) {
      this.recordRuntimeError("Runtime.evaluate.exception");
      return { readyState: "unknown", rootChildren: 0, runtimeErrors: this.runtimeErrors };
    }
    const value = response.result?.result?.value;
    return {
      readyState: value?.readyState,
      rootChildren: Number(value?.rootChildren ?? 0),
      runtimeErrors: [...this.runtimeErrors],
    };
  }

  async close() {
    if (!this.socket || this.socket.readyState === 3) {
      return;
    }
    this.closePromise ??= new Promise(resolveClose => {
      const finish = () => resolveClose();
      this.socket.addEventListener("close", finish, { once: true });
      try {
        this.socket.close();
      } catch {
        finish();
      }
      setTimeout(finish, 1_000);
    });
    await this.closePromise;
  }
}

async function inspectTarget(target, name, deadline) {
  const session = new CdpSession(target, name);
  try {
    await session.connect();
    await session.sendCommand("Runtime.enable");
    await session.sendCommand("Log.enable");
    while (Date.now() < deadline) {
      if (session.runtimeErrors.length > 0) {
        throw new Error(`${name} emitted a runtime or console error/warning.`);
      }
      const snapshot = await session.readSnapshot();
      if (evaluateSurfaceSnapshot(snapshot)) {
        // Allow queued CDP events to arrive before declaring the target clean.
        await delay(50);
        if (session.runtimeErrors.length === 0) {
          return;
        }
        throw new Error(`${name} emitted a runtime or console error/warning.`);
      }
      await delay(SURFACE_POLL_INTERVAL_MS);
    }
    throw new Error(`${name} did not reach readyState=complete with a populated #root.`);
  } finally {
    await session.close();
  }
}

async function inspectExpectedTargets(selected, deadline) {
  await Promise.all([
    inspectTarget(selected.workbench, "workbench", deadline),
    inspectTarget(selected.inspector, "inspector", deadline),
  ]);
}

function waitForChildExit(child) {
  return new Promise(resolveExit => {
    if (child.exitCode !== null || child.signalCode !== null) {
      resolveExit({ code: child.exitCode, signal: child.signalCode, error: null });
      return;
    }
    let settled = false;
    const finish = state => {
      if (settled) {
        return;
      }
      settled = true;
      resolveExit(state);
    };
    child.once("error", error => finish({
      code: child.exitCode,
      signal: child.signalCode,
      error: safeError(error),
    }));
    child.once("exit", (code, signal) => finish({ code, signal, error: null }));
  });
}

function waitForPromiseWithTimeout(promise, timeoutMs) {
  return new Promise(resolveWait => {
    let settled = false;
    const finish = result => {
      if (settled) {
        return;
      }
      settled = true;
      clearTimeout(timeout);
      resolveWait(result);
    };
    const timeout = setTimeout(() => finish({ settled: false }), timeoutMs);
    Promise.resolve(promise).then(
      value => finish({ settled: true, value }),
      error => finish({ settled: true, error: safeError(error) }),
    );
  });
}

function isChildRunning(child) {
  return child && child.exitCode === null && child.signalCode === null;
}

export function buildJobLauncherInvocation(
  executablePath,
  launcherPath,
  childEnvironment,
) {
  const resolvedExecutablePath = resolve(executablePath);
  const resolvedLauncherPath = resolve(launcherPath);
  return {
    command: resolvedLauncherPath,
    args: [
      "--working-directory",
      dirname(resolvedExecutablePath),
      "--",
      resolvedExecutablePath,
    ],
    options: {
      cwd: dirname(resolvedLauncherPath),
      env: childEnvironment,
      windowsHide: true,
      stdio: ["pipe", "ignore", "ignore"],
    },
  };
}

export async function terminateJobLauncher(
  launcher,
  exitPromise,
  {
    terminationTimeoutMs = PROCESS_TERMINATION_TIMEOUT_MS,
  } = {},
) {
  if (!launcher) {
    return;
  }
  if (!Number.isInteger(terminationTimeoutMs) || terminationTimeoutMs < 1) {
    throw new RangeError("Process termination timeout must be a positive integer.");
  }

  if (
    isChildRunning(launcher) &&
    launcher.stdin &&
    !launcher.stdin.destroyed &&
    !launcher.stdin.writableEnded
  ) {
    try {
      // Closing the ownership pipe lets the launcher dispose its Job Object.
      // The same EOF is delivered automatically if the Node process exits.
      launcher.stdin.end();
    } catch {
      // Fall through to the bounded process-handle termination below.
    }
  }

  const gracefulWait = await waitForPromiseWithTimeout(
    exitPromise,
    terminationTimeoutMs,
  );
  if (!gracefulWait.settled && isChildRunning(launcher)) {
    let killResult;
    let killError;
    try {
      killResult = launcher.kill("SIGKILL");
    } catch (error) {
      killError = safeError(error);
    }
    const forcedWait = await waitForPromiseWithTimeout(
      exitPromise,
      terminationTimeoutMs,
    );
    if (forcedWait.settled) {
      return;
    }
    if (killResult === false || killError) {
      throw new Error("AgentDesk process launcher could not be terminated.", {
        cause: killError,
      });
    }
    throw new Error("AgentDesk process launcher did not exit after forced termination.");
  }
}

function createChildExitError(exitState) {
  if (exitState.error) {
    const error = new Error("Unable to start AgentDesk.App.exe.", {
      cause: exitState.error,
    });
    if (typeof exitState.error.code === "string") {
      error.code = exitState.error.code;
    }
    return error;
  }
  return new Error(
    `AgentDesk.App.exe exited before WebView2 targets were ready (code ${exitState.code ?? "none"}).`,
  );
}

function parseArguments(argv) {
  let executablePath;
  let launcherPath;
  let timeoutMs = DEFAULT_TIMEOUT_MS;
  for (let index = 0; index < argv.length; index += 1) {
    const argument = argv[index];
    if (["--executable", "--executable-path", "-ExecutablePath"].includes(argument)) {
      executablePath = argv[++index];
    } else if (["--launcher", "--launcher-path", "-LauncherPath"].includes(argument)) {
      launcherPath = argv[++index];
    } else if (["--timeout-ms", "-TimeoutMs"].includes(argument)) {
      timeoutMs = Number(argv[++index]);
    } else if (argument === "--help" || argument === "-h") {
      return { help: true };
    } else if (!executablePath && !argument.startsWith("-")) {
      executablePath = argument;
    } else {
      throw new Error(`Unknown argument: ${argument}`);
    }
  }
  if (!executablePath) {
    throw new Error("Specify the portable AgentDesk.App.exe path with --executable.");
  }
  if (!launcherPath) {
    throw new Error("Specify AgentDesk.ProcessJobLauncher.exe with --launcher.");
  }
  if (!Number.isInteger(timeoutMs) || timeoutMs < 5_000) {
    throw new Error("--timeout-ms must be an integer of at least 5000 milliseconds.");
  }
  return { executablePath, launcherPath, timeoutMs };
}

async function assertLaunchFileExists(path, description) {
  try {
    await access(path);
  } catch (cause) {
    const error = new Error(`${description} is unavailable.`, { cause });
    if (typeof cause?.code === "string") {
      error.code = cause.code;
    }
    throw error;
  }
}

export async function runWebViewCdpSmoke(
  executablePath,
  { launcherPath, timeoutMs = DEFAULT_TIMEOUT_MS } = {},
) {
  const resolvedExecutablePath = resolve(executablePath);
  await assertLaunchFileExists(resolvedExecutablePath, "AgentDesk.App.exe");
  if (!launcherPath) {
    throw new Error("AgentDesk.ProcessJobLauncher.exe is required.");
  }
  const resolvedLauncherPath = resolve(launcherPath);
  await assertLaunchFileExists(
    resolvedLauncherPath,
    "AgentDesk.ProcessJobLauncher.exe",
  );
  const port = await reserveLocalPort();
  return await withIsolatedWebViewTestEnvironment(
    process.env,
    port,
    async ({ childEnvironment }) => {
      let child;
      let exitPromise = Promise.resolve({ code: null, signal: null });
      const deadline = Date.now() + timeoutMs;

      try {
        const invocation = buildJobLauncherInvocation(
          resolvedExecutablePath,
          resolvedLauncherPath,
          childEnvironment,
        );
        child = spawn(invocation.command, invocation.args, invocation.options);
        exitPromise = waitForChildExit(child);
        let childExitState;
        void exitPromise.then(state => {
          childExitState = state;
        });
        let lastEndpointError;
        while (Date.now() < deadline) {
          if (childExitState) {
            throw createChildExitError(childExitState);
          }

          const nextState = await Promise.race([
            exitPromise.then(exitState => ({ type: "child-exit", exitState })),
            fetchTargetList(port).then(
              targets => ({ type: "targets", targets }),
              error => ({ type: "endpoint-error", error: safeError(error) }),
            ),
          ]);
          if (nextState.type === "child-exit") {
            throw createChildExitError(nextState.exitState);
          }

          try {
            if (nextState.type === "endpoint-error") {
              throw nextState.error;
            }
            const targets = nextState.targets;
            const selected = selectExpectedTargets(targets);
            if (selected) {
              await inspectExpectedTargets(selected, deadline);
              return { port, targets: selected };
            }
            lastEndpointError = undefined;
          } catch (error) {
            if (error instanceof Error && /HTTPS|exactly one|loopback|non-WebSocket|invalid CDP/.test(error.message)) {
              throw error;
            }
            lastEndpointError = error;
          }
          await delay(TARGET_POLL_INTERVAL_MS);
        }
        const suffix = lastEndpointError ? " The CDP endpoint never exposed both expected targets." : "";
        throw new Error(`Timed out waiting for AgentDesk WebView2 CDP targets.${suffix}`);
      } finally {
        await terminateJobLauncher(child, exitPromise);
      }
    },
  );
}

function printHelp() {
  process.stdout.write(
    "Usage: node scripts/agentdesk/Test-AgentDeskWebViewCdp.mjs --launcher <AgentDesk.ProcessJobLauncher.exe> --executable <AgentDesk.App.exe> [--timeout-ms <ms>]\n",
  );
}

const invokedPath = process.argv[1] ? pathToFileURL(resolve(process.argv[1])).href : null;
if (invokedPath === import.meta.url) {
  try {
    const options = parseArguments(process.argv.slice(2));
    if (options.help) {
      printHelp();
    } else {
      await runWebViewCdpSmoke(options.executablePath, options);
      process.stdout.write("AgentDesk WebView2 CDP smoke passed.\n");
    }
  } catch (error) {
    process.stderr.write(`AgentDesk WebView2 CDP smoke failed: ${safeError(error).message}\n`);
    process.exitCode = 1;
  }
}
