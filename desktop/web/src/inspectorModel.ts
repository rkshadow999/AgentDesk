import type { HostEvent } from "./hostBridge";

export type InspectorDiff = {
  path: string;
  oldText: string;
  newText: string;
};

export type InspectorPlanEntry = {
  content: string;
  priority: "high" | "medium" | "low";
  status: "pending" | "in_progress" | "completed";
};

export type TerminalTranscript = {
  pages: readonly string[];
  characterCount: number;
};

export const TERMINAL_TRANSCRIPT_MAX_CHARS = 4 * 1024 * 1024;
const TERMINAL_TRANSCRIPT_PAGE_CHARS = 32 * 1024;
const TERMINAL_OUTPUT_TAIL_CHARS = 1024;
const MAX_ORPHAN_TOOLS = 64;
const ORPHAN_FIELD_MAX_CHARS = 4 * 1024;
const ORPHAN_STATE_ENTRY_OVERHEAD_CHARS = 512;
const OUTPUT_HASH_1_SEED = 0x811c9dc5;
const OUTPUT_HASH_2_SEED = 0x9e3779b9;
const BASH_OUTPUT_GAP_PATTERN =
  /^\n\n\.\.\. \(output truncated; [1-9]\d* bytes unavailable\) \.\.\.\n\n/;
const BASH_OUTPUT_GAP_PROBE_BYTES = 128;

type ToolCallState = {
  toolCallId: string;
  title: string;
  kind: string;
  status: string;
  rawInput?: unknown;
  terminalOutputLength: number;
  terminalOutputEndsWithNewline: boolean;
  terminalOutputTail: string;
  terminalOutputHash1: number;
  terminalOutputHash2: number;
  terminalOutputTotalBytes?: number;
  terminalOutputHasGap: boolean;
  terminalOutputIsIncremental: boolean;
  pendingUtf8Bytes: readonly number[];
};

type OrphanToolState = {
  fields: Record<string, string>;
  terminalTranscript: TerminalTranscript;
  terminalOutputLength: number;
  terminalOutputEndsWithNewline: boolean;
  terminalOutputTail: string;
  terminalOutputHash1: number;
  terminalOutputHash2: number;
  terminalOutputTotalBytes?: number;
  terminalOutputHasGap: boolean;
  terminalOutputIsIncremental: boolean;
  pendingUtf8Bytes: readonly number[];
  diffs: InspectorDiff[];
};

export type InspectorState = {
  sessionId?: string;
  diffs: InspectorDiff[];
  selectedPath?: string;
  terminalTranscript: TerminalTranscript;
  terminalAppend: string;
  terminalRevision: number;
  plan: InspectorPlanEntry[];
  toolCalls: Record<string, ToolCallState>;
  orphanToolUpdates: Record<string, OrphanToolState>;
  orphanToolOrder: readonly string[];
};

export function createInitialInspectorState(sessionId?: string): InspectorState {
  return {
    sessionId,
    diffs: [],
    terminalTranscript: { pages: [], characterCount: 0 },
    terminalAppend: "",
    terminalRevision: 0,
    plan: [],
    toolCalls: {},
    orphanToolUpdates: {},
    orphanToolOrder: []
  };
}

export function reduceInspectorEvent(
  state: InspectorState,
  event: HostEvent
): InspectorState {
  // Bind inspector projection to the focused engine session so rapid thread
  // switches never leave stale diffs/terminal/plan from the previous turn.
  if (event.type === "session/active/changed") {
    if (!state.sessionId || event.sessionId !== state.sessionId) {
      return createInitialInspectorState(event.sessionId);
    }
    return state;
  }

  if (event.type === "engine/status") {
    if (event.sessionId &&
        event.sessionId !== state.sessionId &&
        (event.status === "starting" ||
          event.status === "running" ||
          event.status === "ready")) {
      return createInitialInspectorState(event.sessionId);
    }
    return event.sessionId && !state.sessionId ? { ...state, sessionId: event.sessionId } : state;
  }

  if (event.type !== "session/update") {
    return state;
  }

  if (state.sessionId && event.sessionId !== state.sessionId) {
    return state;
  }

  const update = asRecord(event.update);
  if (!update) {
    return state;
  }

  const current = state.sessionId ? state : { ...state, sessionId: event.sessionId };
  switch (event.updateKind) {
    case "diff_review":
      return addDiffs(current, parseDiffs(update.content));
    case "tool_call":
      return applyToolCall(current, update, false);
    case "tool_call_update":
      return applyToolCall(current, update, true);
    case "plan":
      return { ...current, plan: parsePlan(update.entries) };
    default:
      return current;
  }
}

