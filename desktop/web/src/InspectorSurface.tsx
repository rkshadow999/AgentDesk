import {
  CheckCircle2,
  Circle,
  FileCode2,
  FileDiff,
  ListChecks,
  LoaderCircle,
  TerminalSquare
} from "lucide-react";
import { useEffect, useReducer, useRef, useState } from "react";
import { createTranslator } from "./i18n";
import {
  defaultHostBridge,
  type HostBridge,
  type HostEvent,
  type UiLanguage
} from "./hostBridge";
import {
  createInitialInspectorState,
  getTerminalSnapshot,
  reduceInspectorEvent,
  type InspectorPlanEntry
} from "./inspectorModel";
import {
  defaultInspectorRuntime,
  type DiffViewer,
  type InspectorRuntime,
  type TerminalViewer
} from "./inspectorRuntime";
import { TerminalRevisionGate } from "./terminalRevisionGate";
import {
  applyFontScalePercent,
  codeFontSizeForScale,
  defaultFontScalePercent
} from "./fontScale";
import enUs from "./locales/en-US.json";
import zhCn from "./locales/zh-CN.json";
import "./styles.css";

type InspectorTab = "changes" | "terminal" | "plan";

const inspectorTabOrder: readonly InspectorTab[] = ["changes", "terminal", "plan"];

