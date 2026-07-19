import test from "node:test";
import assert from "node:assert/strict";
import { randomUUID } from "node:crypto";
import { EventEmitter } from "node:events";
import { access, lstat, readdir, writeFile } from "node:fs/promises";
import { isAbsolute, join, relative } from "node:path";
import { tmpdir } from "node:os";

import * as cdpSmoke from "./Test-AgentDeskWebViewCdp.mjs";

const {
  EXPECTED_TARGET_URLS,
  appendRemoteDebuggingPort,
  evaluateSurfaceSnapshot,
  selectExpectedTargets,
} = cdpSmoke;

const TEST_MODE_VARIABLE = "AGENTDESK_WEBVIEW2_TEST_MODE";
const TEST_ROOT_VARIABLE = "AGENTDESK_WEBVIEW2_TEST_USER_DATA_ROOT";
const PROVIDER_SECRET_VARIABLE = "GROK_THIRD_PARTY_API_KEY";
const TEST_ROOT_PREFIX = "AgentDesk-WebView2-CDP-";

class FakeChildProcess extends EventEmitter {
  constructor({
    pid,
    exitCode = null,
    signalCode = null,
    killResult = true,
    killError,
    onKill,
  } = {}) {
    super();
    this.pid = pid;
    this.exitCode = exitCode;
    this.signalCode = signalCode;
    this.killResult = killResult;
    this.killError = killError;
    this.onKill = onKill;
    this.killSignals = [];
  }

  kill(signal) {
    this.killSignals.push(signal);
    this.onKill?.(signal);
    if (this.killError) {
      throw this.killError;
    }
    return this.killResult;
  }
}

async function listIsolatedTestRoots() {
  const entries = await readdir(tmpdir(), { withFileTypes: true });
  return entries
    .filter(entry => entry.isDirectory() && entry.name.startsWith(TEST_ROOT_PREFIX))
    .map(entry => entry.name)
    .sort();
}

test("replaces an inherited remote debugging switch without leaking another port", () => {
  assert.equal(
    appendRemoteDebuggingPort("--disable-gpu --remote-debugging-port=9227", 43123),
    "--disable-gpu --remote-debugging-port=43123",
  );
});

test("selects exactly one HTTPS workbench and inspector target", () => {
  const targets = [
    {
      type: "page",
      url: EXPECTED_TARGET_URLS.workbench,
      webSocketDebuggerUrl: "ws://127.0.0.1:43123/devtools/page/workbench",
    },
    {
      type: "page",
      url: EXPECTED_TARGET_URLS.inspector,
      webSocketDebuggerUrl: "ws://127.0.0.1:43123/devtools/page/inspector",
    },
    { type: "other", url: "devtools://devtools" },
  ];

  const selected = selectExpectedTargets(targets);
  assert.equal(selected.workbench.url, EXPECTED_TARGET_URLS.workbench);
  assert.equal(selected.inspector.url, EXPECTED_TARGET_URLS.inspector);
});

test("rejects duplicate or non-HTTPS surface targets", () => {
  const duplicate = [
    {
      type: "page",
      url: EXPECTED_TARGET_URLS.workbench,
      webSocketDebuggerUrl: "ws://127.0.0.1:43123/devtools/page/a",
    },
    {
      type: "page",
      url: EXPECTED_TARGET_URLS.workbench,
      webSocketDebuggerUrl: "ws://127.0.0.1:43123/devtools/page/b",
    },
    {
      type: "page",
      url: EXPECTED_TARGET_URLS.inspector,
      webSocketDebuggerUrl: "ws://127.0.0.1:43123/devtools/page/inspector",
    },
  ];

  assert.throws(() => selectExpectedTargets(duplicate), /exactly one/i);
  assert.throws(
    () =>
      selectExpectedTargets([
        ...duplicate.slice(1),
        {
          type: "page",
          url: EXPECTED_TARGET_URLS.workbench.replace("https://", "http://"),
          webSocketDebuggerUrl: "ws://127.0.0.1:43123/devtools/page/workbench",
        },
      ]),
    /HTTPS/i,
  );
});

test("accepts a loaded root and rejects incomplete or errored runtime snapshots", () => {
  assert.equal(
    evaluateSurfaceSnapshot({
      readyState: "complete",
      rootChildren: 1,
      runtimeErrors: [],
    }),
    true,
  );
  assert.equal(
    evaluateSurfaceSnapshot({
      readyState: "loading",
      rootChildren: 1,
      runtimeErrors: [],
    }),
    false,
  );
  assert.equal(
    evaluateSurfaceSnapshot({
      readyState: "complete",
      rootChildren: 1,
      runtimeErrors: ["Log.entryAdded:warning"],
    }),
    false,
  );
});