function applyToolCall(
  state: InspectorState,
  update: Record<string, unknown>,
  partial: boolean
): InspectorState {
  const toolCallId = nonEmptyString(update.toolCallId);
  if (!toolCallId) {
    return state;
  }

  const existing = state.toolCalls[toolCallId];
  if (!existing && partial) {
    return bufferOrphanToolUpdate(state, toolCallId, update);
  }

  const orphan = partial ? undefined : state.orphanToolUpdates[toolCallId];
  const effectiveUpdate = orphan ? { ...update, ...orphan.fields } : update;
  const next: ToolCallState = {
    toolCallId,
    title: nonEmptyString(effectiveUpdate.title) ?? existing?.title ?? toolCallId,
    kind: nonEmptyString(effectiveUpdate.kind) ?? existing?.kind ?? "other",
    status: nonEmptyString(effectiveUpdate.status) ?? existing?.status ?? "pending",
    terminalOutputLength: existing?.terminalOutputLength ?? 0,
    terminalOutputEndsWithNewline: existing?.terminalOutputEndsWithNewline ?? true,
    terminalOutputTail: existing?.terminalOutputTail ?? "",
    terminalOutputHash1: existing?.terminalOutputHash1 ?? OUTPUT_HASH_1_SEED,
    terminalOutputHash2: existing?.terminalOutputHash2 ?? OUTPUT_HASH_2_SEED,
    terminalOutputTotalBytes: existing?.terminalOutputTotalBytes,
    terminalOutputHasGap: existing?.terminalOutputHasGap ?? false,
    terminalOutputIsIncremental: existing?.terminalOutputIsIncremental ?? false,
    pendingUtf8Bytes: existing?.pendingUtf8Bytes ?? [],
    ...(Object.hasOwn(effectiveUpdate, "rawInput")
      ? { rawInput: effectiveUpdate.rawInput }
      : Object.hasOwn(existing ?? {}, "rawInput")
        ? { rawInput: existing?.rawInput }
        : {})
  };
  let terminalProjection = orphan
    ? orphanOutputProjection(orphan)
    : projectTerminalOutput(
      existing?.terminalOutputLength ?? 0,
      existing?.terminalOutputEndsWithNewline ?? true,
      existing?.terminalOutputTail ?? "",
      existing?.terminalOutputHash1 ?? OUTPUT_HASH_1_SEED,
      existing?.terminalOutputHash2 ?? OUTPUT_HASH_2_SEED,
      existing?.terminalOutputTotalBytes,
      existing?.terminalOutputHasGap ?? false,
      existing?.terminalOutputIsIncremental ?? false,
      existing?.pendingUtf8Bytes ?? [],
      shouldProjectInitialOutput(existing, next, update)
        ? parseTerminalOutputUpdate(update)
        : undefined
    );
  if (isTerminalStatus(next.status) && next.status !== existing?.status) {
    terminalProjection = flushPendingUtf8(terminalProjection);
  }
  next.terminalOutputLength = terminalProjection.outputLength;
  next.terminalOutputEndsWithNewline = terminalProjection.outputEndsWithNewline;
  next.terminalOutputTail = terminalProjection.outputTail;
  next.terminalOutputHash1 = terminalProjection.outputHash1;
  next.terminalOutputHash2 = terminalProjection.outputHash2;
  next.terminalOutputTotalBytes = terminalProjection.totalBytes;
  next.terminalOutputHasGap = terminalProjection.hasGap;
  next.terminalOutputIsIncremental = terminalProjection.isIncremental;
  next.pendingUtf8Bytes = terminalProjection.pendingUtf8Bytes;

  const terminalAppend = formatTerminalAppend(
    state.terminalTranscript.characterCount > 0,
    existing,
    next,
    terminalProjection
  );
  const toolCalls = { ...state.toolCalls, [toolCallId]: next };
  const orphanToolUpdates = { ...state.orphanToolUpdates };
  delete orphanToolUpdates[toolCallId];
  const orphanToolOrder = state.orphanToolOrder.filter((id) => id !== toolCallId);
  const withTool = {
    ...state,
    toolCalls,
    orphanToolUpdates,
    orphanToolOrder
  };
  const updateContent = Array.isArray(update.content) ? update.content : [];
  const diffAdditions = orphan
    ? [...orphan.diffs, ...parseDiffs(updateContent)]
    : parseDiffs(updateContent);
  return addDiffs(
    appendTerminal(withTool, terminalAppend),
    diffAdditions
  );
}