export function InspectorSurface({
  bridge = defaultHostBridge,
  runtime = defaultInspectorRuntime
}: {
  bridge?: HostBridge;
  runtime?: InspectorRuntime;
}) {
  const [state, dispatch] = useReducer(reduceInspectorEvent, undefined, () =>
    createInitialInspectorState());
  const [language, setLanguage] = useState<UiLanguage>("zh-CN");
  const t = createTranslator(enUs, language === "zh-CN" ? zhCn : enUs);
  const [activeTab, setActiveTab] = useState<InspectorTab>("changes");
  const [selectedPath, setSelectedPath] = useState<string>();
  const diffHostRef = useRef<HTMLDivElement>(null);
  const terminalHostRef = useRef<HTMLDivElement>(null);
  const diffViewerRef = useRef<DiffViewer | undefined>(undefined);
  const terminalViewerRef = useRef<TerminalViewer | undefined>(undefined);
  const tabRefs = useRef<Record<InspectorTab, HTMLButtonElement | null>>({
    changes: null,
    terminal: null,
    plan: null
  });
  // When the user picks a tab, stop auto-following until the session resets.
  const userPinnedTabRef = useRef(false);
  const previousDiffCountRef = useRef(0);
  const previousPlanCountRef = useRef(0);
  const previousTerminalRevisionRef = useRef(0);
  const hasDiffs = state.diffs.length > 0;
  const effectiveSelectedPath = selectedPath && state.diffs.some((diff) => diff.path === selectedPath)
    ? selectedPath
    : state.selectedPath;
  const selectedDiff = state.diffs.find((diff) => diff.path === effectiveSelectedPath);
  const selectedDiffRef = useRef(selectedDiff);
  const terminalTranscriptRef = useRef(state.terminalTranscript);
  const terminalRevisionRef = useRef(state.terminalRevision);
  const terminalRevisionGateRef = useRef(new TerminalRevisionGate());
  const fontScalePercentRef = useRef(defaultFontScalePercent);
  selectedDiffRef.current = selectedDiff;
  terminalTranscriptRef.current = state.terminalTranscript;
  terminalRevisionRef.current = state.terminalRevision;

  function selectTab(tab: InspectorTab, options?: { pinned?: boolean }) {
    if (options?.pinned !== false) {
      userPinnedTabRef.current = true;
    }
    setActiveTab(tab);
  }

  useEffect(() => {
    bridge.send({ type: "ui/ready" });
    return bridge.subscribe((event: HostEvent) => {
      if (event.type === "ui/preferences/changed") {
        setLanguage(event.language);
        fontScalePercentRef.current = event.fontScalePercent;
        applyFontScalePercent(event.fontScalePercent);
        const fontSize = codeFontSizeForScale(event.fontScalePercent);
        diffViewerRef.current?.setFontSize(fontSize);
        diffViewerRef.current?.layout();
        terminalViewerRef.current?.setFontSize(fontSize);
        terminalViewerRef.current?.fit();
      }
      dispatch(event);
    });
  }, [bridge]);

  useEffect(() => {
    if (!diffHostRef.current) {
      return;
    }
    let disposed = false;
    void runtime.mountDiff(diffHostRef.current).then((viewer) => {
      if (disposed) {
        viewer.dispose();
        return;
      }
      diffViewerRef.current = viewer;
      viewer.setFontSize(codeFontSizeForScale(fontScalePercentRef.current));
      viewer.setDiff(selectedDiffRef.current);
    });
    return () => {
      disposed = true;
      diffViewerRef.current?.dispose();
      diffViewerRef.current = undefined;
    };
  }, [runtime, hasDiffs]);

  useEffect(() => {
    diffViewerRef.current?.setDiff(selectedDiff);
  }, [selectedDiff]);

  useEffect(() => {
    if (!state.sessionId || !terminalHostRef.current) {
      return;
    }
    let disposed = false;
    terminalRevisionGateRef.current.reset();
    void runtime.mountTerminal(terminalHostRef.current).then((viewer) => {
      if (disposed) {
        viewer.dispose();
        return;
      }
      terminalViewerRef.current = viewer;
      viewer.setFontSize(codeFontSizeForScale(fontScalePercentRef.current));
      viewer.replaceText(getTerminalSnapshot(terminalTranscriptRef.current));
      terminalRevisionGateRef.current.markSnapshot(terminalRevisionRef.current);
    });
    return () => {
      disposed = true;
      terminalViewerRef.current?.dispose();
      terminalViewerRef.current = undefined;
      terminalRevisionGateRef.current.reset();
    };
  }, [runtime, state.sessionId]);

  useEffect(() => {
    const viewer = terminalViewerRef.current;
    if (!viewer) {
      return;
    }
    const action = terminalRevisionGateRef.current.takeRevision(state.terminalRevision);
    if (action === "replace") {
      viewer.replaceText(getTerminalSnapshot(state.terminalTranscript));
    } else if (action === "append" && !viewer.appendText(state.terminalAppend)) {
      viewer.replaceText(getTerminalSnapshot(state.terminalTranscript));
    }
  }, [state.terminalRevision, state.terminalAppend, state.terminalTranscript]);

  useEffect(() => {
    if (activeTab === "changes") {
      // Monaco needs a layout pass after the panel becomes visible again.
      requestAnimationFrame(() => diffViewerRef.current?.layout());
    } else if (activeTab === "terminal") {
      requestAnimationFrame(() => terminalViewerRef.current?.fit());
    }
  }, [activeTab]);

  // Helpful auto-focus (like IDE inspectors), but never override a user pin.
  useEffect(() => {
    if (userPinnedTabRef.current) {
      previousDiffCountRef.current = state.diffs.length;
      previousPlanCountRef.current = state.plan.length;
      previousTerminalRevisionRef.current = state.terminalRevision;
      return;
    }

    if (state.diffs.length > previousDiffCountRef.current) {
      setActiveTab("changes");
    } else if (state.plan.length > previousPlanCountRef.current) {
      setActiveTab("plan");
    } else if (state.terminalRevision > previousTerminalRevisionRef.current &&
        state.terminalTranscript.characterCount > 0) {
      setActiveTab("terminal");
    }

    previousDiffCountRef.current = state.diffs.length;
    previousPlanCountRef.current = state.plan.length;
    previousTerminalRevisionRef.current = state.terminalRevision;
  }, [
    state.diffs.length,
    state.plan.length,
    state.terminalRevision,
    state.terminalTranscript.characterCount
  ]);

  // New engine session: clear pin so auto-focus can help again.
  useEffect(() => {
    userPinnedTabRef.current = false;
    previousDiffCountRef.current = 0;
    previousPlanCountRef.current = 0;
    previousTerminalRevisionRef.current = 0;
    setActiveTab("changes");
    setSelectedPath(undefined);
  }, [state.sessionId]);

  useEffect(() => {
    const layout = () => {
      if (activeTab === "changes") {
        diffViewerRef.current?.layout();
      } else if (activeTab === "terminal") {
        terminalViewerRef.current?.fit();
      }
    };
    window.addEventListener("resize", layout);
    return () => window.removeEventListener("resize", layout);
  }, [activeTab]);

  function moveTab(currentTab: InspectorTab, event: React.KeyboardEvent<HTMLButtonElement>) {
    const currentIndex = inspectorTabOrder.indexOf(currentTab);
    const nextIndex = event.key === "ArrowRight"
      ? (currentIndex + 1) % inspectorTabOrder.length
      : event.key === "ArrowLeft"
        ? (currentIndex - 1 + inspectorTabOrder.length) % inspectorTabOrder.length
        : event.key === "Home"
          ? 0
          : event.key === "End"
            ? inspectorTabOrder.length - 1
            : -1;
    if (nextIndex < 0) {
      return;
    }

    event.preventDefault();
    const nextTab = inspectorTabOrder[nextIndex];
    selectTab(nextTab);
    tabRefs.current[nextTab]?.focus();
  }

  return (
    <main className="inspector-surface" aria-label={t("inspector")}>
      <div className="inspector-tabs" role="tablist" aria-label={t("inspectorViews")}>
        <InspectorTabButton
          active={activeTab === "changes"}
          buttonRef={(button) => { tabRefs.current.changes = button; }}
          label={t("changes")}
          count={state.diffs.length || undefined}
          onClick={() => selectTab("changes")}
          onKeyDown={(event) => moveTab("changes", event)}
        ><FileDiff size={15} /></InspectorTabButton>
        <InspectorTabButton
          active={activeTab === "terminal"}
          buttonRef={(button) => { tabRefs.current.terminal = button; }}
          label={t("terminal")}
          onClick={() => selectTab("terminal")}
          onKeyDown={(event) => moveTab("terminal", event)}
        ><TerminalSquare size={15} /></InspectorTabButton>
        <InspectorTabButton
          active={activeTab === "plan"}
          buttonRef={(button) => { tabRefs.current.plan = button; }}
          label={t("plan")}
          count={state.plan.length || undefined}
          onClick={() => selectTab("plan")}
          onKeyDown={(event) => moveTab("plan", event)}
        ><ListChecks size={15} /></InspectorTabButton>
      </div>

      <section className="inspector-content">
        {/*
          Keep a single changes panel shell so empty-state classes never fight
          panel visibility (`.inspector-empty { display:grid }` must stay nested).
        */}
        <div
          className={`inspector-panel changes-pane${
            state.diffs.length > 0 ? " changes-layout" : ""
          }${activeTab === "changes" ? " is-active" : ""}`}
          role="tabpanel"
          aria-hidden={activeTab !== "changes"}
          data-testid="inspector-panel-changes"
        >
          {state.diffs.length === 0 ? (
            <div className="inspector-empty">{t("noChangesToReview")}</div>
          ) : (
            <>
              <div className="inspector-file-list" aria-label={t("changedFiles")}>
                {state.diffs.map((diff) => (
                  <button
                    key={diff.path}
                    type="button"
                    className={`inspector-file${diff.path === effectiveSelectedPath ? " active" : ""}`}
                    aria-label={diff.path}
                    onClick={() => setSelectedPath(diff.path)}
                  >
                    <FileCode2 size={15} />
                    <span title={diff.path}>{diff.path}</span>
                  </button>
                ))}
              </div>
              <div className="diff-viewer" ref={diffHostRef} aria-label={t("codeDiff")} />
            </>
          )}
        </div>

        <div
          className={`inspector-panel terminal-pane${activeTab === "terminal" ? " is-active" : ""}`}
          role="tabpanel"
          aria-hidden={activeTab !== "terminal"}
          data-testid="inspector-panel-terminal"
        >
          {state.terminalTranscript.characterCount === 0 && (
            <div className="inspector-empty overlay">{t("noTerminalOutput")}</div>
          )}
          <div className="terminal-viewer" ref={terminalHostRef} aria-label={t("terminalOutput")} />
        </div>

        <div
          className={`inspector-panel plan-pane${activeTab === "plan" ? " is-active" : ""}`}
          role="tabpanel"
          aria-hidden={activeTab !== "plan"}
          data-testid="inspector-panel-plan"
        >
          {state.plan.length === 0 ? (
            <div className="inspector-empty">{t("noPlanGenerated")}</div>
          ) : (
            <ol className="plan-list">
              {state.plan.map((entry, index) => (
                <PlanRow key={`${index}-${entry.content}`} entry={entry} t={t} />
              ))}
            </ol>
          )}
        </div>
      </section>
    </main>
  );
}