test("creates a distinct existing non-link temporary user-data root per run", async () => {
  const observedRoots = [];

  await Promise.all([
    cdpSmoke.withIsolatedWebViewTestEnvironment({}, 43123, async ({ userDataRoot }) => {
      observedRoots.push(userDataRoot);
      const stats = await lstat(userDataRoot);
      assert.equal(stats.isDirectory(), true);
      assert.equal(stats.isSymbolicLink(), false);
      assert.equal(isAbsolute(userDataRoot), true);
      assert.equal(relative(tmpdir(), userDataRoot).startsWith(".."), false);
    }),
    cdpSmoke.withIsolatedWebViewTestEnvironment({}, 43124, async ({ userDataRoot }) => {
      observedRoots.push(userDataRoot);
      const stats = await lstat(userDataRoot);
      assert.equal(stats.isDirectory(), true);
      assert.equal(stats.isSymbolicLink(), false);
      assert.equal(isAbsolute(userDataRoot), true);
      assert.equal(relative(tmpdir(), userDataRoot).startsWith(".."), false);
    }),
  ]);

  assert.equal(observedRoots.length, 2);
  assert.notEqual(observedRoots[0], observedRoots[1]);
});

test("builds a child-only test environment and removes inherited provider credentials", async () => {
  const parentEnvironment = {
    WebView2_Additional_Browser_Arguments: "--disable-gpu --remote-debugging-port=9227",
    [PROVIDER_SECRET_VARIABLE]: "dummy-test-secret",
    grok_third_party_api_key: "dummy-duplicate-test-secret",
    AGENTDESK_PARENT_SENTINEL: "unchanged",
  };
  const parentSnapshot = { ...parentEnvironment };

  await cdpSmoke.withIsolatedWebViewTestEnvironment(
    parentEnvironment,
    43123,
    async ({ childEnvironment, userDataRoot }) => {
      assert.equal(childEnvironment[TEST_MODE_VARIABLE], "1");
      assert.equal(childEnvironment[TEST_ROOT_VARIABLE], userDataRoot);
      assert.equal(childEnvironment[PROVIDER_SECRET_VARIABLE], undefined);
      assert.equal(childEnvironment.grok_third_party_api_key, undefined);
      assert.deepEqual(
        Object.keys(childEnvironment).filter(
          key => key.toUpperCase() === PROVIDER_SECRET_VARIABLE,
        ),
        [],
      );
      assert.equal(childEnvironment.AGENTDESK_PARENT_SENTINEL, "unchanged");
      assert.match(
        childEnvironment.WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS,
        /(?:^|\s)--disable-gpu(?:\s|$)/,
      );
      assert.match(
        childEnvironment.WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS,
        /--remote-debugging-port=43123(?:\s|$)/,
      );
      assert.doesNotMatch(
        childEnvironment.WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS,
        /--remote-debugging-port=9227(?:\s|$)/,
      );
    },
  );

  assert.deepEqual(parentEnvironment, parentSnapshot);
});

test("cleans the temporary user-data root after success and failure", async () => {
  let successfulRoot;
  await cdpSmoke.withIsolatedWebViewTestEnvironment({}, 43123, async ({ userDataRoot }) => {
    successfulRoot = userDataRoot;
    await writeFile(join(userDataRoot, "marker.txt"), "cleanup");
  });
  await assert.rejects(access(successfulRoot));

  let failedRoot;
  await assert.rejects(
    cdpSmoke.withIsolatedWebViewTestEnvironment({}, 43124, async ({ userDataRoot }) => {
      failedRoot = userDataRoot;
      await writeFile(join(userDataRoot, "marker.txt"), "cleanup");
      throw new Error("expected smoke failure");
    }),
    /expected smoke failure/,
  );
  await assert.rejects(access(failedRoot));
});

test("reports a real missing executable spawn failure and removes its temporary root", async () => {
  const rootsBefore = await listIsolatedTestRoots();
  const missingExecutable = join(
    tmpdir(),
    `AgentDesk-missing-${randomUUID()}.exe`,
  );
  const startedAt = Date.now();

  await assert.rejects(
    cdpSmoke.runWebViewCdpSmoke(missingExecutable, { timeoutMs: 5_000 }),
    error => error?.code === "ENOENT" || /start|ENOENT/i.test(error?.message ?? ""),
  );

  assert.ok(Date.now() - startedAt < 4_000, "spawn failure should not wait for CDP timeout");
  assert.deepEqual(await listIsolatedTestRoots(), rootsBefore);
});

