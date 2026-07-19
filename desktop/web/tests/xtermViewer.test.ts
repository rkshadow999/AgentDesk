import { beforeEach, describe, expect, it, vi } from "vitest";

const xterm = vi.hoisted(() => ({
  writeCallbacksSynchronously: false,
  instances: [] as Array<{
    options: Record<string, unknown>;
    writes: Array<{ text: string; callback?: () => void }>;
    reset: ReturnType<typeof vi.fn>;
    clear: ReturnType<typeof vi.fn>;
    dispose: ReturnType<typeof vi.fn>;
  }>
}));

vi.mock("@xterm/xterm", () => ({
  Terminal: class {
    readonly writes: Array<{ text: string; callback?: () => void }> = [];
    readonly reset = vi.fn();
    readonly clear = vi.fn();
    readonly dispose = vi.fn();

    constructor(readonly options: Record<string, unknown>) {
      xterm.instances.push(this);
    }

    loadAddon() {}
    open() {}

    write(text: string, callback?: () => void) {
      this.writes.push({ text, callback });
      if (xterm.writeCallbacksSynchronously) {
        callback?.();
      }
    }
  }
}));

vi.mock("@xterm/addon-fit", () => ({
  FitAddon: class {
    fit() {}
  }
}));

import { mountXtermViewer } from "../src/xtermViewer";

