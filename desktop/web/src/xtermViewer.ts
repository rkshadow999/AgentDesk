import { FitAddon } from "@xterm/addon-fit";
import { Terminal } from "@xterm/xterm";
import "@xterm/xterm/css/xterm.css";
import { TERMINAL_TRANSCRIPT_MAX_CHARS } from "./inspectorModel";
import type { TerminalViewer } from "./inspectorRuntime";

const MAX_PENDING_CHUNKS = 64;

export function mountXtermViewer(container: HTMLElement): TerminalViewer {
  const terminal = new Terminal({
    convertEol: true,
    cursorBlink: false,
    disableStdin: true,
    fontFamily: '"Cascadia Code", Consolas, monospace',
    fontSize: 12,
    scrollback: 100_000,
    theme: {
      background: "#181a19",
      foreground: "#d7ddd9",
      cursor: "#74cbb6",
      selectionBackground: "#46635a88",
      black: "#181a19",
      red: "#dd7a72",
      green: "#83bd8c",
      yellow: "#d6aa67",
      blue: "#74a8d8",
      magenta: "#b690c9",
      cyan: "#74cbb6",
      white: "#d7ddd9"
    }
  });
  const fitAddon = new FitAddon();
  terminal.loadAddon(fitAddon);
  terminal.open(container);
  fitAddon.fit();

  let disposed = false;
  let frameId: number | undefined;
  let writeInFlight = false;
  let pendingChunks: string[] = [];
  let pendingCharacterCount = 0;
  let pendingReplacement: string | undefined;

  const hasPendingText = () => pendingReplacement !== undefined || pendingCharacterCount > 0;

  const scheduleFlush = () => {
    if (disposed || frameId !== undefined || !hasPendingText()) {
      return;
    }
    frameId = requestAnimationFrame(flush);
  };

  const write = (text: string) => {
    writeInFlight = true;
    terminal.write(text, () => {
      if (disposed) {
        return;
      }
      writeInFlight = false;
      scheduleFlush();
    });
  };

  const flush = () => {
    frameId = undefined;
    if (disposed || writeInFlight || !hasPendingText()) {
      return;
    }

    if (pendingReplacement !== undefined) {
      const snapshot = pendingReplacement;
      pendingReplacement = undefined;
      terminal.reset();
      terminal.clear();
      if (snapshot.length > 0) {
        write(snapshot);
      } else {
        scheduleFlush();
      }
      return;
    }

    const text = pendingChunks.join("");
    pendingChunks = [];
    pendingCharacterCount = 0;
    write(text);
  };

  return {
    appendText(text) {
      if (disposed) {
        return false;
      }
      if (text.length === 0) {
        return true;
      }
      const replacementCharacterCount = pendingReplacement?.length ?? 0;
      if (text.length > TERMINAL_TRANSCRIPT_MAX_CHARS
        - pendingCharacterCount
        - replacementCharacterCount) {
        return false;
      }
      if (pendingChunks.length >= MAX_PENDING_CHUNKS) {
        pendingChunks = [pendingChunks.join("")];
      }
      pendingChunks.push(text);
      pendingCharacterCount += text.length;
      scheduleFlush();
      return true;
    },
    replaceText(snapshot) {
      if (disposed) {
        return;
      }
      pendingChunks = [];
      pendingCharacterCount = 0;
      pendingReplacement = snapshot.slice(-TERMINAL_TRANSCRIPT_MAX_CHARS);
      scheduleFlush();
    },
    fit() {
      fitAddon.fit();
    },
    dispose() {
      disposed = true;
      pendingChunks = [];
      pendingCharacterCount = 0;
      pendingReplacement = undefined;
      writeInFlight = false;
      if (frameId !== undefined) {
        cancelAnimationFrame(frameId);
        frameId = undefined;
      }
      terminal.dispose();
    }
  };
}