function InspectorTabButton({
  active,
  buttonRef,
  label,
  count,
  onClick,
  onKeyDown,
  children
}: {
  active: boolean;
  buttonRef: (button: HTMLButtonElement | null) => void;
  label: string;
  count?: number;
  onClick: () => void;
  onKeyDown: (event: React.KeyboardEvent<HTMLButtonElement>) => void;
  children: React.ReactNode;
}) {
  return (
    <button
      type="button"
      className={`inspector-tab${active ? " active" : ""}`}
      ref={buttonRef}
      role="tab"
      aria-label={label}
      aria-selected={active}
      tabIndex={active ? 0 : -1}
      onClick={onClick}
      onKeyDown={onKeyDown}
    >
      {children}
      <span className="tab-label">{label}</span>
      {count !== undefined && <span className="tab-count" aria-hidden="true">{count}</span>}
    </button>
  );
}

function PlanRow({
  entry,
  t
}: {
  entry: InspectorPlanEntry;
  t: (key: string) => string;
}) {
  const status = planStatus(entry.status, t);
  return (
    <li className={`plan-row ${entry.status}`}>
      <span className="plan-state" aria-hidden="true">
        {entry.status === "completed" && <CheckCircle2 size={16} />}
        {entry.status === "in_progress" && <LoaderCircle size={16} />}
        {entry.status === "pending" && <Circle size={16} />}
      </span>
      <span className="plan-copy">
        <span>{entry.content}</span>
        <small>{status}</small>
      </span>
      <span className={`plan-priority ${entry.priority}`}>{priorityLabel(entry.priority, t)}</span>
    </li>
  );
}

function planStatus(
  status: InspectorPlanEntry["status"],
  t: (key: string) => string
): string {
  return t(`planStatus_${status}`);
}

function priorityLabel(
  priority: InspectorPlanEntry["priority"],
  t: (key: string) => string
): string {
  return t(`priority_${priority}`);
}