function bufferOrphanToolUpdate(
  state: InspectorState,
  toolCallId: string,
  update: Record<string, unknown>
): InspectorState {
  const previous = state.orphanToolUpdates[toolCallId];
  const projection = projectTerminalOutput(
    previous?.terminalOutputLength ?? 0,
    previous?.terminalOutputEndsWithNewline ?? true,
    previous?.terminalOutputTail ?? "",
    previous?.terminalOutputHash1 ?? OUTPUT_HASH_1_SEED,
    previous?.terminalOutputHash2 ?? OUTPUT_HASH_2_SEED,
    previous?.terminalOutputTotalBytes,
    previous?.terminalOutputHasGap ?? false,
    previous?.terminalOutputIsIncremental ?? false,
    previous?.pendingUtf8Bytes ?? [],
    parseTerminalOutputUpdate(update)
  );
  const transcript = projection.append
    ? appendTerminalTranscript(
      previous?.terminalTranscript ?? { pages: [], characterCount: 0 },
      projection.append
    )
    : previous?.terminalTranscript ?? { pages: [], characterCount: 0 };
  const content = Array.isArray(update.content) ? update.content : [];
  const orphan: OrphanToolState = {
    fields: mergeOrphanFields(previous?.fields, update),
    terminalTranscript: transcript,
    terminalOutputLength: projection.outputLength,
    terminalOutputEndsWithNewline: projection.outputEndsWithNewline,
    terminalOutputTail: projection.outputTail,
    terminalOutputHash1: projection.outputHash1,
    terminalOutputHash2: projection.outputHash2,
    terminalOutputTotalBytes: projection.totalBytes,
    terminalOutputHasGap: projection.hasGap,
    terminalOutputIsIncremental: projection.isIncremental,
    pendingUtf8Bytes: projection.pendingUtf8Bytes,
    diffs: mergeDiffs(previous?.diffs ?? [], parseDiffs(content))
  };
  const orphanToolUpdates = {
    ...state.orphanToolUpdates,
    [toolCallId]: orphan
  };
  const orphanToolOrder = [
    ...state.orphanToolOrder.filter((id) => id !== toolCallId),
    toolCallId
  ];
  return pruneOrphanTools(state, orphanToolUpdates, orphanToolOrder);
}

function pruneOrphanTools(
  state: InspectorState,
  orphanToolUpdates: Record<string, OrphanToolState>,
  orphanToolOrder: string[]
): InspectorState {
  let totalCharacters = orphanToolOrder.reduce(
    (total, id) => total + orphanToolStateCharacterCount(
      id,
      orphanToolUpdates[id]
    ),
    0
  );
  while (orphanToolOrder.length > MAX_ORPHAN_TOOLS ||
         totalCharacters > TERMINAL_TRANSCRIPT_MAX_CHARS) {
    const evictedId = orphanToolOrder.shift();
    if (!evictedId) {
      break;
    }
    totalCharacters -= orphanToolStateCharacterCount(
      evictedId,
      orphanToolUpdates[evictedId]
    );
    delete orphanToolUpdates[evictedId];
  }
  return { ...state, orphanToolUpdates, orphanToolOrder };
}

function mergeOrphanFields(
  previous: Record<string, string> | undefined,
  update: Record<string, unknown>
): Record<string, string> {
  const fields = { ...previous };
  for (const key of ["title", "kind", "status"] as const) {
    const value = boundedOrphanField(update[key]);
    if (value !== undefined) {
      fields[key] = value;
    }
  }
  return fields;
}

function boundedOrphanField(value: unknown): string | undefined {
  if (typeof value !== "string" || !value.trim()) {
    return undefined;
  }
  if (value.length <= ORPHAN_FIELD_MAX_CHARS) {
    return value;
  }
  let end = ORPHAN_FIELD_MAX_CHARS;
  if (isHighSurrogate(value.charCodeAt(end - 1)) &&
      isLowSurrogate(value.charCodeAt(end))) {
    end -= 1;
  }
  if (value[end - 1] === "\r" && value[end] === "\n") {
    end -= 1;
  }
  return value.slice(0, end);
}

function orphanToolStateCharacterCount(
  toolCallId: string,
  orphan: OrphanToolState | undefined
): number {
  if (!orphan) {
    return 0;
  }
  const fieldCharacters = Object.entries(orphan.fields).reduce(
    (total, [key, value]) => total + key.length + value.length + 8,
    0
  );
  const diffCharacters = orphan.diffs.reduce(
    (total, diff) => total + diff.path.length + diff.oldText.length +
      diff.newText.length + 64,
    0
  );
  return ORPHAN_STATE_ENTRY_OVERHEAD_CHARS + toolCallId.length * 2 +
    fieldCharacters + orphan.terminalTranscript.characterCount +
    orphan.terminalTranscript.pages.length * 4 +
    orphan.terminalOutputTail.length + orphan.pendingUtf8Bytes.length * 4 +
    diffCharacters;
}

function mergeDiffs(existing: InspectorDiff[], additions: InspectorDiff[]): InspectorDiff[] {
  if (additions.length === 0) {
    return existing;
  }
  const byPath = new Map(existing.map((diff) => [diff.path, diff]));
  for (const diff of additions) {
    byPath.set(diff.path, diff);
  }
  return [...byPath.values()];
}

function addDiffs(state: InspectorState, additions: InspectorDiff[]): InspectorState {
  if (additions.length === 0) {
    return state;
  }

  const byPath = new Map(state.diffs.map((diff) => [diff.path, diff]));
  for (const diff of additions) {
    byPath.set(diff.path, diff);
  }
  const diffs = [...byPath.values()];
  const selectedPath = state.selectedPath && byPath.has(state.selectedPath)
    ? state.selectedPath
    : diffs[0]?.path;
  return { ...state, diffs, selectedPath };
}