test("builds an argument-safe launch through the Job Object harness", () => {
  const executablePath = join(tmpdir(), "AgentDesk portable", "AgentDesk.App.exe");
  const launcherPath = join(tmpdir(), "AgentDesk tools", "AgentDesk.ProcessJobLauncher.exe");
  const childEnvironment = { AGENTDESK_TEST_SENTINEL: "preserved" };

  const invocation = cdpSmoke.buildJobLauncherInvocation(
    executablePath,
    launcherPath,
    childEnvironment,
  );

  assert.equal(invocation.command, launcherPath);
  assert.deepEqual(invocation.args, [
    "--working-directory",
    join(tmpdir(), "AgentDesk portable"),
    "--",
    executablePath,
  ]);
  assert.equal(invocation.options.cwd, join(tmpdir(), "AgentDesk tools"));
  assert.equal(invocation.options.env, childEnvironment);
  assert.equal(invocation.options.windowsHide, true);
  assert.deepEqual(invocation.options.stdio, ["pipe", "ignore", "ignore"]);
});

test("closes the Job Object launcher without PID-tree commands", async () => {
  const launcher = new FakeChildProcess({ pid: 4343 });
  let ownershipPipeCloseCount = 0;
  launcher.stdin = {
    destroyed: false,
    writableEnded: false,
    end() {
      ownershipPipeCloseCount += 1;
      this.writableEnded = true;
      queueMicrotask(() => {
        launcher.exitCode = 130;
        launcher.emit("exit", 130, null);
      });
    },
  };
  const exitPromise = new Promise(resolve => {
    launcher.once("exit", (code, signal) => resolve({ code, signal, error: null }));
  });
  const startedAt = Date.now();

  await cdpSmoke.terminateJobLauncher(launcher, exitPromise, {
    terminationTimeoutMs: 20,
  });

  assert.ok(Date.now() - startedAt < 500, "process cleanup must remain bounded");
  assert.equal(ownershipPipeCloseCount, 1);
  assert.deepEqual(launcher.killSignals, []);
});

test("rejects when a force-killed launcher never reports exit", async () => {
  const launcher = new FakeChildProcess({ pid: 4444 });
  let ownershipPipeCloseCount = 0;
  launcher.stdin = {
    destroyed: false,
    writableEnded: false,
    end() {
      ownershipPipeCloseCount += 1;
      this.writableEnded = true;
    },
  };
  const neverExits = new Promise(() => {});

  await assert.rejects(
    cdpSmoke.terminateJobLauncher(launcher, neverExits, {
      terminationTimeoutMs: 20,
    }),
    /did not exit/i,
  );

  assert.equal(ownershipPipeCloseCount, 1);
  assert.deepEqual(launcher.killSignals, ["SIGKILL"]);
});

test("rejects when force-killing a live launcher returns false", async () => {
  const launcher = new FakeChildProcess({ pid: 4545, killResult: false });
  launcher.stdin = {
    destroyed: false,
    writableEnded: false,
    end() {
      this.writableEnded = true;
    },
  };

  await assert.rejects(
    cdpSmoke.terminateJobLauncher(launcher, new Promise(() => {}), {
      terminationTimeoutMs: 20,
    }),
    /could not be terminated/i,
  );
  assert.deepEqual(launcher.killSignals, ["SIGKILL"]);
});

test("rejects when force-killing a live launcher throws", async () => {
  const launcher = new FakeChildProcess({
    pid: 4646,
    killError: new Error("access denied"),
  });
  launcher.stdin = {
    destroyed: false,
    writableEnded: false,
    end() {
      this.writableEnded = true;
    },
  };

  await assert.rejects(
    cdpSmoke.terminateJobLauncher(launcher, new Promise(() => {}), {
      terminationTimeoutMs: 20,
    }),
    /could not be terminated/i,
  );
  assert.deepEqual(launcher.killSignals, ["SIGKILL"]);
});

for (const failure of ["false", "throw"]) {
  test(`accepts an exit race when force-killing the launcher returns ${failure}`, async () => {
    let launcher;
    launcher = new FakeChildProcess({
      pid: failure === "false" ? 4747 : 4848,
      killResult: failure !== "false",
      killError: failure === "throw" ? new Error("already exited") : undefined,
      onKill() {
        launcher.exitCode = 0;
        queueMicrotask(() => launcher.emit("exit", 0, null));
      },
    });
    launcher.stdin = {
      destroyed: false,
      writableEnded: false,
      end() {
        this.writableEnded = true;
      },
    };
    const exitPromise = new Promise(resolve => {
      launcher.once("exit", (code, signal) => resolve({ code, signal, error: null }));
    });

    await cdpSmoke.terminateJobLauncher(launcher, exitPromise, {
      terminationTimeoutMs: 20,
    });
    assert.deepEqual(launcher.killSignals, ["SIGKILL"]);
  });
}