describe("xterm viewer", () => {
  let frames: FrameRequestCallback[];

  beforeEach(() => {
    xterm.instances.length = 0;
    xterm.writeCallbacksSynchronously = false;
    frames = [];
    vi.stubGlobal("requestAnimationFrame", vi.fn((callback: FrameRequestCallback) => {
      frames.push(callback);
      return frames.length;
    }));
    vi.stubGlobal("cancelAnimationFrame", vi.fn());
  });

  it("batches raw appends in one frame without resetting the terminal", () => {
    const viewer = mountXtermViewer(document.createElement("div"));
    const terminal = xterm.instances[0];

    expect(viewer.appendText("first line\n")).toBe(true);
    expect(viewer.appendText("second line\n")).toBe(true);
    expect(terminal.writes).toHaveLength(0);

    runNextFrame(frames);

    expect(terminal.writes.map((write) => write.text)).toEqual([
      "first line\nsecond line\n"
    ]);
    expect(terminal.reset).not.toHaveBeenCalled();
    expect(terminal.options.convertEol).toBe(true);
  });

  it("waits for an older asynchronous write before flushing newer output", () => {
    const viewer = mountXtermViewer(document.createElement("div"));
    const terminal = xterm.instances[0];

    expect(viewer.appendText("old\n")).toBe(true);
    runNextFrame(frames);
    expect(viewer.appendText("new\n")).toBe(true);
    runNextFrame(frames);
    expect(terminal.writes.map((write) => write.text)).toEqual(["old\n"]);

    terminal.writes[0].callback?.();
    runNextFrame(frames);

    expect(terminal.writes.map((write) => write.text)).toEqual([
      "old\n",
      "new\n"
    ]);
    expect(terminal.reset).not.toHaveBeenCalled();
  });

  it("continues flushing when xterm invokes write callbacks synchronously", () => {
    xterm.writeCallbacksSynchronously = true;
    const viewer = mountXtermViewer(document.createElement("div"));
    const terminal = xterm.instances[0];

    expect(viewer.appendText("first")).toBe(true);
    runNextFrame(frames);
    expect(viewer.appendText("second")).toBe(true);
    runNextFrame(frames);

    expect(terminal.writes.map((write) => write.text)).toEqual(["first", "second"]);
  });

  it("preserves a CRLF pair split across asynchronous writes", () => {
    const viewer = mountXtermViewer(document.createElement("div"));
    const terminal = xterm.instances[0];

    expect(viewer.appendText("a\r")).toBe(true);
    runNextFrame(frames);
    expect(viewer.appendText("\nb")).toBe(true);
    terminal.writes[0].callback?.();
    runNextFrame(frames);

    expect(terminal.writes.map((write) => write.text)).toEqual(["a\r", "\nb"]);
  });

  it("rejects queued appends beyond the four MiB pending limit", () => {
    const viewer = mountXtermViewer(document.createElement("div"));
    const terminal = xterm.instances[0];
    const pendingLimit = 4 * 1024 * 1024;

    expect(viewer.appendText("in flight")).toBe(true);
    runNextFrame(frames);
    expect(viewer.appendText("x".repeat(pendingLimit))).toBe(true);
    expect(viewer.appendText("overflow")).toBe(false);

    expect(terminal.writes.map((write) => write.text)).toEqual(["in flight"]);
  });

  it("batches one hundred thousand terminal lines without per-line writes", () => {
    const viewer = mountXtermViewer(document.createElement("div"));
    const terminal = xterm.instances[0];
    const transcript = Array.from(
      { length: 100_000 },
      (_, index) => `line ${index.toString().padStart(6, "0")}\n`
    ).join("");

    expect(viewer.appendText(transcript)).toBe(true);
    expect(terminal.writes).toHaveLength(0);
    runNextFrame(frames);

    expect(terminal.writes).toHaveLength(1);
    expect(terminal.writes[0].text).toHaveLength(transcript.length);
    expect(terminal.options.scrollback).toBe(100_000);
  });

  it("collapses replacements to the latest bounded authoritative snapshot", () => {
    const viewer = mountXtermViewer(document.createElement("div"));
    const terminal = xterm.instances[0];
    const pendingLimit = 4 * 1024 * 1024;

    expect(viewer.appendText("in flight")).toBe(true);
    runNextFrame(frames);
    expect(viewer.appendText("stale queued output")).toBe(true);
    viewer.replaceText("old snapshot");
    viewer.replaceText(`discarded-prefix${"x".repeat(pendingLimit)}`);
    expect(viewer.appendText("overflow after replacement")).toBe(false);

    expect(terminal.reset).not.toHaveBeenCalled();
    terminal.writes[0].callback?.();
    runNextFrame(frames);

    expect(terminal.reset).toHaveBeenCalledTimes(1);
    expect(terminal.clear).toHaveBeenCalledTimes(1);
    expect(terminal.writes).toHaveLength(2);
    expect(terminal.writes[1].text).toBe("x".repeat(pendingLimit));
  });

  it("writes appends accepted after a replacement behind the authoritative snapshot", () => {
    const viewer = mountXtermViewer(document.createElement("div"));
    const terminal = xterm.instances[0];

    expect(viewer.appendText("in flight")).toBe(true);
    runNextFrame(frames);
    viewer.replaceText("authoritative snapshot");
    expect(viewer.appendText(" then append")).toBe(true);

    terminal.writes[0].callback?.();
    runNextFrame(frames);
    terminal.writes[1].callback?.();
    runNextFrame(frames);

    expect(terminal.reset).toHaveBeenCalledTimes(1);
    expect(terminal.writes.map((write) => write.text)).toEqual([
      "in flight",
      "authoritative snapshot",
      " then append"
    ]);
  });

  it("does not flush queued output after disposal and a late write callback", () => {
    const viewer = mountXtermViewer(document.createElement("div"));
    const terminal = xterm.instances[0];

    expect(viewer.appendText("in flight\n")).toBe(true);
    runNextFrame(frames);
    expect(viewer.appendText("must be discarded\n")).toBe(true);
    viewer.replaceText("must also be discarded\n");
    viewer.dispose();
    terminal.writes[0].callback?.();
    while (frames.length > 0) {
      runNextFrame(frames);
    }

    expect(terminal.writes.map((write) => write.text)).toEqual(["in flight\n"]);
    expect(terminal.reset).not.toHaveBeenCalled();
    expect(terminal.dispose).toHaveBeenCalledTimes(1);
  });
});

function runNextFrame(frames: FrameRequestCallback[]): void {
  const frame = frames.shift();
  expect(frame).toBeDefined();
  frame?.(0);
}