function parseDiffs(value: unknown): InspectorDiff[] {
  if (!Array.isArray(value)) {
    return [];
  }

  return value.flatMap((candidate) => {
    const record = asRecord(candidate);
    if (!record || record.type !== "diff") {
      return [];
    }
    const path = nonEmptyString(record.path);
    const newText = typeof record.newText === "string" ? record.newText : undefined;
    if (!path || newText === undefined) {
      return [];
    }
    return [{
      path,
      oldText: typeof record.oldText === "string" ? record.oldText : "",
      newText
    }];
  });
}

function parsePlan(value: unknown): InspectorPlanEntry[] {
  if (!Array.isArray(value)) {
    return [];
  }

  return value.flatMap((candidate) => {
    const record = asRecord(candidate);
    const content = record && nonEmptyString(record.content);
    if (!record || !content || !isPriority(record.priority) || !isPlanStatus(record.status)) {
      return [];
    }
    return [{ content, priority: record.priority, status: record.status }];
  });
}

type TerminalOutputUpdate = {
  fullOutput?: string;
  fullOutputBytes?: readonly number[];
  deltaBytes?: readonly number[];
  totalBytes?: number;
  deltaHasGap?: boolean;
};

type TerminalOutputProjection = {
  append: string;
  outputLength: number;
  outputEndsWithNewline: boolean;
  outputTail: string;
  outputHash1: number;
  outputHash2: number;
  totalBytes?: number;
  hasGap: boolean;
  isIncremental: boolean;
  pendingUtf8Bytes: readonly number[];
};

function shouldProjectInitialOutput(
  existing: ToolCallState | undefined,
  next: ToolCallState,
  update: Record<string, unknown>
): boolean {
  return existing !== undefined || Object.hasOwn(update, "rawOutput") ||
    isTerminalStatus(next.status);
}

function parseTerminalOutputUpdate(
  update: Record<string, unknown>
): TerminalOutputUpdate | undefined {
  const fullOutput = readFullTerminalOutput(update);
  if (Object.hasOwn(update, "rawOutput")) {
    const deltaBytes = extractBashDeltaBytes(update.rawOutput);
    const fullOutputBytes = extractBashOutputBytes(update.rawOutput);
    const totalBytes = extractBashTotalBytes(update.rawOutput);
    if (deltaBytes !== undefined) {
      return {
        deltaBytes,
        deltaHasGap: hasBashOutputGap(deltaBytes),
        ...(deltaBytes.length === 0 && fullOutput !== undefined ? { fullOutput } : {}),
        ...(fullOutputBytes !== undefined ? { fullOutputBytes } : {}),
        ...(totalBytes !== undefined ? { totalBytes } : {})
      };
    }
    if (fullOutput !== undefined) {
      return {
        fullOutput,
        ...(fullOutputBytes !== undefined ? { fullOutputBytes } : {}),
        ...(totalBytes !== undefined ? { totalBytes } : {})
      };
    }
  }

  return fullOutput === undefined ? undefined : { fullOutput };
}

function readFullTerminalOutput(update: Record<string, unknown>): string | undefined {
  if (Array.isArray(update.content)) {
    const content = extractOptionalTextContent(update.content);
    if (content !== undefined) {
      return content;
    }
  }

  if (Object.hasOwn(update, "rawOutput")) {
    return extractBashOutput(update.rawOutput) ?? formatOutput(update.rawOutput);
  }
  return undefined;
}

function projectTerminalOutput(
  previousOutputLength: number,
  previousOutputEndsWithNewline: boolean,
  previousOutputTail: string,
  previousOutputHash1: number,
  previousOutputHash2: number,
  previousTotalBytes: number | undefined,
  previousHasGap: boolean,
  previousIsIncremental: boolean,
  pendingUtf8Bytes: readonly number[],
  update: TerminalOutputUpdate | undefined
): TerminalOutputProjection {
  if (!update) {
    return {
      append: "",
      outputLength: previousOutputLength,
      outputEndsWithNewline: previousOutputEndsWithNewline,
      outputTail: previousOutputTail,
      outputHash1: previousOutputHash1,
      outputHash2: previousOutputHash2,
      totalBytes: previousTotalBytes,
      hasGap: previousHasGap,
      isIncremental: previousIsIncremental,
      pendingUtf8Bytes
    };
  }

  let effectiveUpdate = update;
  if (update.deltaBytes !== undefined && update.deltaBytes.length === 0 &&
      previousTotalBytes !== undefined && update.totalBytes !== undefined &&
      update.totalBytes <= previousTotalBytes) {
    return {
      append: "",
      outputLength: previousOutputLength,
      outputEndsWithNewline: previousOutputEndsWithNewline,
      outputTail: previousOutputTail,
      outputHash1: previousOutputHash1,
      outputHash2: previousOutputHash2,
      totalBytes: previousTotalBytes,
      hasGap: previousHasGap,
      isIncremental: true,
      pendingUtf8Bytes
    };
  }
  if (update.deltaBytes === undefined && previousIsIncremental &&
      previousTotalBytes !== undefined && update.totalBytes !== undefined) {
    if (update.totalBytes <= previousTotalBytes) {
      return {
        append: "",
        outputLength: previousOutputLength,
        outputEndsWithNewline: previousOutputEndsWithNewline,
        outputTail: previousOutputTail,
        outputHash1: previousOutputHash1,
        outputHash2: previousOutputHash2,
        totalBytes: previousTotalBytes,
        hasGap: previousHasGap,
        isIncremental: true,
        pendingUtf8Bytes
      };
    }
    const newByteCount = update.totalBytes - previousTotalBytes;
    if (update.fullOutputBytes !== undefined &&
        newByteCount <= update.fullOutputBytes.length) {
      effectiveUpdate = {
        ...update,
        deltaBytes: update.fullOutputBytes.slice(-newByteCount),
        deltaHasGap: false
      };
    } else if (update.fullOutputBytes !== undefined) {
      effectiveUpdate = {
        ...update,
        deltaBytes: bashGapDeltaBytes(newByteCount, []),
        deltaHasGap: true
      };
    }
  }

  if (effectiveUpdate.deltaBytes !== undefined) {
    if (effectiveUpdate.deltaBytes.length === 0) {
      if (effectiveUpdate.fullOutput !== undefined) {
        return projectFullTerminalOutput(
          previousOutputLength,
          previousOutputEndsWithNewline,
          previousOutputHash1,
          previousOutputHash2,
          effectiveUpdate.fullOutput,
          monotonicTotal(previousTotalBytes, effectiveUpdate.totalBytes)
        );
      }
      return {
        append: "",
        outputLength: previousOutputLength,
        outputEndsWithNewline: previousOutputEndsWithNewline,
        outputTail: previousOutputTail,
        outputHash1: previousOutputHash1,
        outputHash2: previousOutputHash2,
        totalBytes: monotonicTotal(previousTotalBytes, effectiveUpdate.totalBytes),
        hasGap: previousHasGap || effectiveUpdate.deltaHasGap === true,
        isIncremental: true,
        pendingUtf8Bytes
      };
    }
    const decoded = decodeUtf8Delta(pendingUtf8Bytes, effectiveUpdate.deltaBytes);
    const fingerprint = extendOutputFingerprint(
      previousOutputHash1,
      previousOutputHash2,
      decoded.text
    );
    return {
      append: decoded.text,
      outputLength: previousOutputLength + decoded.text.length,
      outputEndsWithNewline: decoded.text
        ? decoded.text.endsWith("\n")
        : previousOutputEndsWithNewline,
      outputTail: appendOutputTail(previousOutputTail, decoded.text),
      outputHash1: fingerprint.hash1,
      outputHash2: fingerprint.hash2,
      totalBytes: monotonicTotal(previousTotalBytes, effectiveUpdate.totalBytes),
      hasGap: previousHasGap || effectiveUpdate.deltaHasGap === true,
      isIncremental: true,
      pendingUtf8Bytes: decoded.pendingBytes
    };
  }

  return projectFullTerminalOutput(
    previousOutputLength,
    previousOutputEndsWithNewline,
    previousOutputHash1,
    previousOutputHash2,
    effectiveUpdate.fullOutput ?? "",
    monotonicTotal(previousTotalBytes, effectiveUpdate.totalBytes)
  );
}

function projectFullTerminalOutput(
  previousOutputLength: number,
  previousOutputEndsWithNewline: boolean,
  previousOutputHash1: number,
  previousOutputHash2: number,
  nextOutput: string,
  totalBytes?: number
): TerminalOutputProjection {
  let append = "";
  const prefixFingerprint = nextOutput.length >= previousOutputLength
    ? extendOutputFingerprint(
      OUTPUT_HASH_1_SEED,
      OUTPUT_HASH_2_SEED,
      nextOutput,
      0,
      previousOutputLength
    )
    : undefined;
  const prefixMatches = prefixFingerprint !== undefined &&
    prefixFingerprint.hash1 === previousOutputHash1 &&
    prefixFingerprint.hash2 === previousOutputHash2;
  if (nextOutput.length > previousOutputLength && prefixMatches) {
    append = nextOutput.slice(previousOutputLength);
  } else if (!prefixMatches || nextOutput.length < previousOutputLength) {
    append = previousOutputEndsWithNewline ? nextOutput : `\n${nextOutput}`;
  }
  const fingerprint = prefixFingerprint
    ? extendOutputFingerprint(
      prefixFingerprint.hash1,
      prefixFingerprint.hash2,
      nextOutput,
      previousOutputLength
    )
    : extendOutputFingerprint(
      OUTPUT_HASH_1_SEED,
      OUTPUT_HASH_2_SEED,
      nextOutput
    );
  return {
    append,
    outputLength: nextOutput.length,
    outputEndsWithNewline: nextOutput.endsWith("\n"),
    outputTail: outputTail(nextOutput),
    outputHash1: fingerprint.hash1,
    outputHash2: fingerprint.hash2,
    totalBytes,
    hasGap: false,
    isIncremental: false,
    pendingUtf8Bytes: []
  };
}

function flushPendingUtf8(projection: TerminalOutputProjection): TerminalOutputProjection {
  if (projection.pendingUtf8Bytes.length === 0) {
    return projection;
  }
  const replacement = new TextDecoder().decode(
    Uint8Array.from(projection.pendingUtf8Bytes)
  );
  const fingerprint = extendOutputFingerprint(
    projection.outputHash1,
    projection.outputHash2,
    replacement
  );
  return {
    append: projection.append + replacement,
    outputLength: projection.outputLength + replacement.length,
    outputEndsWithNewline: replacement
      ? replacement.endsWith("\n")
      : projection.outputEndsWithNewline,
    outputTail: appendOutputTail(projection.outputTail, replacement),
    outputHash1: fingerprint.hash1,
    outputHash2: fingerprint.hash2,
    totalBytes: projection.totalBytes,
    hasGap: projection.hasGap,
    isIncremental: projection.isIncremental,
    pendingUtf8Bytes: []
  };
}

function extendOutputFingerprint(
  hash1: number,
  hash2: number,
  text: string,
  start = 0,
  end = text.length
): { hash1: number; hash2: number } {
  for (let index = start; index < end; index += 1) {
    const codeUnit = text.charCodeAt(index);
    hash1 = Math.imul(hash1 ^ codeUnit, 0x01000193) >>> 0;
    hash2 = (Math.imul(hash2, 65_599) + codeUnit) >>> 0;
  }
  return { hash1, hash2 };
}

function outputTail(text: string): string {
  return text.length <= TERMINAL_OUTPUT_TAIL_CHARS
    ? text
    : text.slice(text.length - TERMINAL_OUTPUT_TAIL_CHARS);
}

function appendOutputTail(previousTail: string, append: string): string {
  return outputTail(previousTail + append);
}

function orphanOutputProjection(orphan: OrphanToolState): TerminalOutputProjection {
  return {
    append: getTerminalSnapshot(orphan.terminalTranscript),
    outputLength: orphan.terminalOutputLength,
    outputEndsWithNewline: orphan.terminalOutputEndsWithNewline,
    outputTail: orphan.terminalOutputTail,
    outputHash1: orphan.terminalOutputHash1,
    outputHash2: orphan.terminalOutputHash2,
    totalBytes: orphan.terminalOutputTotalBytes,
    hasGap: orphan.terminalOutputHasGap,
    isIncremental: orphan.terminalOutputIsIncremental,
    pendingUtf8Bytes: orphan.pendingUtf8Bytes
  };
}

function formatTerminalAppend(
  hasTerminalOutput: boolean,
  previous: ToolCallState | undefined,
  next: ToolCallState,
  output: TerminalOutputProjection
): string {
  if (next.kind !== "execute") {
    return "";
  }

  const previousWasExecute = previous?.kind === "execute";
  let append = "";
  if (!previousWasExecute) {
    const command = formatCommand(next.rawInput);
    append = `${hasTerminalOutput ? "\n\n" : ""}# ${next.title}\n${
      command ? `> ${command}` : "> 命令参数不可用"
    }\n`;
  }
  append += output.append;

  if (isTerminalStatus(next.status) && next.status !== previous?.status) {
    const hasPrecedingText = append.length > 0 || output.outputLength > 0;
    const precedingTextEndsWithNewline = append
      ? append.endsWith("\n")
      : output.outputEndsWithNewline;
    if (hasPrecedingText && !precedingTextEndsWithNewline) {
      append += "\n";
    }
    append += `[${next.status}]\n`;
  }
  return append;
}

function isTerminalStatus(status: string): boolean {
  return status === "completed" || status === "failed";
}

function appendTerminal(state: InspectorState, text: string): InspectorState {
  if (!text) {
    return state;
  }
  return {
    ...state,
    terminalTranscript: appendTerminalTranscript(state.terminalTranscript, text),
    terminalAppend: terminalSuffix(text, TERMINAL_TRANSCRIPT_MAX_CHARS),
    terminalRevision: state.terminalRevision + 1
  };
}

function appendTerminalTranscript(
  transcript: TerminalTranscript,
  text: string
): TerminalTranscript {
  if (text.length >= TERMINAL_TRANSCRIPT_MAX_CHARS) {
    const retained = terminalSuffix(text, TERMINAL_TRANSCRIPT_MAX_CHARS);
    return {
      pages: paginateTerminalText(retained),
      characterCount: retained.length
    };
  }

  const pages = [...transcript.pages];
  let offset = 0;
  const lastIndex = pages.length - 1;
  if (lastIndex >= 0 && pages[lastIndex].length < TERMINAL_TRANSCRIPT_PAGE_CHARS) {
    const take = Math.min(
      TERMINAL_TRANSCRIPT_PAGE_CHARS - pages[lastIndex].length,
      text.length
    );
    pages[lastIndex] += text.slice(0, take);
    offset = take;
  }
  while (offset < text.length) {
    pages.push(text.slice(offset, offset + TERMINAL_TRANSCRIPT_PAGE_CHARS));
    offset += TERMINAL_TRANSCRIPT_PAGE_CHARS;
  }

  const totalCharacters = transcript.characterCount + text.length;
  let overflow = Math.max(0, totalCharacters - TERMINAL_TRANSCRIPT_MAX_CHARS);
  let firstPage = 0;
  let lastRemovedCharacter = "";
  while (overflow > 0 && firstPage < pages.length &&
         overflow >= pages[firstPage].length) {
    overflow -= pages[firstPage].length;
    lastRemovedCharacter = pages[firstPage].slice(-1);
    firstPage += 1;
  }
  const retainedPages = pages.slice(firstPage);
  if (overflow > 0 && retainedPages.length > 0) {
    const firstRetainedPage = retainedPages[0];
    lastRemovedCharacter = firstRetainedPage[overflow - 1] ?? lastRemovedCharacter;
    retainedPages[0] = firstRetainedPage.slice(
      safeTerminalBoundary(firstRetainedPage, overflow)
    );
  } else if (retainedPages.length > 0) {
    retainedPages[0] = removeBrokenTerminalPrefix(
      retainedPages[0],
      lastRemovedCharacter
    );
  }

  return {
    pages: retainedPages,
    characterCount: retainedPages.reduce((total, page) => total + page.length, 0)
  };
}

function paginateTerminalText(text: string): string[] {
  const pages: string[] = [];
  for (let offset = 0; offset < text.length; offset += TERMINAL_TRANSCRIPT_PAGE_CHARS) {
    pages.push(text.slice(offset, offset + TERMINAL_TRANSCRIPT_PAGE_CHARS));
  }
  return pages;
}

function terminalSuffix(text: string, maximumLength: number): string {
  if (text.length <= maximumLength) {
    return text;
  }
  return text.slice(safeTerminalBoundary(text, text.length - maximumLength));
}

function safeTerminalBoundary(text: string, requestedIndex: number): number {
  let index = Math.max(0, Math.min(requestedIndex, text.length));
  if (index > 0 && index < text.length &&
      isHighSurrogate(text.charCodeAt(index - 1)) &&
      isLowSurrogate(text.charCodeAt(index))) {
    index += 1;
  }
  if (index > 0 && text[index - 1] === "\r" && text[index] === "\n") {
    index += 1;
  }
  return index;
}

function removeBrokenTerminalPrefix(text: string, previousCharacter: string): string {
  let offset = 0;
  if (text && isLowSurrogate(text.charCodeAt(0))) {
    offset = 1;
  }
  if (previousCharacter === "\r" && text[offset] === "\n") {
    offset += 1;
  }
  return offset === 0 ? text : text.slice(offset);
}

function isHighSurrogate(value: number): boolean {
  return value >= 0xd800 && value <= 0xdbff;
}

function isLowSurrogate(value: number): boolean {
  return value >= 0xdc00 && value <= 0xdfff;
}

export function getTerminalSnapshot(transcript: TerminalTranscript): string {
  return transcript.pages.join("");
}

function formatCommand(value: unknown): string {
  if (typeof value === "string") {
    return value;
  }
  const record = asRecord(value);
  if (!record) {
    return "";
  }
  const command = nonEmptyString(record.command) ?? nonEmptyString(record.cmd);
  if (!command) {
    return "";
  }
  const args = Array.isArray(record.args)
    ? record.args.filter((arg): arg is string | number =>
      typeof arg === "string" || typeof arg === "number")
    : [];
  return [command, ...args.map(String)].join(" ");
}

function extractOptionalTextContent(content: unknown[]): string | undefined {
  let first: string | undefined;
  let additional: string[] | undefined;
  for (const candidate of content) {
    const item = asRecord(candidate);
    if (!item || item.type !== "content") {
      continue;
    }
    const block = asRecord(item.content);
    if (block?.type !== "text" || typeof block.text !== "string") {
      continue;
    }
    if (first === undefined) {
      first = block.text;
    } else {
      (additional ??= []).push(block.text);
    }
  }
  return additional ? [first, ...additional].join("\n") : first;
}

function extractBashOutput(value: unknown): string | undefined {
  const record = asRecord(value);
  if (!record || (record.type !== "Bash" && record.type !== "bash")) {
    return undefined;
  }
  const decodedOutput = decodeBytes(record.output);
  if (decodedOutput) {
    return decodedOutput;
  }
  const outputForPrompt = record.output_for_prompt ?? record.outputForPrompt;
  if (typeof outputForPrompt === "string") {
    return outputForPrompt;
  }
  return decodedOutput ?? "";
}

function extractBashDeltaBytes(value: unknown): readonly number[] | undefined {
  const record = bashOutputRecord(value);
  if (!record) return undefined;
  return validBytes(record.output_delta ?? record.outputDelta);
}

function extractBashOutputBytes(value: unknown): readonly number[] | undefined {
  const record = bashOutputRecord(value);
  return record ? validBytes(record.output) : undefined;
}

function extractBashTotalBytes(value: unknown): number | undefined {
  const record = bashOutputRecord(value);
  if (!record) return undefined;
  const total = record.total_bytes ?? record.totalBytes;
  return typeof total === "number" && Number.isSafeInteger(total) && total >= 0
    ? total
    : undefined;
}

function bashOutputRecord(value: unknown): Record<string, unknown> | undefined {
  const record = asRecord(value);
  return record && (record.type === "Bash" || record.type === "bash")
    ? record
    : undefined;
}

function hasBashOutputGap(bytes: readonly number[]): boolean {
  if (bytes.length === 0) return false;
  const probe = new TextDecoder().decode(Uint8Array.from(
    bytes.slice(0, BASH_OUTPUT_GAP_PROBE_BYTES)
  ));
  return BASH_OUTPUT_GAP_PATTERN.test(probe);
}

function bashGapDeltaBytes(
  missingBytes: number,
  retained: readonly number[]
): readonly number[] {
  const marker = new TextEncoder().encode(
    `\n\n... (output truncated; ${missingBytes} bytes unavailable) ...\n\n`
  );
  const delta = new Uint8Array(marker.length + retained.length);
  delta.set(marker);
  delta.set(retained, marker.length);
  return Array.from(delta);
}

function monotonicTotal(
  previous: number | undefined,
  current: number | undefined
): number | undefined {
  if (current === undefined) return previous;
  return previous === undefined ? current : Math.max(previous, current);
}

function decodeBytes(value: unknown): string | undefined {
  const bytes = validBytes(value);
  return bytes ? new TextDecoder().decode(Uint8Array.from(bytes)) : undefined;
}

function validBytes(value: unknown): readonly number[] | undefined {
  if (!Array.isArray(value) || !value.every((item) =>
    Number.isInteger(item) && item >= 0 && item <= 255)) {
    return undefined;
  }
  return value as number[];
}

function decodeUtf8Delta(
  pendingBytes: readonly number[],
  deltaBytes: readonly number[]
): { text: string; pendingBytes: readonly number[] } {
  const bytes = new Uint8Array(pendingBytes.length + deltaBytes.length);
  bytes.set(pendingBytes);
  bytes.set(deltaBytes, pendingBytes.length);
  const pendingLength = incompleteUtf8SuffixLength(bytes);
  const completeLength = bytes.length - pendingLength;
  return {
    text: completeLength > 0
      ? new TextDecoder().decode(bytes.subarray(0, completeLength))
      : "",
    pendingBytes: pendingLength > 0
      ? Array.from(bytes.subarray(completeLength))
      : []
  };
}

function incompleteUtf8SuffixLength(bytes: Uint8Array): number {
  if (bytes.length === 0) {
    return 0;
  }
  let leadIndex = bytes.length - 1;
  while (leadIndex >= 0 && (bytes[leadIndex] & 0xc0) === 0x80) {
    leadIndex -= 1;
  }
  if (leadIndex < 0) {
    return 0;
  }
  const lead = bytes[leadIndex];
  const expectedLength = lead >= 0xc2 && lead <= 0xdf
    ? 2
    : lead >= 0xe0 && lead <= 0xef
      ? 3
      : lead >= 0xf0 && lead <= 0xf4
        ? 4
        : 1;
  const availableLength = bytes.length - leadIndex;
  return expectedLength > availableLength ? availableLength : 0;
}

function formatOutput(value: unknown): string {
  if (typeof value === "string") {
    return value;
  }
  if (value === undefined || value === null) {
    return "";
  }
  return JSON.stringify(value, null, 2) ?? String(value);
}

function isPriority(value: unknown): value is InspectorPlanEntry["priority"] {
  return value === "high" || value === "medium" || value === "low";
}

function isPlanStatus(value: unknown): value is InspectorPlanEntry["status"] {
  return value === "pending" || value === "in_progress" || value === "completed";
}

function nonEmptyString(value: unknown): string | undefined {
  return typeof value === "string" && value.trim() ? value : undefined;
}

function asRecord(value: unknown): Record<string, unknown> | undefined {
  return typeof value === "object" && value !== null && !Array.isArray(value)
    ? value as Record<string, unknown>
    : undefined;
}
