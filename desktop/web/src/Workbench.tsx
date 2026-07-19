import {
  Archive,
  ArchiveRestore,
  Bot,
  Brain,
  Check,
  Cloud,
  ChevronDown,
  Database,
  Download,
  Eye,
  FolderKanban,
  GitBranch,
  GitFork,
  GitMerge,
  GitPullRequest,
  History,
  KeyRound,
  ListTodo,
  LoaderCircle,
  Minimize2,
  Pencil,
  Paperclip,
  Play,
  Plus,
  Power,
  Puzzle,
  RefreshCw,
  Search,
  Send,
  Server,
  Settings,
  ShieldAlert,
  ShieldCheck,
  Square,
  TerminalSquare,
  Trash2,
  Upload,
  X
} from "lucide-react";
import { useEffect, useRef, useState } from "react";
import ReactMarkdown from "react-markdown";
import remarkGfm from "remark-gfm";
import { createTranslator } from "./i18n";
import {
  defaultHostBridge,
  type BackgroundTaskSnapshot,
  type CloudAutomation,
  type CloudHandoffImport,
  type CloudOperation,
  type EngineStatus,
  type ExecutionProfile,
  type ExtensionScope,
  type ExtensionsCatalog,
  type HostBridge,
  type HostCommand,
  type HostEvent,
  type ImageAttachmentError,
  type MaintenanceOperation,
  type MemoryCapabilities,
  type MemoryFile,
  type MemoryOperation,
  type PromptAttachment,
  type ProviderBackend,
  type RuntimeCommand,
  type SessionRewindMode,
  type SessionRewindPoint,
  type SessionMode,
  type SessionSummary,
  type SubagentSnapshot,
  type UiLanguage,
  type WindowsAutomationAction,
  type WorkspaceContextFile,
  type WorktreeConflict,
  type WorktreeOperation,
  type WorktreeRecord
} from "./hostBridge";
import enUs from "./locales/en-US.json";
import zhCn from "./locales/zh-CN.json";
import { VirtualizedList } from "./VirtualizedList";
import "./styles.css";

const defaultProviderBaseUrl = "https://api.x.ai/v1";
const defaultProviderModel = "grok-build";
const sessionPageSize = 50;
const sessionListRowHeight = 60;
const sessionListOverscan = 4;
const createdWorktreeRefreshAttempts = 6;
const createdWorktreeRefreshIntervalMs = 500;
const workspaceContentByteLimit = 512 * 1024;
const memoryContentByteLimit = 64 * 1024;
const worktreeReviewRequestMaxLength = 16 * 1024;
const worktreeReviewIdMaxLength = 512;
const worktreeReviewPathMaxLength = 4096;
const worktreeReviewBaseReferenceMaxLength = 512;

type LiveMessage = {
  type: "message";
  id: string;
  role: "user" | "assistant";
  text: string;
  streaming?: boolean;
};

type ToolTimelineEntry = {
  type: "tool";
  id: string;
  toolCallId: string;
  title: string;
  kind: string;
  status: string;
  rawInput?: unknown;
};

type ConversationEntry = LiveMessage | ToolTimelineEntry;

type PermissionRequestEvent = Extract<HostEvent, { type: "permission/requested" }>;
type SessionRewoundEvent = Extract<HostEvent, { type: "session/rewound" }>;
type SessionListStatus = "loading" | "loaded" | "error";
type SessionListRequest = { requestId: string; mode: "append" | "replace" };
type SessionRenameFocusTarget = { sessionId: string; target: "row" | "rename" };
type SessionOperationRequest = { operation: "rename" | "archive"; sessionId: string };
type WorkbenchSurface = "conversation" | "agents" | "worktrees";
type RuntimeDashboardStatus = "idle" | "loading" | "loaded" | "error";
type WorktreeStatus = "idle" | "loading" | "loaded" | "error";
type MaintenanceRequest = { requestId: string; operation: MaintenanceOperation };
type MaintenanceNotice = { kind: "status" | "error"; text: string };
type WindowsAutomationPending = {
  requestId: string;
  action: WindowsAutomationAction;
  processId: number;
};
type WorktreeCreateDraft = {
  copyMode: "clean" | "dirty";
  creationType: "linked" | "standalone" | "git";
  gitReference: string;
  label: string;
  destinationPath: string;
};
type WorktreeGcPreview = {
  deadRemoved: number;
  expiredRemoved: number;
  skippedAlive: number;
  removeFailed: number;
};
type CreatedWorktreeRefresh = {
  workspaceGeneration: number;
  worktreePath: string;
  attemptsRemaining: number;
  timeout?: number;
};
type WorktreeReviewDraft = {
  worktreeId: string;
  workspaceGeneration: number;
  request: string;
};
type PromptSubmission = {
  text: string;
  attachments: PromptAttachment[];
  source: "composer" | "worktree-review";
  worktreeId?: string;
  workspaceGeneration?: number;
};
type AgentsEditorStatus = "idle" | "loading" | "ready" | "saving" | "error";
type FileSearchStatus = "idle" | "loading" | "loaded" | "empty" | "error";
type MemoryBrowserStatus = "idle" | "loading" | "ready" | "mutating" | "error";
type MemoryPendingRequest = {
  requestId: string;
  operation: MemoryOperation;
  fileId?: string;
  content?: string;
  confirmed?: boolean;
};
type MemoryMutationChallenge = {
  operation: "write" | "delete";
  fileId: string;
  content?: string;
  message: string;
  confirmationToken: string;
};

let nextLiveMessageId = 0;

const focusableSelector = [
  "a[href]",
  "button:not([disabled])",
  "input:not([disabled])",
  "select:not([disabled])",
  "textarea:not([disabled])",
  "[tabindex]:not([tabindex='-1'])"
].join(",");

function canRestoreFocus(target: HTMLElement | null): target is HTMLElement {
  return target !== null
    && target !== document.body
    && target.isConnected
    && !target.matches(":disabled")
    && !target.closest("[inert]");
}

function focusableElements(container: HTMLElement): HTMLElement[] {
  return Array.from(container.querySelectorAll<HTMLElement>(focusableSelector)).filter(
    (element) => !element.hasAttribute("hidden") && element.getAttribute("aria-hidden") !== "true"
  );
}

function sameWorkspacePath(left: string, right: string): boolean {
  return left.length === right.length && left.toUpperCase() === right.toUpperCase();
}

function createMaintenanceRequestId(): string {
  if (typeof globalThis.crypto.randomUUID === "function") {
    return globalThis.crypto.randomUUID();
  }
  const bytes = globalThis.crypto.getRandomValues(new Uint8Array(16));
  bytes[6] = (bytes[6] & 0x0f) | 0x40;
  bytes[8] = (bytes[8] & 0x3f) | 0x80;
  const hex = Array.from(bytes, (value) => value.toString(16).padStart(2, "0")).join("");
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}

function trailingFileReferenceQuery(value: string): string | undefined {
  const match = /(?:^|\s)@([^\s@]{1,512})$/u.exec(value);
  return match?.[1];
}

function removeTrailingFileReference(value: string): string {
  const match = /(?:^|\s)@([^\s@]{1,512})$/u.exec(value);
  return match ? value.slice(0, match.index).trimEnd() : value;
}

function appendFileReferences(text: string, relativePaths: readonly string[]): string {
  if (relativePaths.length === 0) {
    return text;
  }
  const references = relativePaths.map((relativePath) => `@${relativePath}`).join("\n");
  return text ? `${text}\n\n${references}` : references;
}

function formatFileSize(byteLength: number): string {
  if (byteLength < 1024) {
    return `${byteLength} B`;
  }
  return `${(byteLength / 1024).toFixed(byteLength < 10 * 1024 ? 1 : 0)} KiB`;
}

export function Workbench({ bridge = defaultHostBridge }: { bridge?: HostBridge }) {
  const previewMode = !bridge.available;
  const [language, setLanguage] = useState<UiLanguage>("zh-CN");
  const t = createTranslator(enUs, language === "zh-CN" ? zhCn : enUs);
  const [prompt, setPrompt] = useState("");
  const [executionProfile, setExecutionProfile] = useState<ExecutionProfile>("NativeProtected");
  const [executionProfiles, setExecutionProfiles] = useState<ExecutionProfile[]>(
    previewMode ? ["NativeProtected", "WslStrict"] : ["NativeProtected"]
  );
  const [sessionMode, setSessionMode] = useState<SessionMode>("default");
  const [confirmedSessionMode, setConfirmedSessionMode] = useState<SessionMode>("default");
  const [planAvailable, setPlanAvailable] = useState<boolean | undefined>(
    previewMode ? true : undefined
  );
  const [modeConfirmationPending, setModeConfirmationPending] = useState(false);
  const [wslStrictReason, setWslStrictReason] = useState("");
  const [nativeRiskAcknowledged, setNativeRiskAcknowledged] = useState(false);
  const [pendingNativePrompt, setPendingNativePrompt] = useState<PromptSubmission>();
  const [settingsOpen, setSettingsOpen] = useState(false);
  const [providerBaseUrl, setProviderBaseUrl] = useState(defaultProviderBaseUrl);
  const [providerModel, setProviderModel] = useState(defaultProviderModel);
  const [providerBackend, setProviderBackend] = useState<ProviderBackend>("chat_completions");
  const [allowInsecureTransport, setAllowInsecureTransport] = useState(false);
  const [providerCredentialBaseUrl, setProviderCredentialBaseUrl] = useState<string>();
  const [providerStatus, setProviderStatus] = useState<"loaded" | "saved" | "error">();
  const [providerMessage, setProviderMessage] = useState("");
  const [replaceProviderCredential, setReplaceProviderCredential] = useState(false);
  const [engineStatus, setEngineStatus] = useState<EngineStatus>("idle");
  const [engineMessage, setEngineMessage] = useState("");
  const [activeSessionId, setActiveSessionId] = useState<string>();
  const [workspaceReady, setWorkspaceReady] = useState(previewMode);
  const [workspaceGeneration, setWorkspaceGeneration] = useState(previewMode ? 1 : 0);
  const [workspacePath, setWorkspacePath] = useState(
    previewMode ? "agentdesk-alpha / desktop" : "");
  const [conversationEntries, setConversationEntries] = useState<ConversationEntry[]>([]);
  const [sessions, setSessions] = useState<SessionSummary[]>([]);
  const [sessionQuery, setSessionQuery] = useState("");
  const [archivedSessionsView, setArchivedSessionsView] = useState(false);
  const [archivingSessionIds, setArchivingSessionIds] = useState<Set<string>>(new Set());
  const [forkingSessionIds, setForkingSessionIds] = useState<Set<string>>(new Set());
  const [compactingSessionId, setCompactingSessionId] = useState<string>();
  const [sessionListStatus, setSessionListStatus] = useState<SessionListStatus>(
    previewMode ? "loaded" : "loading"
  );
  const [sessionListError, setSessionListError] = useState("");
  const [nextSessionCursor, setNextSessionCursor] = useState<string>();
  const [loadingMoreSessions, setLoadingMoreSessions] = useState(false);
  const [editingSessionId, setEditingSessionId] = useState<string>();
  const [sessionTitleDraft, setSessionTitleDraft] = useState("");
  const [historyStatus, setHistoryStatus] = useState("");
  const [rewindOpen, setRewindOpen] = useState(false);
  const [rewindPoints, setRewindPoints] = useState<SessionRewindPoint[]>([]);
  const [rewindPointsLoading, setRewindPointsLoading] = useState(false);
  const [selectedRewindPoint, setSelectedRewindPoint] = useState<number>();
  const [rewindMode, setRewindMode] = useState<SessionRewindMode>("all");
  const [rewindSubmitting, setRewindSubmitting] = useState(false);
  const [rewindFailure, setRewindFailure] = useState<SessionRewoundEvent>();
  const [rewindError, setRewindError] = useState("");
  const [permissionQueue, setPermissionQueue] = useState<PermissionRequestEvent[]>([]);
  const [activeSurface, setActiveSurface] = useState<WorkbenchSurface>("conversation");
  const [backgroundTasks, setBackgroundTasks] = useState<BackgroundTaskSnapshot[]>([]);
  const [runningSubagents, setRunningSubagents] = useState<SubagentSnapshot[]>([]);
  const [runtimeDashboardStatus, setRuntimeDashboardStatus] =
    useState<RuntimeDashboardStatus>("idle");
  const [runtimeDashboardError, setRuntimeDashboardError] = useState("");
  const [runtimeActionStatus, setRuntimeActionStatus] = useState("");
  const [pendingTaskKills, setPendingTaskKills] = useState<Set<string>>(new Set());
  const [pendingSubagentCancels, setPendingSubagentCancels] = useState<Set<string>>(new Set());
  const [selectedSubagent, setSelectedSubagent] = useState<SubagentSnapshot>();
  const [worktrees, setWorktrees] = useState<WorktreeRecord[]>([]);
  const [selectedWorktree, setSelectedWorktree] = useState<WorktreeRecord>();
  const [worktreeStatus, setWorktreeStatus] = useState<WorktreeStatus>("idle");
  const [worktreeError, setWorktreeError] = useState("");
  const [worktreeActionStatus, setWorktreeActionStatus] = useState("");
  const [worktreeConflicts, setWorktreeConflicts] = useState<WorktreeConflict[]>([]);
  const [worktreeOperation, setWorktreeOperation] = useState<WorktreeOperation>();
  const [worktreeCreateOpen, setWorktreeCreateOpen] = useState(false);
  const [worktreeCreateDraft, setWorktreeCreateDraft] = useState<WorktreeCreateDraft>({
    copyMode: "dirty",
    creationType: "linked",
    gitReference: "HEAD",
    label: "",
    destinationPath: ""
  });
  const [worktreeApplyTarget, setWorktreeApplyTarget] = useState<WorktreeRecord>();
  const [worktreeApplyMode, setWorktreeApplyMode] = useState<"merge" | "overwrite">("merge");
  const [worktreeGcOpen, setWorktreeGcOpen] = useState(false);
  const [worktreeGcPreview, setWorktreeGcPreview] = useState<WorktreeGcPreview>();
  const [worktreeGcExecuting, setWorktreeGcExecuting] = useState(false);
  const [worktreeReviewDraft, setWorktreeReviewDraft] = useState<WorktreeReviewDraft>();
  const [imagePrompts, setImagePrompts] = useState(previewMode);
  const [pendingAttachments, setPendingAttachments] = useState<PromptAttachment[]>([]);
  const [attachmentError, setAttachmentError] = useState<ImageAttachmentError>();
  const [attachmentSelectionPending, setAttachmentSelectionPending] = useState(false);
  const [runtimeCommands, setRuntimeCommands] = useState<RuntimeCommand[]>([]);
  const [runtimeCommandsError, setRuntimeCommandsError] = useState("");
  const [agentsFiles, setAgentsFiles] = useState<WorkspaceContextFile[]>([]);
  const [selectedAgentsPath, setSelectedAgentsPath] = useState("");
  const [agentsContent, setAgentsContent] = useState("");
  const [savedAgentsContent, setSavedAgentsContent] = useState("");
  const [agentsEditorStatus, setAgentsEditorStatus] = useState<AgentsEditorStatus>("idle");
  const [agentsNotice, setAgentsNotice] = useState("");
  const [fileSearchResults, setFileSearchResults] = useState<WorkspaceContextFile[]>([]);
  const [fileSearchStatus, setFileSearchStatus] = useState<FileSearchStatus>("idle");
  const [fileReferenceIndex, setFileReferenceIndex] = useState(0);
  const [referencedFiles, setReferencedFiles] = useState<string[]>([]);
  const [commandPaletteIndex, setCommandPaletteIndex] = useState(0);
  const [commandPaletteDismissed, setCommandPaletteDismissed] = useState(false);
  const [memoryStatus, setMemoryStatus] = useState<"idle" | "running" | "succeeded" | "error">(
    "idle"
  );
  const [memoryMessage, setMemoryMessage] = useState("");
  const [memoryCapabilities, setMemoryCapabilities] = useState<MemoryCapabilities>();
  const [memoryFiles, setMemoryFiles] = useState<MemoryFile[]>([]);
  const [selectedMemoryFileId, setSelectedMemoryFileId] = useState("");
  const [memoryContent, setMemoryContent] = useState("");
  const [savedMemoryContent, setSavedMemoryContent] = useState("");
  const [memoryBrowserStatus, setMemoryBrowserStatus] =
    useState<MemoryBrowserStatus>("idle");
  const [memoryBrowserNotice, setMemoryBrowserNotice] = useState<MaintenanceNotice>();
  const [memoryListingTruncated, setMemoryListingTruncated] = useState(false);
  const [memoryMutationChallenge, setMemoryMutationChallenge] =
    useState<MemoryMutationChallenge>();
  const [preferencesHydrated, setPreferencesHydrated] = useState(false);
  const [notificationsEnabled, setNotificationsEnabled] = useState(false);
  const [windowsAutomationEnabled, setWindowsAutomationEnabled] = useState(false);
  const [backgroundUpdateChecksEnabled, setBackgroundUpdateChecksEnabled] = useState(false);
  const [windowsAutomationHostEnabled, setWindowsAutomationHostEnabled] = useState(false);
  const [windowsAutomationAction, setWindowsAutomationAction] =
    useState<WindowsAutomationAction>("focus-window");
  const [windowsAutomationProcessId, setWindowsAutomationProcessId] = useState("");
  const [windowsAutomationId, setWindowsAutomationId] = useState("");
  const [windowsAutomationName, setWindowsAutomationName] = useState("");
  const [windowsAutomationPending, setWindowsAutomationPending] =
    useState<WindowsAutomationPending>();
  const [windowsAutomationNotice, setWindowsAutomationNotice] =
    useState<MaintenanceNotice>();
  const [restartRequired, setRestartRequired] = useState(false);
  const [maintenanceRequest, setMaintenanceRequest] = useState<MaintenanceRequest>();
  const [maintenanceNotice, setMaintenanceNotice] = useState<MaintenanceNotice>();
  const [cloudPushNotice, setCloudPushNotice] = useState<string>();
  const [stagedUpdateVersion, setStagedUpdateVersion] = useState<string>();
  const [updatesUnsupported, setUpdatesUnsupported] = useState(false);
  const translatorRef = useRef(t);
  translatorRef.current = t;
  const composerRef = useRef<HTMLTextAreaElement>(null);
  const pendingAttachmentsRef = useRef<PromptAttachment[]>([]);
  const attachmentRequestIdRef = useRef<string | undefined>(undefined);
  const executeModeRef = useRef<HTMLButtonElement>(null);
  const planModeRef = useRef<HTMLButtonElement>(null);
  const sessionSearchRef = useRef<HTMLInputElement>(null);
  const activeSessionsTabRef = useRef<HTMLButtonElement>(null);
  const archivedSessionsTabRef = useRef<HTMLButtonElement>(null);
  const renameInputRef = useRef<HTMLInputElement>(null);
  const renameReturnFocusRef = useRef<SessionRenameFocusTarget | null>(null);
  const sendButtonRef = useRef<HTMLButtonElement>(null);
  const settingsButtonRef = useRef<HTMLButtonElement>(null);
  const settingsDialogRef = useRef<HTMLElement>(null);
  const settingsInitialFocusRef = useRef<HTMLInputElement>(null);
  const windowsAutomationValueRef = useRef<HTMLInputElement>(null);
  const windowsAutomationPendingRef = useRef<WindowsAutomationPending | undefined>(undefined);
  const nativeRiskDialogRef = useRef<HTMLElement>(null);
  const nativeRiskCancelButtonRef = useRef<HTMLButtonElement>(null);
  const rewindDialogRef = useRef<HTMLElement>(null);
  const rewindCancelButtonRef = useRef<HTMLButtonElement>(null);
  const permissionDialogRef = useRef<HTMLElement>(null);
  const permissionCancelButtonRef = useRef<HTMLButtonElement>(null);
  const modalReturnFocusRef = useRef<HTMLElement | null>(null);
  const workspaceGenerationRef = useRef(previewMode ? 1 : 0);
  const workspacePathRef = useRef(previewMode ? "agentdesk-alpha / desktop" : "");
  const executionProfilesRef = useRef<ExecutionProfile[]>(
    previewMode ? ["NativeProtected", "WslStrict"] : ["NativeProtected"]
  );
  const sessionModesRef = useRef<SessionMode[]>(previewMode ? ["default", "plan"] : ["default"]);
  const sessionModeRef = useRef<SessionMode>("default");
  const activeSessionIdRef = useRef<string | undefined>(undefined);
  const activeEngineEpochRef = useRef(0);
  const sessionQueryRef = useRef("");
  const archivedSessionsViewRef = useRef(false);
  const archivingSessionIdsRef = useRef(new Set<string>());
  const sessionOperationRequestsRef = useRef(new Map<string, SessionOperationRequest>());
  const forkingSessionIdsRef = useRef(new Set<string>());
  const pendingForkProfilesRef = useRef(new Map<string, ExecutionProfile>());
  const compactingSessionIdRef = useRef<string | undefined>(undefined);
  const rewindSessionIdRef = useRef<string | undefined>(undefined);
  const rewindPointsLoadingRef = useRef(false);
  const rewindSubmittingRef = useRef(false);
  const sessionListStatusRef = useRef<SessionListStatus>(previewMode ? "loaded" : "loading");
  const sessionListRequestRef = useRef<SessionListRequest | undefined>(undefined);
  const runtimeDashboardRequestSessionRef = useRef<string | undefined>(undefined);
  const worktreeOperationRef = useRef<WorktreeOperation | undefined>(undefined);
  const createdWorktreeRefreshRef = useRef<CreatedWorktreeRefresh | undefined>(undefined);
  const worktreeGcOpenRef = useRef(false);
  const worktreeGcExecutingRef = useRef(false);
  const worktreeReviewDraftRef = useRef<WorktreeReviewDraft | undefined>(undefined);
  const maintenanceRequestRef = useRef<MaintenanceRequest | undefined>(undefined);
  const agentsListRequestRef = useRef<string | undefined>(undefined);
  const agentsReadRequestRef = useRef<string | undefined>(undefined);
  const agentsWriteRequestRef = useRef<string | undefined>(undefined);
  const agentsWriteContentRef = useRef("");
  const fileSearchRequestRef = useRef<string | undefined>(undefined);
  const memoryCapabilitiesRef = useRef<MemoryCapabilities | undefined>(undefined);
  const memoryCapabilitiesSessionRef = useRef<string | undefined>(undefined);
  const memoryFilesRef = useRef<MemoryFile[]>([]);
  const memoryPendingRequestRef = useRef<MemoryPendingRequest | undefined>(undefined);
  const firstSessionQueryRef = useRef(true);
  const closedSessionIdsRef = useRef(new Set<string>());
  const activePermission = permissionQueue[0];
  const nativeRiskOpen = pendingNativePrompt !== undefined;
  const worktreeModalOpen = worktreeCreateOpen || worktreeApplyTarget !== undefined ||
    worktreeGcOpen;
  const visiblePermission = settingsOpen || nativeRiskOpen || rewindOpen || worktreeModalOpen
    ? undefined
    : activePermission;
  const interactionBlocked = settingsOpen || nativeRiskOpen || rewindOpen || worktreeModalOpen ||
    visiblePermission !== undefined;
  const modalKind = settingsOpen
    ? "settings"
    : nativeRiskOpen
      ? "nativeRisk"
      : rewindOpen
        ? "rewind"
        : worktreeModalOpen
          ? "worktree"
        : visiblePermission
          ? "permission"
          : undefined;
  const firstUserMessage = conversationEntries.find(
    (entry): entry is LiveMessage => entry.type === "message" && entry.role === "user"
  )?.text;
  const activeSession = sessions.find((session) => session.sessionId === activeSessionId);
  const currentTaskTitle = activeSession?.title || firstUserMessage || t("newTask");
  const nativeAvailable = executionProfiles.includes("NativeProtected");
  const wslAvailable = executionProfiles.includes("WslStrict");
  const profileSelectionDisabled = engineStatus === "starting" || engineStatus === "running";
  const modeSelectionDisabled = profileSelectionDisabled || modeConfirmationPending;
  const planSelectable = planAvailable !== false;
  const modeAwaitingConfirmation = sessionMode !== confirmedSessionMode;
  const modeStatusMessage = modeConfirmationPending
    ? t(sessionMode === "plan" ? "confirmingPlanMode" : "confirmingExecuteMode")
    : modeAwaitingConfirmation
      ? t(sessionMode === "plan" ? "planModePending" : "executeModePending")
      : "";
  const wslUnavailableMessage = wslStrictReason || t("wslUnavailable");
  const providerValidationMessage = validateProviderUrl(
    providerBaseUrl,
    allowInsecureTransport,
    t("providerUrlInvalid"),
    t("providerHttpOptInRequired")
  );
  const canReuseProviderCredential = providerCredentialBaseUrl !== undefined
    && providerBaseUrl.trim() === providerCredentialBaseUrl;
  const providerFormReady = providerBaseUrl.trim().length > 0
    && providerModel.trim().length > 0
    && providerValidationMessage === undefined;
  const promptBusy = engineStatus === "starting" || engineStatus === "running";
  const maintenanceBusy = maintenanceRequest !== undefined;
  const maintenanceActionsDisabled = promptBusy || maintenanceBusy;
  const windowsAutomationProcessIdValue = parseWindowsProcessId(windowsAutomationProcessId);
  const windowsAutomationSelectorReady = windowsAutomationAction === "focus-window" ||
    validWindowsAutomationTarget(windowsAutomationId) ||
    validWindowsAutomationTarget(windowsAutomationName);
  const windowsAutomationExecuteReady = bridge.available && windowsAutomationHostEnabled &&
    windowsAutomationPending === undefined && windowsAutomationProcessIdValue !== undefined &&
    windowsAutomationSelectorReady;
  const slashQuery = prompt.startsWith("/") && !/\s/.test(prompt.slice(1))
    ? prompt.slice(1).toLocaleLowerCase()
    : undefined;
  const commandMatches = slashQuery !== undefined && !commandPaletteDismissed
    ? runtimeCommands.filter((command) => command.name.toLocaleLowerCase().includes(slashQuery) ||
      command.description.toLocaleLowerCase().includes(slashQuery))
    : [];
  const commandPaletteOpen = commandMatches.length > 0;
  const fileReferenceQuery = trailingFileReferenceQuery(prompt);
  const fileReferencePaletteOpen = fileSearchStatus === "loaded" &&
    fileSearchResults.length > 0 && fileReferenceQuery !== undefined;
  const agentsContentTooLarge = new TextEncoder().encode(agentsContent).byteLength >
    workspaceContentByteLimit;
  const memoryContentTooLarge = new TextEncoder().encode(memoryContent).byteLength >
    memoryContentByteLimit;
  const selectedMemoryFile = memoryFiles.find((file) => file.id === selectedMemoryFileId);
  const memoryCapabilitiesCurrent = activeSessionId !== undefined &&
    memoryCapabilitiesSessionRef.current === activeSessionId;
  const memoryCanBrowse = memoryCapabilitiesCurrent && memoryCapabilities?.list === true &&
    memoryCapabilities.read === true;
  const memoryMutationsProtected = memoryCapabilitiesCurrent &&
    memoryCapabilities?.mutationConfirmationRequired === true;
  const memoryCanWrite = memoryMutationsProtected && memoryCapabilities?.write === true &&
    selectedMemoryFile?.writable === true;
  const memoryCanDelete = memoryMutationsProtected && memoryCapabilities?.delete === true &&
    selectedMemoryFile?.writable === true;

  useEffect(() => {
    bridge.send({ type: "ui/ready" });
    return bridge.subscribe((event) => handleHostEvent(event));
  }, [bridge]);

  useEffect(() => {
    if (!bridge.available || !workspaceReady || workspaceGeneration <= 0 ||
        fileReferenceQuery === undefined) {
      fileSearchRequestRef.current = undefined;
      setFileSearchResults([]);
      setFileSearchStatus("idle");
      setFileReferenceIndex(0);
      return;
    }

    fileSearchRequestRef.current = undefined;
    setFileSearchResults([]);
    setFileSearchStatus("loading");
    setFileReferenceIndex(0);
    const timeout = window.setTimeout(() => {
      const requestId = createMaintenanceRequestId();
      fileSearchRequestRef.current = requestId;
      bridge.send({
        type: "workspace/context/file/search",
        requestId,
        workspaceGeneration,
        query: fileReferenceQuery
      });
    }, 200);
    return () => window.clearTimeout(timeout);
  }, [bridge, fileReferenceQuery, workspaceGeneration, workspaceReady]);

  useEffect(() => {
    if (!preferencesHydrated || !bridge.available) {
      return;
    }
    const timeout = window.setTimeout(() => {
      bridge.send({
        type: "ui/preferences/save",
        language,
        composerDraft: prompt,
        sessionMode,
        executionProfile,
        notificationsEnabled,
        windowsAutomationEnabled,
        backgroundUpdateChecksEnabled
      });
    }, 250);
    return () => window.clearTimeout(timeout);
  }, [
    bridge,
    preferencesHydrated,
    language,
    prompt,
    sessionMode,
    executionProfile,
    notificationsEnabled,
    windowsAutomationEnabled,
    backgroundUpdateChecksEnabled
  ]);

  useEffect(() => {
    if (previewMode) {
      updateSessionListStatus("loaded");
      return;
    }
    if (!workspaceReady) {
      firstSessionQueryRef.current = false;
      return;
    }
    if (firstSessionQueryRef.current) {
      firstSessionQueryRef.current = false;
      requestSessionList();
      return;
    }

    const timeout = window.setTimeout(() => requestSessionList(), 250);
    return () => window.clearTimeout(timeout);
  }, [bridge, previewMode, sessionQuery]);

  useEffect(() => {
    if (activeSurface !== "agents") {
      return;
    }
    if (!activeSessionId || !bridge.available) {
      setBackgroundTasks([]);
      setRunningSubagents([]);
      setRuntimeDashboardError("");
      setRuntimeDashboardStatus("loaded");
      return;
    }

    const pollingSessionId = activeSessionId;
    requestRuntimeDashboard();
    const interval = window.setInterval(requestRuntimeDashboard, 2000);
    return () => {
      window.clearInterval(interval);
      if (runtimeDashboardRequestSessionRef.current === pollingSessionId) {
        runtimeDashboardRequestSessionRef.current = undefined;
      }
    };
  }, [activeSurface, activeSessionId, bridge]);

  useEffect(() => {
    if (activeSurface === "worktrees") {
      requestWorktrees();
    } else {
      clearCreatedWorktreeRefresh();
    }
  }, [activeSurface, bridge]);

  useEffect(() => () => clearCreatedWorktreeRefresh(), []);

  useEffect(() => {
    if (!modalKind) {
      return;
    }

    bridge.send({ type: "ui/modal", isOpen: true });
    return () => bridge.send({ type: "ui/modal", isOpen: false });
  }, [bridge, Boolean(modalKind)]);

  useEffect(() => {
    const focusSearch = (event: KeyboardEvent) => {
      if ((event.ctrlKey || event.metaKey) && event.key.toLocaleLowerCase() === "k" &&
          !interactionBlocked) {
        event.preventDefault();
        sessionSearchRef.current?.focus();
      }
    };
    document.addEventListener("keydown", focusSearch);
    return () => document.removeEventListener("keydown", focusSearch);
  }, [interactionBlocked]);

  useEffect(() => {
    if (editingSessionId) {
      queueMicrotask(() => {
        renameInputRef.current?.focus();
        renameInputRef.current?.select();
      });
      return;
    }
    const returnFocus = renameReturnFocusRef.current;
    if (!returnFocus) {
      return;
    }
    queueMicrotask(() => {
      const target = Array.from(document.querySelectorAll<HTMLElement>(
        "[data-session-focus-id][data-session-focus-target]"
      )).find((candidate) =>
        candidate.dataset.sessionFocusId === returnFocus.sessionId &&
        candidate.dataset.sessionFocusTarget === returnFocus.target);
      renameReturnFocusRef.current = null;
      if (target && canRestoreFocus(target)) {
        target.focus();
      }
    });
  }, [editingSessionId]);

  useEffect(() => {
    if (!modalKind) {
      return;
    }

    const activeDialog = modalKind === "settings"
      ? settingsDialogRef.current
      : modalKind === "nativeRisk"
        ? nativeRiskDialogRef.current
        : modalKind === "rewind"
          ? rewindDialogRef.current
          : modalKind === "worktree"
            ? document.querySelector<HTMLElement>(".worktree-operation-dialog")
            : permissionDialogRef.current;
    if (!activeDialog) {
      return;
    }

    const trapDialogFocus = (event: KeyboardEvent) => {
      if (event.key === "Escape") {
        event.preventDefault();
        if (modalKind === "settings") {
          closeSettings();
        } else if (modalKind === "nativeRisk") {
          closeNativeRisk();
        } else if (modalKind === "rewind") {
          closeRewind();
        } else if (modalKind === "worktree") {
          setWorktreeCreateOpen(false);
          setWorktreeApplyTarget(undefined);
          if (!worktreeGcExecutingRef.current) {
            worktreeGcOpenRef.current = false;
            setWorktreeGcOpen(false);
          }
        } else {
          respondToPermission("cancelled");
        }
        return;
      }
      if (event.key !== "Tab") {
        return;
      }

      const focusable = focusableElements(activeDialog);
      if (focusable.length === 0) {
        event.preventDefault();
        activeDialog.focus();
        return;
      }
      const first = focusable[0];
      const last = focusable.at(-1)!;
      const focused = document.activeElement;
      if (event.shiftKey && (focused === first || !activeDialog.contains(focused))) {
        event.preventDefault();
        last.focus();
      } else if (!event.shiftKey && (focused === last || !activeDialog.contains(focused))) {
        event.preventDefault();
        first.focus();
      }
    };
    document.addEventListener("keydown", trapDialogFocus);
    return () => document.removeEventListener("keydown", trapDialogFocus);
  }, [modalKind, visiblePermission?.requestId]);

  useEffect(() => {
    if (modalKind) {
      if (!modalReturnFocusRef.current && document.activeElement instanceof HTMLElement) {
        modalReturnFocusRef.current = document.activeElement;
      }
      const initialFocus = modalKind === "settings"
        ? settingsInitialFocusRef.current
        : modalKind === "nativeRisk"
          ? nativeRiskCancelButtonRef.current
          : modalKind === "rewind"
            ? rewindCancelButtonRef.current
            : modalKind === "worktree"
              ? document.querySelector<HTMLElement>(".worktree-operation-dialog")
                ?.querySelector<HTMLElement>(focusableSelector)
              : permissionCancelButtonRef.current;
      initialFocus?.focus();
      return;
    }

    if (modalReturnFocusRef.current) {
      const returnTarget = modalReturnFocusRef.current;
      modalReturnFocusRef.current = null;
      queueMicrotask(() => {
        const focusTarget = canRestoreFocus(returnTarget)
          ? returnTarget
          : canRestoreFocus(composerRef.current)
            ? composerRef.current
            : null;
        focusTarget?.focus();
      });
    }
  }, [modalKind, visiblePermission?.requestId]);

  function handleHostEvent(event: HostEvent) {
    switch (event.type) {
      case "engine/status":
        if (event.engineEpoch !== undefined) {
          if (event.engineEpoch < activeEngineEpochRef.current ||
              (event.engineEpoch === activeEngineEpochRef.current && event.sessionId &&
                activeSessionIdRef.current && event.sessionId !== activeSessionIdRef.current)) {
            break;
          }
          activeEngineEpochRef.current = event.engineEpoch;
        }
        setEngineStatus(event.status);
        setEngineMessage(event.message ?? "");
        if (event.capabilities) {
          const reportedProfiles = event.capabilities.executionProfiles;
          executionProfilesRef.current = reportedProfiles;
          setExecutionProfiles(reportedProfiles);
          setWslStrictReason(event.capabilities.wslStrictReason ?? "");
          setExecutionProfile((profile) => reportedProfiles.includes(profile)
            ? profile
            : reportedProfiles[0] ?? "NativeProtected");
        }
        if (event.sessionId) {
          updateActiveSessionId(event.sessionId);
        }
        if (event.status === "stopped" || event.status === "error") {
          resetMemoryBrowserState(false);
          updateModeConfirmationPending(false);
          setConfirmedSessionMode("default");
          updateArchivingSessionIds(new Set());
          sessionOperationRequestsRef.current.clear();
          updateForkingSessionIds(new Set());
          pendingForkProfilesRef.current.clear();
          updateCompactingSessionId(undefined);
          if (event.status === "error" && rewindSessionIdRef.current &&
              (rewindPointsLoadingRef.current || rewindSubmittingRef.current)) {
            updateRewindPointsLoading(false);
            updateRewindSubmitting(false);
            setRewindError(event.message || translatorRef.current("historyOperationError"));
          }
          if (event.sessionId) {
            closedSessionIdsRef.current.add(event.sessionId);
          }
          setPermissionQueue((requests) => event.sessionId
            ? requests.filter((request) => request.sessionId !== event.sessionId)
            : []);
        } else if (event.status === "running" && event.sessionId) {
          closedSessionIdsRef.current.delete(event.sessionId);
        }
        break;
      case "workspace/selected":
        const sameWorkspace = workspacePathRef.current.length > 0 &&
          sameWorkspacePath(event.path, workspacePathRef.current);
        if (event.workspaceGeneration < workspaceGenerationRef.current ||
            (event.workspaceGeneration === workspaceGenerationRef.current && sameWorkspace)) {
          break;
        }
        const workspaceGenerationChanged =
          event.workspaceGeneration > workspaceGenerationRef.current;
        const hadWorkspace = workspacePathRef.current.length > 0;
        const workspaceContextChanged = hadWorkspace && !sameWorkspace;
        workspaceGenerationRef.current = event.workspaceGeneration;
        setWorkspaceGeneration(event.workspaceGeneration);
        workspacePathRef.current = event.path;
        setWorkspaceReady(true);
        setWorkspacePath(event.path);
        setNativeRiskAcknowledged(false);
        setPendingNativePrompt(undefined);
        setConfirmedSessionMode("default");
        updateModeConfirmationPending(false);
        setRuntimeCommands([]);
        setRuntimeCommandsError("");
        setCommandPaletteIndex(0);
        setCommandPaletteDismissed(false);
        if (workspaceGenerationChanged) {
          resetWorkspaceContextState();
          clearCreatedWorktreeRefresh();
          clearPendingAttachments();
          setWorktrees([]);
          setSelectedWorktree(undefined);
          setWorktreeStatus("idle");
          setWorktreeError("");
          setWorktreeActionStatus("");
          setWorktreeConflicts([]);
          updateWorktreeReviewDraft(undefined);
          updateWorktreeOperation(undefined);
        }
        if (workspaceContextChanged) {
          if (activeSessionIdRef.current) {
            closedSessionIdsRef.current.add(activeSessionIdRef.current);
          }
          updateActiveSessionId(undefined);
          setConversationEntries([]);
          setPermissionQueue([]);
          setImagePrompts(false);
          sessionModesRef.current = ["default"];
          setPlanAvailable(undefined);
        }
        requestSessionList(
          archivedSessionsViewRef.current,
          sessionQueryRef.current,
          hadWorkspace && !workspaceContextChanged
        );
        if (bridge.available) {
          bridge.send({
            type: "runtime/commands/list",
            workspaceGeneration: event.workspaceGeneration
          });
        }
        break;
      case "workspace/context/instructions/list": {
        if (event.workspaceGeneration !== workspaceGenerationRef.current ||
            event.requestId !== agentsListRequestRef.current) {
          break;
        }
        agentsListRequestRef.current = undefined;
        setAgentsFiles(event.files);
        setAgentsNotice("");
        const firstFile = event.files[0];
        if (firstFile) {
          setSelectedAgentsPath(firstFile.relativePath);
          requestAgentsFile(firstFile.relativePath);
        } else {
          setSelectedAgentsPath("AGENTS.md");
          setAgentsContent("");
          setSavedAgentsContent("");
          setAgentsEditorStatus("ready");
        }
        break;
      }
      case "workspace/context/file/read":
        if (event.workspaceGeneration !== workspaceGenerationRef.current ||
            event.requestId !== agentsReadRequestRef.current) {
          break;
        }
        agentsReadRequestRef.current = undefined;
        setSelectedAgentsPath(event.relativePath);
        setAgentsContent(event.content);
        setSavedAgentsContent(event.content);
        setAgentsEditorStatus("ready");
        setAgentsNotice("");
        break;
      case "workspace/context/instructions/write":
        if (event.workspaceGeneration !== workspaceGenerationRef.current ||
            event.requestId !== agentsWriteRequestRef.current) {
          break;
        }
        agentsWriteRequestRef.current = undefined;
        setSavedAgentsContent(agentsWriteContentRef.current);
        setAgentsFiles(current => current.some(file =>
          file.relativePath === event.relativePath)
          ? current
          : [{
              relativePath: event.relativePath,
              byteLength: new TextEncoder().encode(agentsWriteContentRef.current).byteLength,
              lastWriteTime: new Date().toISOString()
            }]);
        setAgentsEditorStatus("ready");
        setAgentsNotice(translatorRef.current("agentsSaved"));
        break;
      case "workspace/context/file/search":
        if (event.workspaceGeneration === workspaceGenerationRef.current &&
            event.requestId === fileSearchRequestRef.current) {
          fileSearchRequestRef.current = undefined;
          setFileSearchResults(event.files);
          setFileReferenceIndex(0);
          setFileSearchStatus(event.files.length > 0 ? "loaded" : "empty");
        }
        break;
      case "workspace/context/error":
        if (event.workspaceGeneration !== workspaceGenerationRef.current) {
          break;
        }
        if (event.operation === "file-search" &&
            event.requestId === fileSearchRequestRef.current) {
          fileSearchRequestRef.current = undefined;
          setFileSearchResults([]);
          setFileReferenceIndex(0);
          setFileSearchStatus("error");
          break;
        }
        if ((event.operation === "instructions-list" &&
             event.requestId === agentsListRequestRef.current) ||
            (event.operation === "file-read" &&
             event.requestId === agentsReadRequestRef.current) ||
            (event.operation === "instructions-write" &&
             event.requestId === agentsWriteRequestRef.current)) {
          agentsListRequestRef.current = undefined;
          agentsReadRequestRef.current = undefined;
          agentsWriteRequestRef.current = undefined;
          setAgentsEditorStatus("error");
          setAgentsNotice(translatorRef.current("agentsError"));
        }
        break;
      case "memory/capabilities":
        if (activeSessionIdRef.current && event.sessionId !== activeSessionIdRef.current) {
          break;
        }
        memoryCapabilitiesSessionRef.current = event.sessionId;
        memoryCapabilitiesRef.current = event.memory;
        setMemoryCapabilities(event.memory);
        if (!event.memory.list || !event.memory.read) {
          resetMemoryBrowserDocuments();
        }
        break;
      case "memory/listed": {
        const pending = memoryPendingRequestRef.current;
        if (!pending || pending.operation !== "list" || pending.requestId !== event.requestId ||
            event.workspaceGeneration !== workspaceGenerationRef.current ||
            event.sessionId !== activeSessionIdRef.current) {
          break;
        }
        memoryPendingRequestRef.current = undefined;
        updateMemoryFiles(event.files);
        setMemoryListingTruncated(event.truncated);
        setMemoryMutationChallenge(undefined);
        setMemoryBrowserNotice(undefined);
        const firstFile = event.files[0];
        if (firstFile) {
          updateSelectedMemoryFileId(firstFile.id);
          requestMemoryFile(firstFile.id);
        } else {
          updateSelectedMemoryFileId("");
          setMemoryContent("");
          setSavedMemoryContent("");
          setMemoryBrowserStatus("ready");
        }
        break;
      }
      case "memory/document": {
        const pending = memoryPendingRequestRef.current;
        if (!pending || pending.operation !== "read" || pending.requestId !== event.requestId ||
            pending.fileId !== event.file.id ||
            event.workspaceGeneration !== workspaceGenerationRef.current ||
            event.sessionId !== activeSessionIdRef.current) {
          break;
        }
        memoryPendingRequestRef.current = undefined;
        updateMemoryFiles(memoryFilesRef.current.map((file) =>
          file.id === event.file.id ? event.file : file));
        updateSelectedMemoryFileId(event.file.id);
        setMemoryContent(event.content);
        setSavedMemoryContent(event.content);
        setMemoryMutationChallenge(undefined);
        setMemoryBrowserStatus("ready");
        setMemoryBrowserNotice(undefined);
        break;
      }
      case "memory/mutation": {
        const pending = memoryPendingRequestRef.current;
        if (!pending || pending.requestId !== event.requestId ||
            pending.operation !== event.operation || pending.fileId !== event.fileId ||
            event.workspaceGeneration !== workspaceGenerationRef.current ||
            event.sessionId !== activeSessionIdRef.current) {
          break;
        }
        memoryPendingRequestRef.current = undefined;
        if (event.status === "confirmation_required") {
          if (pending.confirmed !== false ||
              memoryCapabilitiesRef.current?.mutationConfirmationRequired !== true ||
              !event.confirmationToken) {
            setMemoryMutationChallenge(undefined);
            setMemoryBrowserStatus("error");
            setMemoryBrowserNotice({
              kind: "error",
              text: translatorRef.current("memoryConfirmationError")
            });
            break;
          }
          setMemoryMutationChallenge({
            operation: event.operation,
            fileId: event.fileId,
            ...(pending.content === undefined ? {} : { content: pending.content }),
            message: event.message,
            confirmationToken: event.confirmationToken
          });
          setMemoryBrowserStatus("ready");
          setMemoryBrowserNotice({ kind: "status", text: event.message });
          break;
        }
        setMemoryMutationChallenge(undefined);
        if (event.status === "not_found") {
          setMemoryBrowserStatus("error");
          setMemoryBrowserNotice({ kind: "error", text: event.message });
          break;
        }
        if (event.operation === "write") {
          if (event.file) {
            updateMemoryFiles(memoryFilesRef.current.map((file) =>
              file.id === event.file!.id ? event.file! : file));
          }
          setSavedMemoryContent(pending.content ?? "");
          setMemoryBrowserStatus("ready");
          setMemoryBrowserNotice({
            kind: "status",
            text: translatorRef.current("memorySaved")
          });
          break;
        }

        const remainingFiles = memoryFilesRef.current.filter((file) => file.id !== event.fileId);
        updateMemoryFiles(remainingFiles);
        setMemoryBrowserNotice({
          kind: "status",
          text: translatorRef.current("memoryDeleted")
        });
        const nextFile = remainingFiles[0];
        if (nextFile) {
          updateSelectedMemoryFileId(nextFile.id);
          requestMemoryFile(nextFile.id, true);
        } else {
          updateSelectedMemoryFileId("");
          setMemoryContent("");
          setSavedMemoryContent("");
          setMemoryBrowserStatus("ready");
        }
        break;
      }
      case "memory/error": {
        const pending = memoryPendingRequestRef.current;
        if (!pending || pending.requestId !== event.requestId ||
            pending.operation !== event.operation || pending.fileId !== event.fileId ||
            event.workspaceGeneration !== workspaceGenerationRef.current ||
            event.sessionId !== activeSessionIdRef.current) {
          break;
        }
        memoryPendingRequestRef.current = undefined;
        setMemoryMutationChallenge(undefined);
        setMemoryBrowserStatus("error");
        setMemoryBrowserNotice({ kind: "error", text: event.message });
        break;
      }
      case "engine/capabilities": {
        if (activeSessionIdRef.current && event.sessionId !== activeSessionIdRef.current) {
          break;
        }
        setImagePrompts(event.imagePrompts);
        sessionModesRef.current = event.sessionModes;
        setPlanAvailable(event.sessionModes.includes("plan"));
        if (!event.sessionModes.includes(sessionModeRef.current)) {
          updateSessionMode(event.sessionModes[0] ?? "default");
        }
        if (!event.imagePrompts) {
          clearPendingAttachments();
        }
        break;
      }
      case "attachment/changed": {
        if (event.requestId !== attachmentRequestIdRef.current) {
          break;
        }
        attachmentRequestIdRef.current = undefined;
        setAttachmentSelectionPending(false);
        if (event.cancelled) {
          break;
        }
        updatePendingAttachments(event.attachments);
        setAttachmentError(event.error);
        break;
      }
      case "credential/status":
        setEngineMessage(event.message ?? (event.status === "saved"
          ? translatorRef.current("credentialSaved")
          : ""));
        break;
      case "provider/status": {
        const hasProviderIdentity = event.baseUrl.trim().length > 0 && event.model.trim().length > 0;
        if (hasProviderIdentity) {
          setProviderBaseUrl(event.baseUrl);
          setProviderModel(event.model);
          setProviderBackend(event.backend);
          setAllowInsecureTransport(event.allowInsecureTransport);
          setProviderCredentialBaseUrl(event.hasCredential ? event.baseUrl.trim() : undefined);
          setReplaceProviderCredential(false);
        }
        setProviderStatus(event.status);
        setProviderMessage(event.message ?? (event.status === "saved"
          ? translatorRef.current("providerSaved")
          : event.status === "loaded"
            ? translatorRef.current("providerLoaded")
            : translatorRef.current("providerError")));
        setEngineMessage(event.message ?? (event.status === "saved"
          ? translatorRef.current("providerSaved")
          : ""));
        break;
      }
      case "session/update":
        if (event.engineEpoch < activeEngineEpochRef.current ||
            (activeSessionIdRef.current && event.sessionId !== activeSessionIdRef.current)) {
          break;
        }
        activeEngineEpochRef.current = event.engineEpoch;
        updateActiveSessionId(event.sessionId);
        if (event.updateKind === "tool_call" || event.updateKind === "tool_call_update") {
          appendToolUpdate(event.update);
        }
        if (event.text) {
          appendAssistantChunk(event.text);
        }
        break;
      case "prompt/completed":
        updateModeConfirmationPending(false);
        setEngineStatus("ready");
        updateActiveSessionId(event.sessionId);
        setPermissionQueue((requests) =>
          requests.filter((request) => request.sessionId !== event.sessionId));
        setConversationEntries((entries) => entries.map((entry) => entry.type === "message"
          ? { ...entry, streaming: false }
          : entry));
        break;
      case "session/list/changed": {
        const request = sessionListRequestRef.current;
        if (!event.requestId) {
          setSessions((current) => mergeExistingSessionUpdates(current, event.sessions));
          break;
        }
        if (!request || event.requestId !== request.requestId) {
          break;
        }
        setSessions((current) => request.mode === "append"
          ? mergeSessionPages(current, event.sessions)
          : event.sessions);
        setNextSessionCursor(event.nextCursor);
        sessionListRequestRef.current = undefined;
        setLoadingMoreSessions(false);
        setSessionListError("");
        updateSessionListStatus("loaded");
        break;
      }
      case "session/list/error": {
        const request = sessionListRequestRef.current;
        if (!request || event.requestId !== request.requestId) {
          break;
        }
        sessionListRequestRef.current = undefined;
        setLoadingMoreSessions(false);
        setSessionListError(event.message);
        updateSessionListStatus("error");
        break;
      }
      case "session/active/changed": {
        const previousSessionId = activeSessionIdRef.current;
        if (event.engineEpoch < activeEngineEpochRef.current ||
            (event.engineEpoch === activeEngineEpochRef.current &&
              previousSessionId && previousSessionId !== event.sessionId)) {
          break;
        }
        activeEngineEpochRef.current = event.engineEpoch;
        updateActiveSessionId(event.sessionId);
        if (previousSessionId && previousSessionId !== event.sessionId) {
          setConversationEntries([]);
          setPermissionQueue([]);
          clearPendingAttachments();
          if (rewindSessionIdRef.current && rewindSessionIdRef.current !== event.sessionId) {
            closeRewind();
          }
        }
        setWorkspacePath(event.workspacePath);
        setWorkspaceReady(true);
        setNativeRiskAcknowledged(false);
        setPendingNativePrompt(undefined);
        break;
      }
      case "session/renamed": {
        const request = sessionOperationRequestsRef.current.get(event.requestId);
        if (!request || request.operation !== "rename" || request.sessionId !== event.sessionId) {
          break;
        }
        sessionOperationRequestsRef.current.delete(event.requestId);
        setHistoryStatus("");
        break;
      }
      case "session/forked": {
        const remaining = new Set(forkingSessionIdsRef.current);
        remaining.delete(event.parentSessionId);
        updateForkingSessionIds(remaining);
        const forkProfile = pendingForkProfilesRef.current.get(event.parentSessionId)
          ?? "NativeProtected";
        pendingForkProfilesRef.current.delete(event.parentSessionId);
        setHistoryStatus(t("sessionForked"));
        requestSessionList();
        bridge.send({
          type: "session/open",
          sessionId: event.sessionId,
          workspacePath: event.workspacePath,
          executionProfile: forkProfile
        });
        break;
      }
      case "session/compacted":
        if (event.sessionId === activeSessionIdRef.current) {
          updateCompactingSessionId(undefined);
          setHistoryStatus(t("sessionCompacted"));
          requestSessionList();
        }
        break;
      case "session/rewind/points":
        if (event.sessionId === rewindSessionIdRef.current) {
          const points = [...event.points].sort((left, right) =>
            right.promptIndex - left.promptIndex);
          setRewindPoints(points);
          setSelectedRewindPoint(points[0]?.promptIndex);
          updateRewindPointsLoading(false);
          setRewindError("");
        }
        break;
      case "session/rewound":
        if (event.sessionId !== rewindSessionIdRef.current) {
          break;
        }
        updateRewindSubmitting(false);
        if (!event.success) {
          setRewindFailure(event);
          setRewindError(event.error ?? "");
          break;
        }
        if (event.mode !== "files_only") {
          setConversationEntries((entries) => trimConversationAtPrompt(
            entries,
            event.targetPromptIndex
          ));
        }
        if (event.promptText !== undefined) {
          setPrompt(event.promptText);
        }
        setHistoryStatus(t("sessionRewound"));
        modalReturnFocusRef.current = composerRef.current;
        closeRewind();
        requestSessionList();
        break;
      case "session/archive/changed": {
        const request = sessionOperationRequestsRef.current.get(event.requestId);
        if (!request || request.operation !== "archive" || request.sessionId !== event.sessionId) {
          break;
        }
        sessionOperationRequestsRef.current.delete(event.requestId);
        const requestedHere = archivingSessionIdsRef.current.has(event.sessionId);
        const remaining = new Set(archivingSessionIdsRef.current);
        remaining.delete(event.sessionId);
        updateArchivingSessionIds(remaining);
        setSessions((current) => current.filter(
          (session) => session.sessionId !== event.sessionId));
        setEditingSessionId(undefined);
        setSessionTitleDraft("");
        renameReturnFocusRef.current = null;
        requestSessionList();
        if (requestedHere) {
          sessionSearchRef.current?.focus();
        }
        break;
      }
      case "session/operation/error": {
        const request = sessionOperationRequestsRef.current.get(event.requestId);
        if (!request || request.operation !== event.operation || request.sessionId !== event.sessionId) {
          break;
        }
        sessionOperationRequestsRef.current.delete(event.requestId);
        if (event.operation === "archive") {
          const remaining = new Set(archivingSessionIdsRef.current);
          remaining.delete(event.sessionId);
          updateArchivingSessionIds(remaining);
        }
        setHistoryStatus(event.message);
        break;
      }
      case "session/mode/changed": {
        updateActiveSessionId(event.sessionId);
        setConfirmedSessionMode(event.mode);
        setPlanAvailable(event.planAvailable);
        const nextDesiredMode = !event.planAvailable && sessionModeRef.current === "plan"
          ? "default"
          : sessionModeRef.current;
        if (nextDesiredMode !== sessionModeRef.current) {
          updateSessionMode(nextDesiredMode);
        }
        if (event.mode === nextDesiredMode) {
          updateModeConfirmationPending(false);
        }
        break;
      }
      case "runtime/dashboard/changed":
        if (event.sessionId !== activeSessionIdRef.current) {
          break;
        }
        setBackgroundTasks(event.backgroundTasks);
        setRunningSubagents(event.subagents);
        runtimeDashboardRequestSessionRef.current = undefined;
        setRuntimeDashboardError("");
        setRuntimeDashboardStatus("loaded");
        setSelectedSubagent((current) => current
          ? event.subagents.find((subagent) => subagent.subagentId === current.subagentId)
          : undefined);
        break;
      case "runtime/task/killed":
        if (event.sessionId !== activeSessionIdRef.current) {
          break;
        }
        setPendingTaskKills((current) => withoutSetItem(current, event.taskId));
        setRuntimeActionStatus(t(`runtimeTaskOutcome_${event.outcome}`));
        break;
      case "runtime/subagent/detail":
        if (event.sessionId === activeSessionIdRef.current) {
          setSelectedSubagent(event.snapshot);
        }
        break;
      case "runtime/subagent/cancelled":
        if (event.sessionId !== activeSessionIdRef.current) {
          break;
        }
        setPendingSubagentCancels((current) => withoutSetItem(current, event.subagentId));
        setRuntimeActionStatus(t(`runtimeSubagentOutcome_${event.outcome}`));
        break;
      case "runtime/dashboard/error":
        if (event.sessionId !== activeSessionIdRef.current) {
          break;
        }
        if (event.operation === "refresh") {
          runtimeDashboardRequestSessionRef.current = undefined;
          setRuntimeDashboardError(event.message);
          setRuntimeDashboardStatus("error");
        }
        else {
          if (event.operation === "task_kill" && event.itemId) {
            setPendingTaskKills((current) => withoutSetItem(current, event.itemId!));
          }
          if (event.operation === "subagent_cancel" && event.itemId) {
            setPendingSubagentCancels((current) => withoutSetItem(current, event.itemId!));
          }
          setRuntimeActionStatus(event.message);
        }
        break;
      case "runtime/commands/changed":
        if (event.workspaceGeneration !== workspaceGenerationRef.current) {
          break;
        }
        setRuntimeCommands(event.commands);
        setRuntimeCommandsError("");
        setCommandPaletteIndex(0);
        break;
      case "runtime/commands/error":
        if (event.workspaceGeneration !== workspaceGenerationRef.current) {
          break;
        }
        setRuntimeCommands([]);
        setRuntimeCommandsError(event.message);
        setCommandPaletteIndex(0);
        break;
      case "worktree/created":
        if (event.workspaceGeneration !== workspaceGenerationRef.current) {
          break;
        }
        updateWorktreeOperation(undefined);
        setWorktreeActionStatus(translatorRef.current("worktreeCreated"));
        setWorktreeError("");
        setWorktreeConflicts([]);
        startCreatedWorktreeRefresh(event.worktreePath, event.workspaceGeneration);
        break;
      case "worktree/list/changed":
        if (event.workspaceGeneration !== workspaceGenerationRef.current) {
          break;
        }
        updateWorktreeOperation(undefined);
        setWorktrees(event.worktrees);
        setSelectedWorktree((current) => current
          ? event.worktrees.find((worktree) => worktree.id === current.id)
          : undefined);
        if (worktreeReviewDraftRef.current && !event.worktrees.some((worktree) =>
          worktree.id === worktreeReviewDraftRef.current?.worktreeId &&
          worktree.status === "alive")) {
          updateWorktreeReviewDraft(undefined);
        }
        setWorktreeStatus("loaded");
        setWorktreeError("");
        continueCreatedWorktreeRefresh(event.worktrees, event.workspaceGeneration);
        break;
      case "worktree/detail":
        if (event.workspaceGeneration !== workspaceGenerationRef.current) {
          break;
        }
        updateWorktreeOperation(undefined);
        setSelectedWorktree(event.worktree);
        if (!event.worktree || (worktreeReviewDraftRef.current &&
            worktreeReviewDraftRef.current.worktreeId !== event.worktree.id)) {
          updateWorktreeReviewDraft(undefined);
        }
        setWorktreeError("");
        break;
      case "worktree/applied":
        if (event.workspaceGeneration !== workspaceGenerationRef.current) {
          break;
        }
        updateWorktreeOperation(undefined);
        setWorktreeConflicts(event.conflicts);
        setWorktreeError("");
        setWorktreeActionStatus(translatorRef.current(
          event.status === "conflicts" ? "worktreeApplyConflicts" : "worktreeApplied"
        ));
        break;
      case "worktree/removed":
        if (event.workspaceGeneration !== workspaceGenerationRef.current) {
          break;
        }
        updateWorktreeOperation(undefined);
        if (event.removed) {
          setWorktrees((current) => current.filter((worktree) =>
            worktree.id !== event.idOrPath &&
            worktree.path !== event.idOrPath &&
            worktree.path !== event.resolvedPath));
          setSelectedWorktree((current) => current &&
            (current.id === event.idOrPath || current.path === event.resolvedPath)
            ? undefined
            : current);
          if (worktreeReviewDraftRef.current &&
              worktreeReviewDraftRef.current.worktreeId === event.idOrPath) {
            updateWorktreeReviewDraft(undefined);
          }
        }
        setWorktreeError("");
        setWorktreeActionStatus(translatorRef.current(
          event.removed ? "worktreeRemoved" : "worktreeRemoveSkipped"
        ));
        break;
      case "worktree/gc/completed":
        if (event.workspaceGeneration !== workspaceGenerationRef.current) {
          break;
        }
        updateWorktreeOperation(undefined);
        setWorktreeError("");
        if (worktreeGcOpenRef.current && !worktreeGcExecutingRef.current) {
          setWorktreeGcPreview({
            deadRemoved: event.deadRemoved,
            expiredRemoved: event.expiredRemoved,
            skippedAlive: event.skippedAlive,
            removeFailed: event.removeFailed
          });
        }
        if (worktreeGcExecutingRef.current) {
          worktreeGcExecutingRef.current = false;
          setWorktreeGcExecuting(false);
          worktreeGcOpenRef.current = false;
          setWorktreeGcOpen(false);
          requestWorktrees();
        }
        setWorktreeActionStatus(translatorRef.current("worktreeGcSummary")
          .replace("{removed}", String(event.deadRemoved + event.expiredRemoved))
          .replace("{failed}", String(event.removeFailed)));
        break;
      case "worktree/error":
        if (event.workspaceGeneration !== workspaceGenerationRef.current) {
          break;
        }
        updateWorktreeOperation(undefined);
        setWorktreeError(event.message);
        if (event.operation === "list") {
          setWorktreeStatus("error");
          scheduleCreatedWorktreeRefresh();
        }
        break;
      case "runtime/memory/status":
        if (event.sessionId !== activeSessionIdRef.current) {
          break;
        }
        setMemoryStatus(event.status);
        setMemoryMessage(event.message ?? translatorRef.current(
          event.status === "running"
            ? "memoryFlushRunning"
            : event.status === "succeeded"
              ? "memoryFlushSucceeded"
              : "memoryFlushError"
        ));
        break;
      case "session/exported": {
        const pending = maintenanceRequestRef.current;
        if (!pending || pending.requestId !== event.requestId ||
            pending.operation !== "session-export") {
          break;
        }
        updateMaintenanceRequest(undefined);
        setMaintenanceNotice({
          kind: "status",
          text: translatorRef.current("sessionExported").replace("{fileName}", event.fileName)
        });
        break;
      }
      case "session/imported": {
        const pending = maintenanceRequestRef.current;
        if (!pending || pending.requestId !== event.requestId ||
            pending.operation !== "session-import") {
          break;
        }
        updateMaintenanceRequest(undefined);
        updateActiveSessionId(event.sessionId);
        workspacePathRef.current = event.workspacePath;
        setWorkspacePath(event.workspacePath);
        setWorkspaceReady(true);
        setMaintenanceNotice({
          kind: "status",
          text: translatorRef.current("sessionImported")
        });
        requestSessionList();
        break;
      }
      case "backup/completed": {
        const pending = maintenanceRequestRef.current;
        const expectedOperation = event.operation === "create" ? "backup-create" : "backup-restore";
        if (!pending || pending.requestId !== event.requestId ||
            pending.operation !== expectedOperation) {
          break;
        }
        updateMaintenanceRequest(undefined);
        setMaintenanceNotice({
          kind: "status",
          text: translatorRef.current(event.operation === "create"
            ? "backupCreated"
            : "backupRestored")
            .replace("{fileCount}", String(event.fileCount))
        });
        break;
      }
      case "update/background-available":
        setStagedUpdateVersion(event.version);
        setMaintenanceNotice({
          kind: "status",
          text: translatorRef.current("backgroundUpdateAvailable")
        });
        break;
      case "update/status": {
        const pending = maintenanceRequestRef.current;
        if (!pending || pending.requestId !== event.requestId ||
            (pending.operation !== "update-check" && pending.operation !== "update-apply")) {
          break;
        }
        if (event.status === "checking") {
          setMaintenanceNotice({ kind: "status", text: translatorRef.current("updateChecking") });
          break;
        }

        if (event.status !== "launching") {
          updateMaintenanceRequest(undefined);
        }
        if (event.status === "available" && event.version) {
          setStagedUpdateVersion(event.version);
          setMaintenanceNotice({ kind: "status", text: translatorRef.current("updateAvailable") });
        } else if (event.status === "up-to-date") {
          setStagedUpdateVersion(undefined);
          setMaintenanceNotice({ kind: "status", text: translatorRef.current("updateUpToDate") });
        } else if (event.status === "unsupported") {
          setUpdatesUnsupported(true);
          setStagedUpdateVersion(undefined);
          setMaintenanceNotice({
            kind: "status",
            text: translatorRef.current("msixUpdateUnsupported")
          });
        } else if (event.status === "launching") {
          setStagedUpdateVersion(undefined);
          setMaintenanceNotice({ kind: "status", text: translatorRef.current("updateLaunching") });
        } else {
          setStagedUpdateVersion(undefined);
          setMaintenanceNotice({ kind: "error", text: translatorRef.current("updateFailed") });
        }
        break;
      }
      case "maintenance/error": {
        const pending = maintenanceRequestRef.current;
        if (!pending || pending.requestId !== event.requestId ||
            pending.operation !== event.operation) {
          break;
        }
        updateMaintenanceRequest(undefined);
        setMaintenanceNotice({
          kind: "error",
          text: translatorRef.current(`maintenanceError_${event.operation}`)
        });
        break;
      }
      case "maintenance/cancelled": {
        const pending = maintenanceRequestRef.current;
        if (!pending || pending.requestId !== event.requestId ||
            pending.operation !== event.operation) {
          break;
        }
        updateMaintenanceRequest(undefined);
        setMaintenanceNotice(undefined);
        break;
      }
      case "windows/automation/completed": {
        const pending = windowsAutomationPendingRef.current;
        if (!pending || pending.requestId !== event.requestId ||
            pending.action !== event.action || pending.processId !== event.processId) {
          break;
        }
        updateWindowsAutomationPending(undefined);
        setWindowsAutomationNotice({
          kind: "status",
          text: translatorRef.current("windowsAutomationCompleted")
        });
        break;
      }
      case "windows/automation/cancelled": {
        const pending = windowsAutomationPendingRef.current;
        if (!pending || pending.requestId !== event.requestId) {
          break;
        }
        updateWindowsAutomationPending(undefined);
        setWindowsAutomationNotice({
          kind: "status",
          text: translatorRef.current("windowsAutomationCancelled")
        });
        break;
      }
      case "windows/automation/error": {
        const pending = windowsAutomationPendingRef.current;
        if (!pending || pending.requestId !== event.requestId) {
          break;
        }
        updateWindowsAutomationPending(undefined);
        setWindowsAutomationNotice({
          kind: "error",
          text: translatorRef.current(`windowsAutomationError_${event.reason}`)
        });
        break;
      }
      case "ui/preferences/changed": {
        setLanguage(event.language);
        setPrompt(event.composerDraft);
        updateSessionMode(event.sessionMode);
        setExecutionProfile(event.executionProfile);
        setNotificationsEnabled(event.notificationsEnabled);
        setWindowsAutomationEnabled(event.windowsAutomationEnabled);
        setBackgroundUpdateChecksEnabled(event.backgroundUpdateChecksEnabled);
        setWindowsAutomationHostEnabled(event.windowsAutomationEnabled);
        if (!event.windowsAutomationEnabled && windowsAutomationPendingRef.current) {
          updateWindowsAutomationPending(undefined);
          setWindowsAutomationNotice({
            kind: "error",
            text: translatorRef.current("windowsAutomationError_disabled")
          });
        }
        if (!event.windowsAutomationEnabled && windowsAutomationValueRef.current) {
          windowsAutomationValueRef.current.value = "";
        }
        setRestartRequired(event.restartRequired);
        setPreferencesHydrated(true);
        setCommandPaletteIndex(0);
        setCommandPaletteDismissed(false);
        break;
      }
      case "cloud/notification": {
        const message = event.kind === "policy-changed"
          ? translatorRef.current("cloudNotificationPolicy").replace(
              "{version}",
              String(event.policyVersion)
            )
          : translatorRef.current(event.kind === "handoff-changed"
              ? "cloudNotificationHandoff"
              : "cloudNotificationJob")
            .replace("{id}", event.resourceId);
        setCloudPushNotice(message);
        break;
      }
      case "permission/requested":
        if (closedSessionIdsRef.current.has(event.sessionId)) {
          break;
        }
        setPermissionQueue((requests) => requests.some(
          (request) => request.requestId === event.requestId)
          ? requests
          : [...requests, event]);
        break;
    }
  }

  function appendAssistantChunk(text: string) {
    setConversationEntries((entries) => {
      const last = entries.at(-1);
      if (last?.type === "message" && last.role === "assistant" && last.streaming) {
        return [...entries.slice(0, -1), { ...last, text: last.text + text }];
      }
      return [...entries, {
        type: "message",
        id: `message-${++nextLiveMessageId}`,
        role: "assistant",
        text,
        streaming: true
      }];
    });
  }

  function appendToolUpdate(value: unknown) {
    const update = asRecord(value);
    const toolCallId = update && nonEmptyString(update.toolCallId);
    if (!update || !toolCallId) {
      return;
    }
    setConversationEntries((entries) => {
      const existingIndex = entries.findIndex(
        (entry) => entry.type === "tool" && entry.toolCallId === toolCallId
      );
      const existing = existingIndex >= 0
        ? entries[existingIndex] as ToolTimelineEntry
        : undefined;
      const next: ToolTimelineEntry = {
        type: "tool",
        id: `tool-${toolCallId}`,
        toolCallId,
        title: nonEmptyString(update.title) ?? existing?.title ?? toolCallId,
        kind: nonEmptyString(update.kind) ?? existing?.kind ?? "other",
        status: nonEmptyString(update.status) ?? existing?.status ?? "pending",
        ...(Object.hasOwn(update, "rawInput")
          ? { rawInput: update.rawInput }
          : Object.hasOwn(existing ?? {}, "rawInput")
            ? { rawInput: existing?.rawInput }
            : {})
      };
      if (existingIndex < 0) {
        return [...entries, next];
      }
      return entries.map((entry, index) => index === existingIndex ? next : entry);
    });
  }

  function requestRuntimeDashboard() {
    const sessionId = activeSessionIdRef.current;
    if (!sessionId || !bridge.available) {
      setRuntimeDashboardStatus("loaded");
      return;
    }
    if (runtimeDashboardRequestSessionRef.current === sessionId) {
      return;
    }
    runtimeDashboardRequestSessionRef.current = sessionId;
    setRuntimeDashboardError("");
    setRuntimeDashboardStatus("loading");
    bridge.send({ type: "runtime/dashboard/refresh", sessionId });
  }

  function killBackgroundTask(task: BackgroundTaskSnapshot) {
    const sessionId = activeSessionIdRef.current;
    if (!sessionId || task.completed || pendingTaskKills.has(task.taskId)) {
      return;
    }
    setRuntimeActionStatus("");
    setPendingTaskKills((current) => new Set(current).add(task.taskId));
    bridge.send({
      type: "runtime/task/kill",
      sessionId,
      taskId: task.taskId
    });
  }

  function inspectSubagent(subagent: SubagentSnapshot) {
    const sessionId = activeSessionIdRef.current;
    if (!sessionId) {
      return;
    }
    setSelectedSubagent(subagent);
    bridge.send({
      type: "runtime/subagent/get",
      sessionId,
      subagentId: subagent.subagentId
    });
  }

  function cancelSubagent(subagent: SubagentSnapshot) {
    const sessionId = activeSessionIdRef.current;
    if (!sessionId || subagent.status !== "running" ||
        pendingSubagentCancels.has(subagent.subagentId)) {
      return;
    }
    setRuntimeActionStatus("");
    setPendingSubagentCancels((current) => new Set(current).add(subagent.subagentId));
    bridge.send({
      type: "runtime/subagent/cancel",
      sessionId,
      subagentId: subagent.subagentId
    });
  }

  function updateWorktreeOperation(operation: WorktreeOperation | undefined) {
    worktreeOperationRef.current = operation;
    setWorktreeOperation(operation);
  }

  function updateWorktreeReviewDraft(draft: WorktreeReviewDraft | undefined) {
    worktreeReviewDraftRef.current = draft;
    setWorktreeReviewDraft(draft);
  }

  function beginWorktreeOperation(operation: WorktreeOperation): boolean {
    if (worktreeOperationRef.current) {
      return false;
    }
    updateWorktreeOperation(operation);
    setWorktreeError("");
    setWorktreeActionStatus("");
    if (operation !== "apply") {
      setWorktreeConflicts([]);
    }
    return true;
  }

  function clearCreatedWorktreeRefresh() {
    const refresh = createdWorktreeRefreshRef.current;
    if (refresh?.timeout !== undefined) {
      window.clearTimeout(refresh.timeout);
    }
    createdWorktreeRefreshRef.current = undefined;
  }

  function scheduleCreatedWorktreeRefresh() {
    const refresh = createdWorktreeRefreshRef.current;
    if (!refresh || refresh.timeout !== undefined) {
      return;
    }
    if (refresh.attemptsRemaining <= 0 ||
        refresh.workspaceGeneration !== workspaceGenerationRef.current) {
      clearCreatedWorktreeRefresh();
      return;
    }
    refresh.timeout = window.setTimeout(() => {
      const current = createdWorktreeRefreshRef.current;
      if (!current) {
        return;
      }
      current.timeout = undefined;
      current.attemptsRemaining -= 1;
      if (!requestWorktrees()) {
        scheduleCreatedWorktreeRefresh();
      }
    }, createdWorktreeRefreshIntervalMs);
  }

  function startCreatedWorktreeRefresh(worktreePath: string, workspaceGeneration: number) {
    clearCreatedWorktreeRefresh();
    createdWorktreeRefreshRef.current = {
      workspaceGeneration,
      worktreePath,
      attemptsRemaining: createdWorktreeRefreshAttempts
    };
    scheduleCreatedWorktreeRefresh();
  }

  function continueCreatedWorktreeRefresh(
    nextWorktrees: WorktreeRecord[],
    workspaceGeneration: number
  ) {
    const refresh = createdWorktreeRefreshRef.current;
    if (!refresh || refresh.workspaceGeneration !== workspaceGeneration) {
      return;
    }
    if (nextWorktrees.some((worktree) => sameWorktreePath(
      worktree.path,
      refresh.worktreePath
    ))) {
      clearCreatedWorktreeRefresh();
      return;
    }
    scheduleCreatedWorktreeRefresh();
  }

  function requestWorktrees(): boolean {
    if (!bridge.available) {
      setWorktrees([]);
      setWorktreeStatus("loaded");
      return false;
    }
    if (!beginWorktreeOperation("list")) {
      return false;
    }
    setWorktreeStatus("loading");
    bridge.send({
      type: "worktree/list",
      workspaceGeneration: workspaceGenerationRef.current,
      includeAll: false,
      types: []
    });
    return true;
  }

  function createWorktree() {
    if (!activeSessionIdRef.current || worktreeOperationRef.current) {
      return;
    }
    setWorktreeCreateDraft({
      copyMode: "dirty",
      creationType: "linked",
      gitReference: "HEAD",
      label: "",
      destinationPath: ""
    });
    setWorktreeCreateOpen(true);
  }

  function submitWorktreeCreate() {
    const sessionId = activeSessionIdRef.current;
    if (!sessionId || !beginWorktreeOperation("create")) {
      return;
    }
    const draft = worktreeCreateDraft;
    bridge.send({
      type: "worktree/create",
      workspaceGeneration: workspaceGenerationRef.current,
      sessionId,
      copyMode: draft.copyMode,
      copyIgnoredInBackground: false,
      ignoredSkipPatterns: [],
      creationType: draft.creationType,
      ...(draft.gitReference.trim() ? { gitReference: draft.gitReference.trim() } : {}),
      ...(draft.label.trim() ? { label: draft.label.trim() } : {}),
      ...(draft.destinationPath.trim() ? { destinationPath: draft.destinationPath.trim() } : {})
    });
    setWorktreeCreateOpen(false);
  }

  function inspectWorktree(worktree: WorktreeRecord) {
    if (!beginWorktreeOperation("show")) {
      return;
    }
    bridge.send({
      type: "worktree/show",
      workspaceGeneration: workspaceGenerationRef.current,
      idOrPath: worktree.id
    });
  }

  function reviewWorktree(worktree: WorktreeRecord) {
    if (!activeSessionIdRef.current || worktreeOperationRef.current ||
        engineStatus === "starting" || engineStatus === "running" ||
        selectedWorktree?.id !== worktree.id || worktree.status !== "alive") {
      return;
    }
    const request = buildWorktreeReviewRequest(worktree, t("worktreeReviewPrompt"));
    if (!request) {
      return;
    }
    updateWorktreeReviewDraft({
      worktreeId: worktree.id,
      workspaceGeneration: workspaceGenerationRef.current,
      request
    });
  }

  function editWorktreeReview(request: string) {
    const current = worktreeReviewDraftRef.current;
    if (!current || request.length > worktreeReviewRequestMaxLength) {
      return;
    }
    updateWorktreeReviewDraft({ ...current, request });
  }

  function startWorktreeReview() {
    const draft = worktreeReviewDraftRef.current;
    if (!draft) {
      return;
    }
    submitPrompt({
      text: draft.request.trim(),
      attachments: [],
      source: "worktree-review",
      worktreeId: draft.worktreeId,
      workspaceGeneration: draft.workspaceGeneration
    });
  }

  function applyWorktree(worktree: WorktreeRecord) {
    if (!activeSessionIdRef.current || worktreeOperationRef.current) {
      return;
    }
    setWorktreeApplyTarget(worktree);
    setWorktreeApplyMode("merge");
  }

  function submitWorktreeApply() {
    const sessionId = activeSessionIdRef.current;
    const worktree = worktreeApplyTarget;
    if (!sessionId || !worktree || !beginWorktreeOperation("apply")) {
      return;
    }
    bridge.send({
      type: "worktree/apply",
      workspaceGeneration: workspaceGenerationRef.current,
      sessionId,
      worktreePath: worktree.path,
      mode: worktreeApplyMode
    });
    setWorktreeApplyTarget(undefined);
  }

  function removeWorktree(worktree: WorktreeRecord) {
    if (worktreeOperationRef.current ||
        !window.confirm(t("removeWorktreeConfirm").replace("{label}", worktreeLabel(worktree)))) {
      return;
    }
    if (!beginWorktreeOperation("remove")) {
      return;
    }
    bridge.send({
      type: "worktree/remove",
      workspaceGeneration: workspaceGenerationRef.current,
      idOrPath: worktree.id,
      force: false,
      dryRun: false
    });
  }

  function gcWorktrees() {
    if (worktreeOperationRef.current) {
      return;
    }
    setWorktreeGcPreview(undefined);
    worktreeGcExecutingRef.current = false;
    setWorktreeGcExecuting(false);
    worktreeGcOpenRef.current = true;
    setWorktreeGcOpen(true);
    previewGcWorktrees();
  }

  function previewGcWorktrees() {
    if (!beginWorktreeOperation("gc")) {
      return;
    }
    worktreeGcExecutingRef.current = false;
    setWorktreeGcExecuting(false);
    bridge.send({
      type: "worktree/gc",
      workspaceGeneration: workspaceGenerationRef.current,
      dryRun: true,
      force: false
    });
  }

  function executeGcWorktrees() {
    if (!worktreeGcPreview || worktreeOperationRef.current ||
        !window.confirm(t("worktreeGcConfirm"))) {
      return;
    }
    if (!beginWorktreeOperation("gc")) {
      return;
    }
    worktreeGcExecutingRef.current = true;
    setWorktreeGcExecuting(true);
    bridge.send({
      type: "worktree/gc",
      workspaceGeneration: workspaceGenerationRef.current,
      dryRun: false,
      force: false
    });
  }

  function requestSessionList(
    archived = archivedSessionsViewRef.current,
    query = sessionQueryRef.current,
    preserveSessions = false
  ) {
    if (!workspacePathRef.current) {
      return;
    }
    const requestId = createMaintenanceRequestId();
    sessionListRequestRef.current = { requestId, mode: "replace" };
    setLoadingMoreSessions(false);
    if (!preserveSessions) {
      setSessions([]);
    }
    setNextSessionCursor(undefined);
    setSessionListError("");
    updateSessionListStatus("loading");
    bridge.send({
      type: "session/list",
      requestId,
      query: query.trim(),
      limit: sessionPageSize,
      ...(archived ? { archived: true } : {})
    });
  }

  function loadMoreSessionResults() {
    if (!nextSessionCursor || loadingMoreSessions) {
      return;
    }
    const requestId = createMaintenanceRequestId();
    sessionListRequestRef.current = { requestId, mode: "append" };
    setLoadingMoreSessions(true);
    bridge.send({
      type: "session/list",
      requestId,
      query: sessionQueryRef.current.trim(),
      cursor: nextSessionCursor,
      limit: sessionPageSize,
      ...(archivedSessionsViewRef.current ? { archived: true } : {})
    });
  }

  function selectSessionView(archived: boolean) {
    if (archivedSessionsViewRef.current === archived) {
      return;
    }
    archivedSessionsViewRef.current = archived;
    setArchivedSessionsView(archived);
    setEditingSessionId(undefined);
    setSessionTitleDraft("");
    renameReturnFocusRef.current = null;
    requestSessionList(archived);
  }

  function handleSessionViewKeyDown(event: React.KeyboardEvent<HTMLButtonElement>) {
    let archived: boolean | undefined;
    if (event.key === "ArrowLeft" || event.key === "ArrowUp" || event.key === "Home") {
      archived = false;
    } else if (event.key === "ArrowRight" || event.key === "ArrowDown" || event.key === "End") {
      archived = true;
    }
    if (archived === undefined) {
      return;
    }
    event.preventDefault();
    selectSessionView(archived);
    (archived ? archivedSessionsTabRef : activeSessionsTabRef).current?.focus();
  }

  function archiveSession(session: SessionSummary) {
    if (archivingSessionIdsRef.current.has(session.sessionId)) {
      return;
    }
    const pending = new Set(archivingSessionIdsRef.current);
    pending.add(session.sessionId);
    updateArchivingSessionIds(pending);
    const requestId = createMaintenanceRequestId();
    sessionOperationRequestsRef.current.set(requestId, {
      operation: "archive",
      sessionId: session.sessionId
    });
    bridge.send({
      type: "session/archive",
      requestId,
      sessionId: session.sessionId,
      archived: !archivedSessionsViewRef.current
    });
  }

  function forkSession(session: SessionSummary) {
    if (forkingSessionIdsRef.current.has(session.sessionId) ||
        engineStatus === "starting" || engineStatus === "running") {
      return;
    }
    const pending = new Set(forkingSessionIdsRef.current);
    pending.add(session.sessionId);
    updateForkingSessionIds(pending);
    pendingForkProfilesRef.current.set(session.sessionId, executionProfile);
    setHistoryStatus("");
    bridge.send({
      type: "session/fork",
      sessionId: session.sessionId,
      sourceWorkspacePath: session.workspacePath,
      targetWorkspacePath: session.workspacePath
    });
  }

  function compactActiveSession() {
    const sessionId = activeSessionIdRef.current;
    if (!sessionId || compactingSessionIdRef.current ||
        engineStatus === "starting" || engineStatus === "running") {
      return;
    }
    updateCompactingSessionId(sessionId);
    setHistoryStatus("");
    bridge.send({ type: "session/compact", sessionId });
  }

  function openRewind() {
    const sessionId = activeSessionIdRef.current;
    if (!sessionId || engineStatus === "starting" || engineStatus === "running") {
      return;
    }
    if (document.activeElement instanceof HTMLElement) {
      modalReturnFocusRef.current = document.activeElement;
    }
    rewindSessionIdRef.current = sessionId;
    setRewindPoints([]);
    setSelectedRewindPoint(undefined);
    setRewindMode("all");
    setRewindFailure(undefined);
    setRewindError("");
    updateRewindSubmitting(false);
    updateRewindPointsLoading(true);
    setRewindOpen(true);
    bridge.send({ type: "session/rewind/points", sessionId });
  }

  function selectRewindPoint(promptIndex: number) {
    if (rewindSubmittingRef.current) {
      return;
    }
    setSelectedRewindPoint(promptIndex);
    setRewindFailure(undefined);
    setRewindError("");
  }

  function selectRewindMode(mode: SessionRewindMode) {
    if (rewindSubmittingRef.current) {
      return;
    }
    setRewindMode(mode);
    setRewindFailure(undefined);
    setRewindError("");
  }

  function handleRewindPointKeyDown(
    event: React.KeyboardEvent<HTMLButtonElement>,
    index: number
  ) {
    const nextIndex = radioNavigationIndex(event.key, index, rewindPoints.length);
    if (nextIndex === undefined) {
      return;
    }
    event.preventDefault();
    const point = rewindPoints[nextIndex];
    selectRewindPoint(point.promptIndex);
    queueMicrotask(() => rewindDialogRef.current
      ?.querySelector<HTMLButtonElement>(`[data-prompt-index="${point.promptIndex}"]`)
      ?.focus());
  }

  function handleRewindModeKeyDown(
    event: React.KeyboardEvent<HTMLButtonElement>,
    index: number
  ) {
    const modes: SessionRewindMode[] = ["all", "conversation_only", "files_only"];
    const nextIndex = radioNavigationIndex(event.key, index, modes.length);
    if (nextIndex === undefined) {
      return;
    }
    event.preventDefault();
    const mode = modes[nextIndex];
    selectRewindMode(mode);
    queueMicrotask(() => rewindDialogRef.current
      ?.querySelector<HTMLButtonElement>(`[data-rewind-mode="${mode}"]`)
      ?.focus());
  }

  function submitRewind(force: boolean) {
    const sessionId = rewindSessionIdRef.current;
    if (!sessionId || selectedRewindPoint === undefined || rewindSubmittingRef.current) {
      return;
    }
    setRewindFailure(undefined);
    setRewindError("");
    updateRewindSubmitting(true);
    bridge.send({
      type: "session/rewind",
      sessionId,
      targetPromptIndex: selectedRewindPoint,
      mode: rewindMode,
      force
    });
  }

  function closeRewind() {
    if (rewindSubmittingRef.current) {
      return;
    }
    rewindSessionIdRef.current = undefined;
    updateRewindPointsLoading(false);
    setRewindOpen(false);
    setRewindPoints([]);
    setSelectedRewindPoint(undefined);
    setRewindFailure(undefined);
    setRewindError("");
  }

  function openSession(session: SessionSummary) {
    if (engineStatus === "starting" || engineStatus === "running") {
      return;
    }
    bridge.send({
      type: "session/open",
      sessionId: session.sessionId,
      workspacePath: session.workspacePath,
      executionProfile
    });
  }

  function startSessionRename(
    session: SessionSummary,
    target: SessionRenameFocusTarget["target"]
  ) {
    renameReturnFocusRef.current = { sessionId: session.sessionId, target };
    setEditingSessionId(session.sessionId);
    setSessionTitleDraft(session.title);
  }

  function saveSessionRename(session: SessionSummary) {
    const title = sessionTitleDraft.trim();
    if (!title) {
      return;
    }
    if (title !== session.title) {
      const requestId = createMaintenanceRequestId();
      sessionOperationRequestsRef.current.set(requestId, {
        operation: "rename",
        sessionId: session.sessionId
      });
      bridge.send({
        type: "session/rename",
        requestId,
        sessionId: session.sessionId,
        title,
        workspacePath: session.workspacePath
      });
    }
    closeSessionRename();
  }

  function closeSessionRename() {
    setEditingSessionId(undefined);
    setSessionTitleDraft("");
  }

  function updateActiveSessionId(sessionId: string | undefined) {
    if (activeSessionIdRef.current !== sessionId) {
      setBackgroundTasks([]);
      setRunningSubagents([]);
      setRuntimeDashboardError("");
      setRuntimeActionStatus("");
      setRuntimeDashboardStatus("idle");
      setPendingTaskKills(new Set());
      setPendingSubagentCancels(new Set());
      setSelectedSubagent(undefined);
      setMemoryStatus("idle");
      setMemoryMessage("");
      resetMemoryBrowserState(memoryCapabilitiesSessionRef.current === sessionId);
      runtimeDashboardRequestSessionRef.current = undefined;
    }
    activeSessionIdRef.current = sessionId;
    setActiveSessionId(sessionId);
  }

  function updateSessionListStatus(status: SessionListStatus) {
    sessionListStatusRef.current = status;
    setSessionListStatus(status);
  }

  function updateArchivingSessionIds(sessionIds: Set<string>) {
    archivingSessionIdsRef.current = sessionIds;
    setArchivingSessionIds(sessionIds);
  }

  function updateForkingSessionIds(sessionIds: Set<string>) {
    forkingSessionIdsRef.current = sessionIds;
    setForkingSessionIds(sessionIds);
  }

  function updateCompactingSessionId(sessionId: string | undefined) {
    compactingSessionIdRef.current = sessionId;
    setCompactingSessionId(sessionId);
  }

  function updateRewindPointsLoading(loading: boolean) {
    rewindPointsLoadingRef.current = loading;
    setRewindPointsLoading(loading);
  }

  function updateRewindSubmitting(submitting: boolean) {
    rewindSubmittingRef.current = submitting;
    setRewindSubmitting(submitting);
  }

  function sendPrompt() {
    const text = appendFileReferences(prompt.trim(), referencedFiles);
    const attachments = [...pendingAttachmentsRef.current];
    submitPrompt({ text, attachments, source: "composer" });
  }

  function submitPrompt(submission: PromptSubmission) {
    if (!canSubmitPrompt(submission)) {
      return;
    }

    if (executionProfile === "NativeProtected" && !nativeRiskAcknowledged) {
      const activeElement = document.activeElement instanceof HTMLElement
        && document.activeElement !== document.body
        ? document.activeElement
        : sendButtonRef.current;
      modalReturnFocusRef.current = activeElement;
      setPendingNativePrompt(submission);
      return;
    }

    dispatchPrompt(
      submission,
      executionProfile === "NativeProtected" && nativeRiskAcknowledged
    );
  }

  function canSubmitPrompt(submission: PromptSubmission): boolean {
    if (!workspaceReady || (!submission.text && submission.attachments.length === 0) ||
        engineStatus === "starting" || engineStatus === "running") {
      return false;
    }
    if (submission.source !== "worktree-review") {
      return true;
    }
    const draft = worktreeReviewDraftRef.current;
    return Boolean(
      activeSessionIdRef.current &&
      draft &&
      selectedWorktree &&
      selectedWorktree.status === "alive" &&
      draft.worktreeId === selectedWorktree.id &&
      draft.worktreeId === submission.worktreeId &&
      draft.workspaceGeneration === workspaceGenerationRef.current &&
      draft.workspaceGeneration === submission.workspaceGeneration &&
      submission.text.length <= worktreeReviewRequestMaxLength
    );
  }

  function dispatchPrompt(submission: PromptSubmission, riskAcknowledged: boolean) {
    const requestedSessionMode = sessionModeRef.current;
    bridge.send({
      type: "engine/prompt",
      text: submission.text,
      executionProfile,
      sessionMode: requestedSessionMode,
      nativeRiskAcknowledged: riskAcknowledged,
      workspaceGeneration: workspaceGenerationRef.current,
      ...(submission.attachments.length > 0 ? { attachments: submission.attachments } : {})
    });
    updateModeConfirmationPending(requestedSessionMode !== confirmedSessionMode);
    const displayText = submission.text || t("imagePromptSummary").replace(
      "{count}",
      String(submission.attachments.length)
    );
    setConversationEntries((entries) => [...entries, {
      type: "message",
      id: `message-${++nextLiveMessageId}`,
      role: "user",
      text: displayText
    }]);
    if (submission.source === "composer") {
      setPrompt("");
      setReferencedFiles([]);
      setFileSearchResults([]);
      setFileSearchStatus("idle");
      fileSearchRequestRef.current = undefined;
      clearPendingAttachments(false);
    } else {
      updateWorktreeReviewDraft(undefined);
      setActiveSurface("conversation");
    }
    setEngineStatus("starting");
    setEngineMessage(t("engineStarting"));
  }

  function selectImageAttachments() {
    if (!imagePrompts || attachmentRequestIdRef.current ||
        engineStatus === "starting" || engineStatus === "running") {
      return;
    }
    const requestId = createMaintenanceRequestId();
    attachmentRequestIdRef.current = requestId;
    setAttachmentSelectionPending(true);
    setAttachmentError(undefined);
    bridge.send({ type: "attachment/select", requestId });
  }

  function removeAttachment(token: string) {
    const next = pendingAttachmentsRef.current.filter(
      (attachment) => attachment.token !== token);
    updatePendingAttachments(next);
    if (bridge.available) {
      bridge.send({ type: "attachment/discard", tokens: [token] });
    }
    setAttachmentError(undefined);
  }

  function updatePendingAttachments(attachments: PromptAttachment[]) {
    pendingAttachmentsRef.current = attachments;
    setPendingAttachments(attachments);
  }

  function clearPendingAttachments(discard = true) {
    const tokens = pendingAttachmentsRef.current.map((attachment) => attachment.token);
    updatePendingAttachments([]);
    attachmentRequestIdRef.current = undefined;
    setAttachmentSelectionPending(false);
    setAttachmentError(undefined);
    if (discard && bridge.available && tokens.length > 0) {
      bridge.send({ type: "attachment/discard", tokens });
    }
  }

  function updatePrompt(value: string) {
    setPrompt(value);
    setCommandPaletteIndex(0);
    setFileReferenceIndex(0);
    setCommandPaletteDismissed(false);
  }

  function updateMemoryFiles(files: MemoryFile[]) {
    memoryFilesRef.current = files;
    setMemoryFiles(files);
  }

  function updateSelectedMemoryFileId(fileId: string) {
    setSelectedMemoryFileId(fileId);
  }

  function resetMemoryBrowserDocuments() {
    memoryPendingRequestRef.current = undefined;
    updateMemoryFiles([]);
    updateSelectedMemoryFileId("");
    setMemoryContent("");
    setSavedMemoryContent("");
    setMemoryBrowserStatus("idle");
    setMemoryBrowserNotice(undefined);
    setMemoryListingTruncated(false);
    setMemoryMutationChallenge(undefined);
  }

  function resetMemoryBrowserState(preserveCapabilities = false) {
    resetMemoryBrowserDocuments();
    if (!preserveCapabilities) {
      memoryCapabilitiesRef.current = undefined;
      memoryCapabilitiesSessionRef.current = undefined;
      setMemoryCapabilities(undefined);
    }
  }

  function resetWorkspaceContextState() {
    agentsListRequestRef.current = undefined;
    agentsReadRequestRef.current = undefined;
    agentsWriteRequestRef.current = undefined;
    fileSearchRequestRef.current = undefined;
    setAgentsFiles([]);
    setSelectedAgentsPath("");
    setAgentsContent("");
    setSavedAgentsContent("");
    setAgentsEditorStatus("idle");
    setAgentsNotice("");
    setFileSearchResults([]);
    setFileSearchStatus("idle");
    setFileReferenceIndex(0);
    setReferencedFiles([]);
    resetMemoryBrowserDocuments();
  }

  function requestAgentsList() {
    if (!bridge.available || workspaceGenerationRef.current <= 0) {
      return;
    }
    const requestId = createMaintenanceRequestId();
    agentsListRequestRef.current = requestId;
    agentsReadRequestRef.current = undefined;
    setAgentsEditorStatus("loading");
    setAgentsNotice("");
    bridge.send({
      type: "workspace/context/instructions/list",
      requestId,
      workspaceGeneration: workspaceGenerationRef.current
    });
  }

  function requestAgentsFile(relativePath: string) {
    const requestId = createMaintenanceRequestId();
    agentsReadRequestRef.current = requestId;
    setAgentsEditorStatus("loading");
    setAgentsNotice("");
    bridge.send({
      type: "workspace/context/file/read",
      requestId,
      workspaceGeneration: workspaceGenerationRef.current,
      relativePath
    });
  }

  function saveAgentsInstructions() {
    if (!selectedAgentsPath || agentsEditorStatus === "saving" ||
        agentsContent === savedAgentsContent || agentsContentTooLarge) {
      return;
    }
    const requestId = createMaintenanceRequestId();
    agentsWriteRequestRef.current = requestId;
    agentsWriteContentRef.current = agentsContent;
    setAgentsEditorStatus("saving");
    setAgentsNotice("");
    bridge.send({
      type: "workspace/context/instructions/write",
      requestId,
      workspaceGeneration: workspaceGenerationRef.current,
      relativePath: selectedAgentsPath,
      content: agentsContent
    });
  }

  function requestMemoryFiles() {
    const sessionId = activeSessionIdRef.current;
    const capabilities = memoryCapabilitiesRef.current;
    if (!bridge.available || !sessionId || workspaceGenerationRef.current <= 0 ||
        memoryCapabilitiesSessionRef.current !== sessionId || !capabilities?.list ||
        !capabilities.read || memoryPendingRequestRef.current) {
      return;
    }
    const requestId = createMaintenanceRequestId();
    memoryPendingRequestRef.current = { requestId, operation: "list" };
    setMemoryBrowserStatus("loading");
    setMemoryBrowserNotice(undefined);
    setMemoryMutationChallenge(undefined);
    bridge.send({
      type: "memory/list",
      requestId,
      workspaceGeneration: workspaceGenerationRef.current,
      sessionId
    });
  }

  function requestMemoryFile(fileId: string, preserveNotice = false) {
    const sessionId = activeSessionIdRef.current;
    const capabilities = memoryCapabilitiesRef.current;
    if (!bridge.available || !sessionId || workspaceGenerationRef.current <= 0 ||
        memoryCapabilitiesSessionRef.current !== sessionId || !capabilities?.read ||
        !memoryFilesRef.current.some((file) => file.id === fileId) ||
        memoryPendingRequestRef.current) {
      return;
    }
    const requestId = createMaintenanceRequestId();
    memoryPendingRequestRef.current = { requestId, operation: "read", fileId };
    updateSelectedMemoryFileId(fileId);
    setMemoryBrowserStatus("loading");
    setMemoryMutationChallenge(undefined);
    if (!preserveNotice) {
      setMemoryBrowserNotice(undefined);
    }
    bridge.send({
      type: "memory/read",
      requestId,
      workspaceGeneration: workspaceGenerationRef.current,
      sessionId,
      fileId
    });
  }

  function beginMemoryMutation(
    operation: "write" | "delete",
    fileId: string,
    content: string | undefined,
    confirmed: boolean,
    confirmationToken?: string
  ) {
    const sessionId = activeSessionIdRef.current;
    const capabilities = memoryCapabilitiesRef.current;
    const file = memoryFilesRef.current.find((candidate) => candidate.id === fileId);
    const supported = operation === "write" ? capabilities?.write : capabilities?.delete;
    if (!bridge.available || !sessionId || workspaceGenerationRef.current <= 0 ||
        memoryCapabilitiesSessionRef.current !== sessionId ||
        capabilities?.mutationConfirmationRequired !== true || !supported || !file?.writable ||
        memoryPendingRequestRef.current ||
        (confirmed ? confirmationToken === undefined : confirmationToken !== undefined) ||
        (operation === "write" &&
          (content === undefined || new TextEncoder().encode(content).byteLength >
            memoryContentByteLimit))) {
      return;
    }

    const requestId = createMaintenanceRequestId();
    memoryPendingRequestRef.current = {
      requestId,
      operation,
      fileId,
      ...(content === undefined ? {} : { content }),
      confirmed
    };
    setMemoryBrowserStatus("mutating");
    setMemoryBrowserNotice(undefined);
    setMemoryMutationChallenge(undefined);
    if (operation === "write") {
      bridge.send({
        type: "memory/write",
        requestId,
        workspaceGeneration: workspaceGenerationRef.current,
        sessionId,
        fileId,
        content: content!,
        confirmed,
        ...(confirmationToken ? { confirmationToken } : {})
      });
    } else {
      bridge.send({
        type: "memory/delete",
        requestId,
        workspaceGeneration: workspaceGenerationRef.current,
        sessionId,
        fileId,
        confirmed,
        ...(confirmationToken ? { confirmationToken } : {})
      });
    }
  }

  function saveMemoryFile() {
    if (!selectedMemoryFileId || memoryContent === savedMemoryContent || memoryContentTooLarge) {
      return;
    }
    beginMemoryMutation("write", selectedMemoryFileId, memoryContent, false);
  }

  function deleteMemoryFile() {
    if (!selectedMemoryFileId) {
      return;
    }
    beginMemoryMutation("delete", selectedMemoryFileId, undefined, false);
  }

  function confirmMemoryMutation() {
    const challenge = memoryMutationChallenge;
    if (!challenge || challenge.fileId !== selectedMemoryFileId ||
        (challenge.operation === "write" && challenge.content !== memoryContent)) {
      setMemoryMutationChallenge(undefined);
      return;
    }
    beginMemoryMutation(
      challenge.operation,
      challenge.fileId,
      challenge.content,
      true,
      challenge.confirmationToken
    );
  }

  function cancelMemoryMutation() {
    setMemoryMutationChallenge(undefined);
    setMemoryBrowserNotice(undefined);
    setMemoryBrowserStatus("ready");
  }

  function selectFileReference(file: WorkspaceContextFile) {
    setReferencedFiles((files) => files.includes(file.relativePath)
      ? files
      : [...files, file.relativePath]);
    setPrompt(removeTrailingFileReference(prompt));
    setFileSearchResults([]);
    setFileSearchStatus("idle");
    setFileReferenceIndex(0);
    fileSearchRequestRef.current = undefined;
    queueMicrotask(() => composerRef.current?.focus());
  }

  function removeFileReference(relativePath: string) {
    setReferencedFiles((files) => files.filter((file) => file !== relativePath));
    queueMicrotask(() => composerRef.current?.focus());
  }

  function selectRuntimeCommand(command: RuntimeCommand) {
    setPrompt(`/${command.name} `);
    setCommandPaletteIndex(0);
    setCommandPaletteDismissed(true);
    queueMicrotask(() => composerRef.current?.focus());
  }

  function handleComposerKeyDown(event: React.KeyboardEvent<HTMLTextAreaElement>) {
    if (commandPaletteOpen && !event.nativeEvent.isComposing) {
      if (event.key === "ArrowDown") {
        event.preventDefault();
        setCommandPaletteIndex((index) => (index + 1) % commandMatches.length);
        return;
      }
      if (event.key === "ArrowUp") {
        event.preventDefault();
        setCommandPaletteIndex((index) =>
          (index - 1 + commandMatches.length) % commandMatches.length);
        return;
      }
      if (event.key === "Enter" && !event.ctrlKey && !event.metaKey) {
        event.preventDefault();
        selectRuntimeCommand(commandMatches[commandPaletteIndex] ?? commandMatches[0]);
        return;
      }
      if (event.key === "Escape") {
        event.preventDefault();
        setCommandPaletteDismissed(true);
        return;
      }
    }

    if (fileReferencePaletteOpen && !event.nativeEvent.isComposing) {
      if (event.key === "ArrowDown") {
        event.preventDefault();
        setFileReferenceIndex((index) => (index + 1) % fileSearchResults.length);
        return;
      }
      if (event.key === "ArrowUp") {
        event.preventDefault();
        setFileReferenceIndex((index) =>
          (index - 1 + fileSearchResults.length) % fileSearchResults.length);
        return;
      }
      if (event.key === "Enter" && !event.ctrlKey && !event.metaKey) {
        event.preventDefault();
        selectFileReference(fileSearchResults[fileReferenceIndex] ?? fileSearchResults[0]);
        return;
      }
    }

    if (!event.nativeEvent.isComposing && event.key === "Enter" &&
        (event.ctrlKey || event.metaKey)) {
      event.preventDefault();
      sendPrompt();
    }
  }

  function flushMemory() {
    const sessionId = activeSessionIdRef.current;
    if (!sessionId || memoryStatus === "running") {
      return;
    }
    setMemoryStatus("running");
    setMemoryMessage(t("memoryFlushRunning"));
    bridge.send({ type: "runtime/memory/flush", sessionId });
  }

  function updateSessionMode(mode: SessionMode) {
    sessionModeRef.current = mode;
    setSessionMode(mode);
  }

  function updateModeConfirmationPending(pending: boolean) {
    setModeConfirmationPending(pending);
  }

  function selectSessionMode(mode: SessionMode) {
    if (modeSelectionDisabled || (mode === "plan" && !planSelectable)) {
      return;
    }
    updateSessionMode(mode);
  }

  function handleSessionModeKeyDown(event: React.KeyboardEvent<HTMLButtonElement>) {
    let nextMode: SessionMode | undefined;
    if (event.key === "ArrowLeft" || event.key === "ArrowUp" || event.key === "Home") {
      nextMode = "default";
    } else if (event.key === "ArrowRight" || event.key === "ArrowDown" || event.key === "End") {
      nextMode = planSelectable ? "plan" : "default";
    }
    if (!nextMode) {
      return;
    }

    event.preventDefault();
    selectSessionMode(nextMode);
    (nextMode === "plan" ? planModeRef : executeModeRef).current?.focus();
  }

  function confirmNativeRisk() {
    if (pendingNativePrompt === undefined) {
      return;
    }

    const submission = pendingNativePrompt;
    if (!canSubmitPrompt(submission)) {
      setPendingNativePrompt(undefined);
      return;
    }
    setNativeRiskAcknowledged(true);
    setPendingNativePrompt(undefined);
    dispatchPrompt(submission, true);
  }

  function closeNativeRisk() {
    setPendingNativePrompt(undefined);
  }

  function selectWorkspace() {
    setNativeRiskAcknowledged(false);
    setPendingNativePrompt(undefined);
    bridge.send({ type: "workspace/select" });
  }

  function saveProvider() {
    const baseUrl = providerBaseUrl.trim();
    const model = providerModel.trim();
    if (!baseUrl || !model || providerValidationMessage) {
      return;
    }
    const replaceCredential = replaceProviderCredential || !canReuseProviderCredential;

    bridge.send({
      type: "provider/save",
      baseUrl,
      model,
      backend: providerBackend,
      allowInsecureTransport,
      useExistingCredential: !replaceCredential,
      replaceCredential
    });
    closeSettings();
  }

  function updateMaintenanceRequest(request: MaintenanceRequest | undefined) {
    maintenanceRequestRef.current = request;
    setMaintenanceRequest(request);
  }

  function updateWindowsAutomationPending(
    pending: WindowsAutomationPending | undefined
  ) {
    windowsAutomationPendingRef.current = pending;
    setWindowsAutomationPending(pending);
  }

  function toggleWindowsAutomation(enabled: boolean) {
    if (!enabled) {
      setWindowsAutomationEnabled(false);
      setWindowsAutomationHostEnabled(false);
      updateWindowsAutomationPending(undefined);
      setWindowsAutomationNotice(undefined);
      if (windowsAutomationValueRef.current) {
        windowsAutomationValueRef.current.value = "";
      }
      return;
    }
    if (!window.confirm(t("windowsAutomationEnableConfirm"))) {
      return;
    }
    setWindowsAutomationEnabled(true);
    setWindowsAutomationNotice(undefined);
  }

  function executeWindowsAutomation() {
    const processId = parseWindowsProcessId(windowsAutomationProcessId);
    const automationId = windowsAutomationId.trim();
    const name = windowsAutomationName.trim();
    if (!bridge.available || !windowsAutomationHostEnabled ||
        windowsAutomationPendingRef.current || processId === undefined ||
        (windowsAutomationAction !== "focus-window" &&
          !validWindowsAutomationTarget(automationId) && !validWindowsAutomationTarget(name))) {
      return;
    }

    const requestId = createMaintenanceRequestId();
    const command: HostCommand = {
      type: "windows/automation/execute",
      requestId,
      action: windowsAutomationAction,
      processId,
      ...(validWindowsAutomationTarget(automationId) ? { automationId } : {}),
      ...(validWindowsAutomationTarget(name) ? { name } : {}),
      ...(windowsAutomationAction === "set-value"
        ? { value: windowsAutomationValueRef.current?.value ?? "" }
        : {})
    };
    if (windowsAutomationValueRef.current) {
      windowsAutomationValueRef.current.value = "";
    }
    updateWindowsAutomationPending({ requestId, action: windowsAutomationAction, processId });
    setWindowsAutomationNotice({
      kind: "status",
      text: t("windowsAutomationRunning")
    });
    bridge.send(command);
  }

  function startMaintenance(operation: MaintenanceOperation) {
    if (maintenanceRequestRef.current || promptBusy ||
        (operation === "session-export" && !activeSessionIdRef.current) ||
        (operation === "update-check" && updatesUnsupported) ||
        (operation === "update-apply" && (!stagedUpdateVersion || updatesUnsupported))) {
      return;
    }

    const requestId = createMaintenanceRequestId();
    updateMaintenanceRequest({ requestId, operation });
    setMaintenanceNotice(undefined);
    switch (operation) {
      case "session-export":
        bridge.send({
          type: "session/export",
          requestId,
          sessionId: activeSessionIdRef.current!
        });
        break;
      case "session-import":
        bridge.send({ type: "session/import", requestId });
        break;
      case "backup-create":
        bridge.send({ type: "backup/create", requestId });
        break;
      case "backup-restore":
        bridge.send({ type: "backup/restore", requestId });
        break;
      case "update-check":
        bridge.send({ type: "update/check", requestId });
        break;
      case "update-apply":
        bridge.send({ type: "update/apply", requestId });
        break;
    }
  }

  function closeSettings() {
    setReplaceProviderCredential(false);
    setSettingsOpen(false);
  }

  function openSettings() {
    if (document.activeElement instanceof HTMLElement) {
      modalReturnFocusRef.current = document.activeElement;
    }
    setSettingsOpen(true);
    requestAgentsList();
    requestMemoryFiles();
  }

  function cancelPrompt() {
    if (!activeSessionId) {
      return;
    }

    bridge.send({ type: "engine/cancel", sessionId: activeSessionId });
    setPermissionQueue((requests) =>
      requests.filter((request) => request.sessionId !== activeSessionId));
  }

  function respondToPermission(outcome: "selected" | "cancelled", optionId?: string) {
    if (!visiblePermission) {
      return;
    }

    if (outcome === "selected" && optionId) {
      bridge.send({
        type: "permission/respond",
        requestId: visiblePermission.requestId,
        outcome,
        optionId
      });
    } else {
      bridge.send({
        type: "permission/respond",
        requestId: visiblePermission.requestId,
        outcome: "cancelled"
      });
    }

    setPermissionQueue((requests) =>
      requests.filter((request) => request.requestId !== visiblePermission.requestId));
  }

  const loadMoreSessionsButton = nextSessionCursor ? (
    <button
      type="button"
      className="load-more-sessions"
      disabled={loadingMoreSessions}
      onClick={loadMoreSessionResults}
    >
      {loadingMoreSessions
        ? <LoaderCircle className="session-spinner" size={13} />
        : <ChevronDown size={13} />}
      <span>{loadingMoreSessions ? t("loadingMore") : t("loadMore")}</span>
    </button>
  ) : undefined;

  return (
    <div className="app-shell">
      <aside
        className="tool-rail"
        aria-label={t("mainToolbar")}
        aria-hidden={interactionBlocked || undefined}
        inert={interactionBlocked || undefined}
      >
        <div className="brand-mark" aria-label="AgentDesk">A</div>
        <div className="rail-actions">
          <IconButton
            label={t("workspace")}
            active={activeSurface === "conversation"}
            onClick={() => {
              if (activeSurface !== "conversation") {
                setActiveSurface("conversation");
              } else {
                selectWorkspace();
              }
            }}
          ><FolderKanban size={19} /></IconButton>
          <IconButton
            label={t("agents")}
            active={activeSurface === "agents"}
            onClick={() => setActiveSurface("agents")}
          ><Bot size={19} /></IconButton>
          <IconButton
            label={t("worktrees")}
            active={activeSurface === "worktrees"}
            onClick={() => setActiveSurface("worktrees")}
          ><GitBranch size={19} /></IconButton>
        </div>
        <div className="rail-actions rail-actions-bottom">
          <IconButton
            label={t("settings")}
            buttonRef={settingsButtonRef}
            onClick={openSettings}
          >
            <Settings size={19} />
          </IconButton>
        </div>
      </aside>

      <nav
        className="task-sidebar"
        aria-label={t("sessionNavigation")}
        aria-hidden={interactionBlocked || undefined}
        inert={interactionBlocked || undefined}
      >
        <div className="sidebar-header">
          <div>
            <span className="product-name">AgentDesk</span>
            <span className="channel-label">ALPHA</span>
          </div>
        </div>

        <label className="search-box">
          <Search size={15} aria-hidden="true" />
          <input
            ref={sessionSearchRef}
            type="search"
            aria-label={t("searchSessions")}
            placeholder={t("searchSessions")}
            value={sessionQuery}
            onChange={(event) => {
              sessionQueryRef.current = event.target.value;
              setSessionQuery(event.target.value);
            }}
          />
          <kbd>Ctrl K</kbd>
        </label>

        <div className="session-view-tabs" role="tablist" aria-label={t("sessionViews")}>
          <button
            ref={activeSessionsTabRef}
            id="active-sessions-tab"
            type="button"
            role="tab"
            aria-selected={!archivedSessionsView}
            aria-controls="session-results"
            tabIndex={!archivedSessionsView ? 0 : -1}
            onClick={() => selectSessionView(false)}
            onKeyDown={handleSessionViewKeyDown}
          >{t("activeSessions")}</button>
          <button
            ref={archivedSessionsTabRef}
            id="archived-sessions-tab"
            type="button"
            role="tab"
            aria-selected={archivedSessionsView}
            aria-controls="session-results"
            tabIndex={archivedSessionsView ? 0 : -1}
            onClick={() => selectSessionView(true)}
            onKeyDown={handleSessionViewKeyDown}
          >{t("archivedSessions")}</button>
        </div>

        <div
          id="session-results"
          className="session-section"
          role="tabpanel"
          aria-labelledby={archivedSessionsView ? "archived-sessions-tab" : "active-sessions-tab"}
        >
          <div className="section-heading">
            <span>{t("sessions")}</span>
            {sessionListStatus === "loaded" && sessions.length > 0 && (
              <span className="session-count">{sessions.length}</span>
            )}
          </div>
          <div className={sessionListStatus === "loaded" && sessions.length > 0
            ? "session-list-shell"
            : "session-list"}>
            {!workspaceReady && (
              <>
                <p className="session-empty">{t("workspaceNotSelected")}</p>
                <button
                  type="button"
                  className="load-more-sessions"
                  onClick={selectWorkspace}
                >
                  <FolderKanban size={13} />
                  <span>{t("selectWorkspace")}</span>
                </button>
              </>
            )}
            {workspaceReady && sessionListStatus === "loading" && (
              <div
                className="session-list-state"
                role={activeSurface === "conversation" ? "status" : undefined}
              >
                <LoaderCircle className="session-spinner" size={14} />
                <span>{t("loadingSessions")}</span>
              </div>
            )}
            {workspaceReady && sessionListStatus === "error" && (
              <div className="session-list-error" role="alert">
                <span>{sessionListError || t("sessionListError")}</span>
                <button type="button" onClick={() => requestSessionList()}>
                  <RefreshCw size={12} />
                  <span>{t("retry")}</span>
                </button>
              </div>
            )}
            {workspaceReady && sessionListStatus === "loaded" && sessions.length === 0 && (
              <>
                <p className="session-empty">
                  {t(archivedSessionsView ? "noArchivedSessions" : "noSessions")}
                </p>
                {loadMoreSessionsButton}
              </>
            )}
            {workspaceReady && sessionListStatus === "loaded" && sessions.length > 0 && (
              <VirtualizedList
                className="session-list"
                items={sessions}
                getKey={(session) => session.sessionId}
                rowHeight={sessionListRowHeight}
                overscan={sessionListOverscan}
                ariaLabel={t("sessions")}
                renderItem={(session, index) => {
                  const active = session.sessionId === activeSessionId;
                  const editing = session.sessionId === editingSessionId;
                  const archiving = archivingSessionIds.has(session.sessionId);
                  const forking = forkingSessionIds.has(session.sessionId);
                  return (
                    <div className={`session-item${active ? " active" : ""}`}>
                  {editing ? (
                    <form
                      className="session-rename-form"
                      onSubmit={(event) => {
                        event.preventDefault();
                        saveSessionRename(session);
                      }}
                    >
                      <input
                        ref={renameInputRef}
                        aria-label={t("sessionTitle")}
                        value={sessionTitleDraft}
                        maxLength={160}
                        onChange={(event) => setSessionTitleDraft(event.target.value)}
                        onKeyDown={(event) => {
                          if (!event.nativeEvent.isComposing && event.key === "Enter") {
                            event.preventDefault();
                            saveSessionRename(session);
                          } else if (event.key === "Escape") {
                            event.preventDefault();
                            closeSessionRename();
                          }
                        }}
                      />
                      <button
                        type="submit"
                        aria-label={t("saveRename")}
                        title={t("saveRename")}
                        disabled={!sessionTitleDraft.trim()}
                      ><Check size={13} /></button>
                      <button
                        type="button"
                        aria-label={t("cancelRename")}
                        title={t("cancelRename")}
                        onClick={closeSessionRename}
                      ><X size={13} /></button>
                    </form>
                  ) : (
                    <>
                      <button
                        type="button"
                        className="session-row"
                        data-virtual-index={index}
                        data-session-focus-id={session.sessionId}
                        data-session-focus-target="row"
                        aria-label={t("openSessionLabel").replace("{title}", session.title)}
                        aria-current={active ? "page" : undefined}
                        disabled={engineStatus === "starting" || engineStatus === "running"}
                        onDoubleClick={() => openSession(session)}
                        onContextMenu={(event) => {
                          event.preventDefault();
                          startSessionRename(session, "row");
                        }}
                        onKeyDown={(event) => {
                          if (event.key === "Enter") {
                            event.preventDefault();
                            openSession(session);
                          }
                        }}
                      >
                        <span className="session-active-marker" aria-hidden="true" />
                        <span className="session-copy">
                          <span className="session-title">{session.title}</span>
                          <span className="session-meta">
                            {workspaceName(session.workspacePath)} · {t("messageCount").replace(
                              "{count}", String(session.messageCount))}
                          </span>
                          <span className="session-detail">
                            {(session.worktreeLabel || session.branch) && (
                              <span>{session.worktreeLabel || session.branch}</span>
                            )}
                            <time dateTime={session.updatedAt}>
                              {formatSessionTime(session.updatedAt, language)}
                            </time>
                          </span>
                        </span>
                      </button>
                      <button
                        type="button"
                        className="session-fork-button"
                        aria-label={t("forkSessionLabel").replace("{title}", session.title)}
                        title={t("forkSession")}
                        disabled={forking || engineStatus === "starting" || engineStatus === "running"}
                        onClick={() => forkSession(session)}
                      >
                        {forking
                          ? <LoaderCircle className="session-spinner" size={12} />
                          : <GitFork size={12} />}
                      </button>
                      <button
                        type="button"
                        className="session-rename-button"
                        data-session-focus-id={session.sessionId}
                        data-session-focus-target="rename"
                        aria-label={t("renameSessionLabel").replace("{title}", session.title)}
                        title={t("renameSession")}
                        onClick={() => startSessionRename(session, "rename")}
                      ><Pencil size={12} /></button>
                      <button
                        type="button"
                        className="session-archive-button"
                        aria-label={t(archivedSessionsView
                          ? "restoreSessionLabel"
                          : "archiveSessionLabel").replace("{title}", session.title)}
                        title={t(archivedSessionsView ? "restoreSession" : "archiveSession")}
                        disabled={archiving}
                        onClick={() => archiveSession(session)}
                      >
                        {archiving
                          ? <LoaderCircle className="session-spinner" size={12} />
                          : archivedSessionsView
                            ? <ArchiveRestore size={12} />
                            : <Archive size={12} />}
                      </button>
                    </>
                  )}
                    </div>
                  );
                }}
                footer={loadMoreSessionsButton}
              />
            )}
          </div>
        </div>
      </nav>

      {activeSurface === "agents" ? (
        <RuntimeDashboard
          activeSessionId={activeSessionId}
          backgroundTasks={backgroundTasks}
          subagents={runningSubagents}
          selectedSubagent={selectedSubagent}
          status={runtimeDashboardStatus}
          error={runtimeDashboardError}
          actionStatus={runtimeActionStatus}
          pendingTaskKills={pendingTaskKills}
          pendingSubagentCancels={pendingSubagentCancels}
          interactionBlocked={interactionBlocked}
          onRefresh={requestRuntimeDashboard}
          onKillTask={killBackgroundTask}
          onInspectSubagent={inspectSubagent}
          onCancelSubagent={cancelSubagent}
          language={language}
          t={t}
        />
      ) : activeSurface === "worktrees" ? (
        <WorktreeDashboard
          worktrees={worktrees}
          selectedWorktree={selectedWorktree}
          status={worktreeStatus}
          error={worktreeError}
          actionStatus={worktreeActionStatus}
          conflicts={worktreeConflicts}
          operation={worktreeOperation}
          activeSessionId={activeSessionId}
          engineStatus={engineStatus}
          workspaceGeneration={workspaceGeneration}
          reviewDraft={worktreeReviewDraft}
          interactionBlocked={interactionBlocked}
          onRefresh={requestWorktrees}
          onCreate={createWorktree}
          onInspect={inspectWorktree}
          onReview={reviewWorktree}
          onReviewChange={editWorktreeReview}
          onStartReview={startWorktreeReview}
          onCancelReview={() => updateWorktreeReviewDraft(undefined)}
          onApply={applyWorktree}
          onRemove={removeWorktree}
          onGc={gcWorktrees}
          language={language}
          t={t}
        />
      ) : (
      <main
        className="conversation"
        aria-label={t("conversation")}
        aria-hidden={interactionBlocked || undefined}
        inert={interactionBlocked || undefined}
      >
        <header className="conversation-header">
          <div className="conversation-title">
            <h1>{currentTaskTitle}</h1>
            <div className="workspace-path" title={workspacePath || t("workspaceNotSelected")}>
              {workspacePath || t("workspaceNotSelected")}
            </div>
          </div>
          <div className="history-toolbar">
            {historyStatus && (
              <span className="history-status" role="status" aria-live="polite">
                {historyStatus}
              </span>
            )}
            {activeSessionId && (
              <>
                <button
                  type="button"
                  className="history-action"
                  aria-label={t("flushMemory")}
                  title={t("flushMemory")}
                  disabled={memoryStatus === "running" || engineStatus === "starting" ||
                    engineStatus === "running"}
                  onClick={flushMemory}
                >
                  <Brain className={memoryStatus === "running" ? "session-spinner" : undefined}
                    size={13} />
                  <span>{t("memory")}</span>
                </button>
                <button
                  type="button"
                  className="history-action"
                  aria-label={t("compactSession")}
                  title={t("compactSession")}
                  disabled={compactingSessionId !== undefined || engineStatus === "starting" ||
                    engineStatus === "running"}
                  onClick={compactActiveSession}
                >
                  {compactingSessionId
                    ? <LoaderCircle className="session-spinner" size={13} />
                    : <Minimize2 size={13} />}
                  <span>{t("compact")}</span>
                </button>
                <button
                  type="button"
                  className="history-action"
                  aria-label={t("rewindSession")}
                  title={t("rewindSession")}
                  disabled={engineStatus === "starting" || engineStatus === "running"}
                  onClick={openRewind}
                >
                  <History size={13} />
                  <span>{t("rewind")}</span>
                </button>
              </>
            )}
          </div>
        </header>

        <div className="transcript">
          {conversationEntries.map((entry) => entry.type === "message" ? (
            <article
              className={`message ${entry.role === "user" ? "user-message" : "assistant-message"}`}
              key={entry.id}
            >
              <div className={`message-author${entry.role === "assistant" ? " agent-author" : ""}`}>
                {entry.role === "assistant" && <span className="agent-dot" />}
                {entry.role === "assistant" ? "AgentDesk" : t("you")}
              </div>
              <MarkdownMessage text={entry.text} />
            </article>
          ) : (
            <ToolTimeline key={entry.id} entry={entry} t={t} />
          ))}

          {(engineStatus === "starting" || engineStatus === "running") && (
            <div className="running-row">
              <span className="running-indicator" />
              <span>{engineMessage || t("running")}</span>
            </div>
          )}

        </div>

        <div className="composer-wrap">
          <div className="composer">
            <textarea
              ref={composerRef}
              rows={3}
              placeholder={t("promptPlaceholder")}
              aria-label={t("promptPlaceholder")}
              aria-controls={fileReferencePaletteOpen ? "file-reference-results" : undefined}
              aria-activedescendant={fileReferencePaletteOpen
                ? `file-reference-option-${fileReferenceIndex}`
                : undefined}
              value={prompt}
              onChange={(event) => updatePrompt(event.target.value)}
              onKeyDown={handleComposerKeyDown}
            />
            {commandPaletteOpen && (
              <div
                className="command-palette"
                role="listbox"
                aria-label={t("commandPalette")}
              >
                {commandMatches.map((command, index) => (
                  <button
                    key={command.name}
                    type="button"
                    role="option"
                    aria-selected={index === commandPaletteIndex}
                    className="command-option"
                    onMouseDown={(event) => event.preventDefault()}
                    onClick={() => selectRuntimeCommand(command)}
                  >
                    <span className="command-name">/{command.name}</span>
                    <span className="command-description">{command.description}</span>
                    <span className="command-meta">
                      {command.input?.hint && <span>{command.input.hint}</span>}
                      {command.skill && <span>{t(`skillScope_${command.skill.scope}`)}</span>}
                    </span>
                  </button>
                ))}
              </div>
            )}
            {fileSearchStatus === "loaded" && fileSearchResults.length > 0 &&
              fileReferenceQuery !== undefined && (
              <div
                id="file-reference-results"
                className="command-palette file-reference-palette"
                role="listbox"
                aria-label={t("fileSearchResults")}
              >
                {fileSearchResults.map((file, index) => (
                  <button
                    key={file.relativePath}
                    id={`file-reference-option-${index}`}
                    type="button"
                    role="option"
                    aria-label={file.relativePath}
                    aria-selected={index === fileReferenceIndex}
                    className="command-option file-reference-option"
                    onMouseDown={(event) => event.preventDefault()}
                    onClick={() => selectFileReference(file)}
                  >
                    <span className="command-name">{file.relativePath}</span>
                    <span className="command-description">
                      {formatFileSize(file.byteLength)}
                    </span>
                  </button>
                ))}
              </div>
            )}
            {fileReferenceQuery !== undefined && fileSearchStatus !== "idle" &&
              fileSearchStatus !== "loaded" && (
              <div
                className={`command-palette file-reference-status ${fileSearchStatus}`}
                role={fileSearchStatus === "error" ? "alert" : "status"}
              >
                {fileSearchStatus === "loading" && <LoaderCircle size={14} />}
                <span>{t(fileSearchStatus === "loading"
                  ? "fileSearchLoading"
                  : fileSearchStatus === "empty"
                    ? "fileSearchEmpty"
                    : "fileSearchError")}</span>
              </div>
            )}
            {referencedFiles.length > 0 && (
              <div className="file-reference-strip" aria-label={t("referencedFiles")}>
                {referencedFiles.map((relativePath) => (
                  <div className="file-reference-chip" key={relativePath}>
                    <span title={relativePath}>{relativePath}</span>
                    <button
                      type="button"
                      aria-label={t("removeFileReference").replace("{name}", relativePath)}
                      title={t("removeFileReference").replace("{name}", relativePath)}
                      onClick={() => removeFileReference(relativePath)}
                    ><X size={12} /></button>
                  </div>
                ))}
              </div>
            )}
            {pendingAttachments.length > 0 && (
              <div className="attachment-strip" aria-label={t("selectedImages")}>
                {pendingAttachments.map((attachment) => (
                  <div className="attachment-preview" key={attachment.token}>
                    <Paperclip size={14} aria-hidden="true" />
                    <span title={attachment.name}>{attachment.name}</span>
                    <button
                      type="button"
                      aria-label={t("removeImage").replace("{name}", attachment.name)}
                      title={t("removeImage").replace("{name}", attachment.name)}
                      onClick={() => removeAttachment(attachment.token)}
                    ><X size={12} /></button>
                  </div>
                ))}
              </div>
            )}
            <div className="composer-toolbar">
              <div className="composer-left">
                <button
                  type="button"
                  className="composer-icon-button"
                  aria-label={t("attachImage")}
                  title={t("attachImage")}
                  disabled={!imagePrompts || attachmentSelectionPending ||
                    engineStatus === "starting" || engineStatus === "running"}
                  onClick={selectImageAttachments}
                ><Paperclip size={14} /></button>
                <div
                  className="session-mode-segment"
                  role="radiogroup"
                  aria-label={t("sessionMode")}
                >
                  <button
                    ref={executeModeRef}
                    type="button"
                    className="mode-button"
                    role="radio"
                    aria-checked={sessionMode === "default"}
                    data-confirmed={confirmedSessionMode === "default"}
                    data-pending={sessionMode === "default" && modeAwaitingConfirmation}
                    tabIndex={sessionMode === "default" ? 0 : -1}
                    disabled={modeSelectionDisabled}
                    onClick={() => selectSessionMode("default")}
                    onKeyDown={handleSessionModeKeyDown}
                  >
                    <Play size={13} fill="currentColor" />
                    <span>{t("execute")}</span>
                  </button>
                  <button
                    ref={planModeRef}
                    type="button"
                    className="mode-button"
                    role="radio"
                    aria-checked={sessionMode === "plan"}
                    aria-description={!planSelectable ? t("planUnavailable") : undefined}
                    title={!planSelectable ? t("planUnavailable") : t("plan")}
                    data-confirmed={confirmedSessionMode === "plan"}
                    data-pending={sessionMode === "plan" && modeAwaitingConfirmation}
                    tabIndex={sessionMode === "plan" ? 0 : -1}
                    disabled={!planSelectable || modeSelectionDisabled}
                    onClick={() => selectSessionMode("plan")}
                    onKeyDown={handleSessionModeKeyDown}
                  >
                    <ListTodo size={13} />
                    <span>{t("plan")}</span>
                  </button>
                </div>
                <div
                  className="profile-segment"
                  role="group"
                  aria-label={t("executionMode")}
                >
                  <button
                    type="button"
                    className="profile-button"
                    aria-label={t("nativeProtected")}
                    aria-pressed={executionProfile === "NativeProtected"}
                    disabled={!nativeAvailable || profileSelectionDisabled}
                    onClick={() => setExecutionProfile("NativeProtected")}
                  >
                    <ShieldAlert size={14} />
                    <span>{t("nativeShort")}</span>
                  </button>
                  <button
                    type="button"
                    className="profile-button"
                    aria-label={t("wslStrict")}
                    aria-pressed={executionProfile === "WslStrict"}
                    title={wslAvailable ? t("wslStrict") : wslUnavailableMessage}
                    disabled={!wslAvailable || profileSelectionDisabled}
                    onClick={() => setExecutionProfile("WslStrict")}
                  >
                    <ShieldCheck size={14} />
                    <span>WSL2</span>
                  </button>
                </div>
              </div>
              {engineStatus === "running" && activeSessionId ? (
                <button className="stop-button" aria-label={t("stop")} title={t("stop")} onClick={cancelPrompt}>
                  <Square size={13} fill="currentColor" />
                </button>
              ) : (
                <button
                  ref={sendButtonRef}
                  className="send-button"
                  aria-label={t("send")}
                  title={t("send")}
                  disabled={!workspaceReady || (!prompt.trim() && pendingAttachments.length === 0 &&
                    referencedFiles.length === 0) ||
                    engineStatus === "starting" ||
                    engineStatus === "running" ||
                    !executionProfiles.includes(executionProfile)}
                  onClick={sendPrompt}
                ><Send size={17} /></button>
              )}
            </div>
          </div>
          <div className="composer-status">
            {previewMode && <><span>{t("previewContext")}</span><span>·</span></>}
            <span>{engineMessage || t("localWorkspace")}</span>
            {modeStatusMessage && <><span>·</span><span role="status">{modeStatusMessage}</span></>}
            {!wslAvailable && <><span>·</span><span>{wslUnavailableMessage}</span></>}
            {!imagePrompts && <><span>·</span><span>{t("imagePromptsUnsupported")}</span></>}
            {attachmentError && (
              <><span>·</span><span role="alert">{t(attachmentErrorKey(attachmentError))}</span></>
            )}
            {runtimeCommandsError && <><span>·</span><span>{runtimeCommandsError}</span></>}
            {memoryMessage && <><span>·</span><span role="status">{memoryMessage}</span></>}
          </div>
        </div>
      </main>
      )}

      {rewindOpen && (
        <div className="modal-backdrop" role="presentation">
          <section
            ref={rewindDialogRef}
            className="rewind-dialog"
            role="dialog"
            tabIndex={-1}
            aria-modal="true"
            aria-labelledby="rewind-title"
          >
            <header className="dialog-header rewind-header">
              <div>
                <History size={17} />
                <h2 id="rewind-title">{t("rewindSession")}</h2>
              </div>
              <button
                type="button"
                className="icon-button"
                aria-label={t("close")}
                title={t("close")}
                disabled={rewindSubmitting}
                onClick={closeRewind}
              ><X size={17} /></button>
            </header>
            <div className="rewind-body">
              {rewindPointsLoading ? (
                <div className="rewind-loading" role="status">
                  <LoaderCircle className="session-spinner" size={15} />
                  <span>{t("loadingRewindPoints")}</span>
                </div>
              ) : rewindPoints.length === 0 ? (
                <p className="rewind-empty">{rewindError || t("noRewindPoints")}</p>
              ) : (
                <>
                  <div className="rewind-section-heading">{t("rewindCheckpoint")}</div>
                  <div
                    className="rewind-points"
                    role="radiogroup"
                    aria-label={t("rewindCheckpoint")}
                  >
                    {rewindPoints.map((point, index) => {
                      const selected = point.promptIndex === selectedRewindPoint;
                      const checkpoint = t("checkpointNumber").replace(
                        "{count}",
                        String(point.promptIndex + 1)
                      );
                      const preview = point.promptPreview || t("promptUnavailable");
                      return (
                        <button
                          key={point.promptIndex}
                          type="button"
                          role="radio"
                          className="rewind-point"
                          aria-checked={selected}
                          aria-label={`${checkpoint}：${preview}`}
                          tabIndex={selected || (selectedRewindPoint === undefined && index === 0)
                            ? 0
                            : -1}
                          disabled={rewindSubmitting}
                          data-prompt-index={point.promptIndex}
                          onClick={() => selectRewindPoint(point.promptIndex)}
                          onKeyDown={(event) => handleRewindPointKeyDown(event, index)}
                        >
                          <span className="rewind-point-marker" aria-hidden="true" />
                          <span className="rewind-point-copy">
                            <strong>{preview}</strong>
                            <small>
                              <span>{checkpoint}</span>
                              <time dateTime={point.createdAt}>
                                {formatSessionTime(point.createdAt, language)}
                              </time>
                              <span>{t("snapshotCount").replace(
                                "{count}",
                                String(point.fileSnapshotCount)
                              )}</span>
                            </small>
                          </span>
                          {point.hasFileChanges && (
                            <span className="rewind-file-badge">{t("hasFileChanges")}</span>
                          )}
                        </button>
                      );
                    })}
                  </div>

                  <div className="rewind-section-heading">{t("rewindScope")}</div>
                  <div
                    className="rewind-mode-segment"
                    role="radiogroup"
                    aria-label={t("rewindScope")}
                  >
                    {(["all", "conversation_only", "files_only"] as const).map((mode, index) => (
                      <button
                        key={mode}
                        type="button"
                        role="radio"
                        aria-checked={rewindMode === mode}
                        tabIndex={rewindMode === mode ? 0 : -1}
                        disabled={rewindSubmitting}
                        data-rewind-mode={mode}
                        onClick={() => selectRewindMode(mode)}
                        onKeyDown={(event) => handleRewindModeKeyDown(event, index)}
                      >{t(rewindModeTranslationKey(mode))}</button>
                    ))}
                  </div>
                </>
              )}

              {rewindFailure && (
                <div className="rewind-conflicts" role="alert">
                  <strong>{rewindFailure.conflicts.length > 0
                    ? t("rewindConflictsTitle")
                    : t("rewindFailed")}</strong>
                  {rewindFailure.error && <p>{rewindFailure.error}</p>}
                  {rewindFailure.conflicts.length > 0 && (
                    <ul>
                      {rewindFailure.conflicts.map((conflict) => (
                        <li key={`${conflict.path}:${conflict.conflictType}`}>
                          <code>{conflict.path}</code>
                          <span>{conflict.conflictType}</span>
                        </li>
                      ))}
                    </ul>
                  )}
                </div>
              )}

              <div className="dialog-actions rewind-actions">
                <button
                  ref={rewindCancelButtonRef}
                  type="button"
                  className="secondary-button"
                  disabled={rewindSubmitting}
                  onClick={closeRewind}
                >{t("cancel")}</button>
                {rewindFailure?.conflicts.length ? (
                  <button
                    type="button"
                    className="force-button"
                    disabled={rewindSubmitting}
                    onClick={() => submitRewind(true)}
                  >{t("forceRewind")}</button>
                ) : (
                  <button
                    type="button"
                    className="rewind-button"
                    disabled={rewindPointsLoading || selectedRewindPoint === undefined ||
                      rewindSubmitting}
                    onClick={() => submitRewind(false)}
                  >{rewindSubmitting ? t("rewinding") : t("rewindToPoint")}</button>
                )}
              </div>
            </div>
          </section>
        </div>
      )}

      {nativeRiskOpen && (
        <div className="modal-backdrop" role="presentation">
          <section
            ref={nativeRiskDialogRef}
            className="native-risk-dialog"
            role="alertdialog"
            tabIndex={-1}
            aria-modal="true"
            aria-labelledby="native-risk-title"
            aria-describedby="native-risk-description"
          >
            <header className="dialog-header native-risk-header">
              <div>
                <ShieldAlert size={17} />
                <h2 id="native-risk-title">{t("nativeRiskTitle")}</h2>
              </div>
            </header>
            <div className="native-risk-body">
              <p id="native-risk-description">{t("nativeRiskBody")}</p>
              <div className="native-risk-workspace" title={workspacePath}>
                <FolderKanban size={15} />
                <span>{workspacePath}</span>
              </div>
              <div className="dialog-actions">
                <button
                  ref={nativeRiskCancelButtonRef}
                  type="button"
                  className="secondary-button"
                  onClick={closeNativeRisk}
                >{t("nativeRiskCancel")}</button>
                <button
                  type="button"
                  className="risk-confirm-button"
                  onClick={confirmNativeRisk}
                >{t("nativeRiskContinue")}</button>
              </div>
            </div>
          </section>
        </div>
      )}

      {visiblePermission && (
        <div className="modal-backdrop" role="presentation">
          <section
            ref={permissionDialogRef}
            className="permission-dialog"
            role="dialog"
            tabIndex={-1}
            aria-modal="true"
            aria-labelledby="permission-title"
          >
            <header className="dialog-header permission-header">
              <div>
                <ShieldAlert size={17} />
                <h2 id="permission-title">{t("permissionTitle")}</h2>
              </div>
              {permissionQueue.length > 1 && (
                <span className="permission-queue-count">
                  1 / {permissionQueue.length}
                </span>
              )}
            </header>
            <div className="permission-body">
              <div className="permission-summary">
                {visiblePermission.toolKind && (
                  <code className="permission-kind">{visiblePermission.toolKind}</code>
                )}
                <h3>{visiblePermission.title}</h3>
              </div>

              {visiblePermission.locations.length > 0 && (
                <section className="permission-detail" aria-labelledby="permission-locations">
                  <h4 id="permission-locations">{t("permissionLocations")}</h4>
                  <ul>
                    {visiblePermission.locations.map((location) => (
                      <li key={location}><code>{location}</code></li>
                    ))}
                  </ul>
                </section>
              )}

              {Object.hasOwn(visiblePermission, "rawInput") && (
                <section className="permission-detail" aria-labelledby="permission-input">
                  <h4 id="permission-input">{t("permissionInput")}</h4>
                  <pre>{formatPermissionInput(visiblePermission.rawInput)}</pre>
                </section>
              )}

              <div className="permission-options">
                {visiblePermission.options.map((option) => {
                  const rejecting = option.kind.startsWith("reject_");
                  return (
                    <button
                      key={option.optionId}
                      type="button"
                      className={`permission-option${rejecting ? " reject" : " allow"}`}
                      aria-label={option.name}
                      onClick={() => respondToPermission("selected", option.optionId)}
                    >
                      {rejecting ? <X size={16} /> : <Check size={16} />}
                      <span>
                        <strong>{option.name}</strong>
                        <small>{permissionKindLabel(option.kind, t)}</small>
                      </span>
                    </button>
                  );
                })}
              </div>

              <button
                ref={permissionCancelButtonRef}
                type="button"
                className="secondary-button permission-cancel"
                onClick={() => respondToPermission("cancelled")}
              >{t("permissionCancel")}</button>
            </div>
          </section>
        </div>
      )}

      {worktreeCreateOpen && (
        <div className="modal-backdrop" role="presentation">
          <section
            className="worktree-operation-dialog"
            role="dialog"
            aria-modal="true"
            aria-labelledby="worktree-create-title"
          >
            <header className="dialog-header">
              <div>
                <GitBranch size={16} />
                <h2 id="worktree-create-title">{t("createWorktree")}</h2>
              </div>
              <button
                type="button"
                className="icon-button"
                aria-label={t("close")}
                title={t("close")}
                onClick={() => setWorktreeCreateOpen(false)}
              ><X size={17} /></button>
            </header>
            <form
              className="worktree-operation-form"
              onSubmit={(event) => {
                event.preventDefault();
                submitWorktreeCreate();
              }}
            >
              <label className="provider-field">
                <span>{t("worktreeCopyMode")}</span>
                <select
                  autoFocus
                  value={worktreeCreateDraft.copyMode}
                  onChange={(event) => setWorktreeCreateDraft((current) => ({
                    ...current,
                    copyMode: event.target.value as WorktreeCreateDraft["copyMode"]
                  }))}
                >
                  <option value="dirty">{t("worktreeCopyDirty")}</option>
                  <option value="clean">{t("worktreeCopyClean")}</option>
                </select>
              </label>
              <label className="provider-field">
                <span>{t("creationType")}</span>
                <select
                  value={worktreeCreateDraft.creationType}
                  onChange={(event) => setWorktreeCreateDraft((current) => ({
                    ...current,
                    creationType: event.target.value as WorktreeCreateDraft["creationType"]
                  }))}
                >
                  <option value="linked">{t("worktreeCreation_linked")}</option>
                  <option value="standalone">{t("worktreeCreation_standalone")}</option>
                  <option value="git">{t("worktreeCreation_git")}</option>
                </select>
              </label>
              <label className="provider-field">
                <span>{t("gitReference")}</span>
                <input
                  type="text"
                  value={worktreeCreateDraft.gitReference}
                  onChange={(event) => setWorktreeCreateDraft((current) => ({
                    ...current,
                    gitReference: event.target.value
                  }))}
                  placeholder="HEAD"
                />
              </label>
              <label className="provider-field">
                <span>{t("worktreeLabel")}</span>
                <input
                  type="text"
                  maxLength={256}
                  value={worktreeCreateDraft.label}
                  onChange={(event) => setWorktreeCreateDraft((current) => ({
                    ...current,
                    label: event.target.value
                  }))}
                />
              </label>
              <label className="provider-field">
                <span>{t("worktreeDestination")}</span>
                <input
                  type="text"
                  value={worktreeCreateDraft.destinationPath}
                  onChange={(event) => setWorktreeCreateDraft((current) => ({
                    ...current,
                    destinationPath: event.target.value
                  }))}
                  placeholder={t("worktreeDestinationAuto")}
                />
              </label>
              <div className="dialog-actions">
                <button
                  type="button"
                  className="secondary-button"
                  onClick={() => setWorktreeCreateOpen(false)}
                >{t("cancel")}</button>
                <button type="submit" className="primary-button">{t("createWorktree")}</button>
              </div>
            </form>
          </section>
        </div>
      )}

      {worktreeApplyTarget && (
        <div className="modal-backdrop" role="presentation">
          <section
            className="worktree-operation-dialog"
            role="dialog"
            aria-modal="true"
            aria-labelledby="worktree-apply-title"
          >
            <header className="dialog-header">
              <div>
                <GitMerge size={16} />
                <h2 id="worktree-apply-title">{t("applyWorktree")}</h2>
              </div>
              <button
                type="button"
                className="icon-button"
                aria-label={t("close")}
                title={t("close")}
                onClick={() => setWorktreeApplyTarget(undefined)}
              ><X size={17} /></button>
            </header>
            <form
              className="worktree-operation-form"
              onSubmit={(event) => {
                event.preventDefault();
                if (window.confirm(t("worktreeApplyConfirm").replace(
                  "{label}",
                  worktreeLabel(worktreeApplyTarget)
                ))) {
                  submitWorktreeApply();
                }
              }}
            >
              <p className="worktree-operation-summary">
                <strong>{worktreeLabel(worktreeApplyTarget)}</strong>
                <code>{worktreeApplyTarget.path}</code>
              </p>
              <fieldset className="worktree-mode-options">
                <legend>{t("worktreeApplyMode")}</legend>
                <label>
                  <input
                    type="radio"
                    name="worktree-apply-mode"
                    value="merge"
                    checked={worktreeApplyMode === "merge"}
                    onChange={() => setWorktreeApplyMode("merge")}
                  />
                  <span>{t("worktreeApplyMerge")}</span>
                </label>
                <label>
                  <input
                    type="radio"
                    name="worktree-apply-mode"
                    value="overwrite"
                    checked={worktreeApplyMode === "overwrite"}
                    onChange={() => setWorktreeApplyMode("overwrite")}
                  />
                  <span>{t("worktreeApplyOverwrite")}</span>
                </label>
              </fieldset>
              {worktreeApplyMode === "overwrite" && (
                <p className="worktree-destructive-warning" role="alert">
                  {t("worktreeApplyOverwriteWarning")}
                </p>
              )}
              <div className="dialog-actions">
                <button
                  type="button"
                  className="secondary-button"
                  onClick={() => setWorktreeApplyTarget(undefined)}
                >{t("cancel")}</button>
                <button type="submit" className="primary-button">{t("applyWorktree")}</button>
              </div>
            </form>
          </section>
        </div>
      )}

      {worktreeGcOpen && (
        <div className="modal-backdrop" role="presentation">
          <section
            className="worktree-operation-dialog"
            role="dialog"
            aria-modal="true"
            aria-labelledby="worktree-gc-title"
          >
            <header className="dialog-header">
              <div>
                <Trash2 size={16} />
                <h2 id="worktree-gc-title">{t("gcWorktrees")}</h2>
              </div>
              <button
                type="button"
                className="icon-button"
                aria-label={t("close")}
                title={t("close")}
                disabled={worktreeGcExecuting}
                onClick={() => {
                  worktreeGcOpenRef.current = false;
                  setWorktreeGcOpen(false);
                }}
              ><X size={17} /></button>
            </header>
            <div className="worktree-operation-form">
              {!worktreeGcPreview ? (
                <div className="worktree-gc-loading" role="status">
                  <LoaderCircle className="session-spinner" size={16} />
                  <span>{t("worktreeGcPreviewing")}</span>
                </div>
              ) : (
                <dl className="worktree-gc-preview">
                  <div><dt>{t("worktreeGcRemovable")}</dt><dd>{
                    worktreeGcPreview.deadRemoved + worktreeGcPreview.expiredRemoved
                  }</dd></div>
                  <div><dt>{t("worktreeGcAlive")}</dt><dd>{worktreeGcPreview.skippedAlive}</dd></div>
                  <div><dt>{t("worktreeGcFailed")}</dt><dd>{worktreeGcPreview.removeFailed}</dd></div>
                </dl>
              )}
              <div className="dialog-actions">
                <button
                  type="button"
                  className="secondary-button"
                  disabled={worktreeGcExecuting}
                  onClick={() => {
                    worktreeGcOpenRef.current = false;
                    setWorktreeGcOpen(false);
                  }}
                >{t("cancel")}</button>
                <button
                  type="button"
                  className="secondary-button"
                  disabled={worktreeOperation === "gc" || worktreeGcExecuting}
                  onClick={previewGcWorktrees}
                ><RefreshCw size={13} /> {t("worktreeGcRefreshPreview")}</button>
                <button
                  type="button"
                  className="primary-button"
                  disabled={!worktreeGcPreview || worktreeGcExecuting ||
                    worktreeGcPreview.deadRemoved + worktreeGcPreview.expiredRemoved === 0}
                  onClick={executeGcWorktrees}
                >{worktreeGcExecuting
                    ? <LoaderCircle className="session-spinner" size={13} />
                    : <Trash2 size={13} />} {t("worktreeGcExecute")}</button>
              </div>
            </div>
          </section>
        </div>
      )}

      {cloudPushNotice && (
        <div className="cloud-push-notice" role="status" aria-live="polite">
          <Cloud size={13} aria-hidden="true" />
          <span>{cloudPushNotice}</span>
        </div>
      )}

      {settingsOpen && (
        <div className="modal-backdrop" role="presentation">
          <section
            ref={settingsDialogRef}
            className="settings-dialog"
            role="dialog"
            tabIndex={-1}
            aria-modal="true"
            aria-labelledby="settings-title"
          >
            <header className="dialog-header">
              <div>
                <KeyRound size={16} />
                <h2 id="settings-title">{t("settingsTitle")}</h2>
              </div>
              <button
                className="icon-button"
                aria-label={t("close")}
                title={t("close")}
                onClick={closeSettings}
              ><X size={17} /></button>
            </header>
            <form
              onSubmit={(event) => {
                event.preventDefault();
                saveProvider();
              }}
            >
              <div className="provider-fields">
                <label className="provider-field">
                  <span>{t("language")}</span>
                  <select
                    aria-label={t("language")}
                    value={language}
                    onChange={(event) => {
                      setLanguage(event.target.value as UiLanguage);
                      setRestartRequired(true);
                    }}
                  >
                    <option value="zh-CN">{t("languageZhCn")}</option>
                    <option value="en-US">{t("languageEnUs")}</option>
                  </select>
                </label>
                <label className="provider-field">
                  <span>{t("baseUrl")}</span>
                  <input
                    ref={settingsInitialFocusRef}
                    type="text"
                    inputMode="url"
                    aria-label={t("baseUrl")}
                    autoComplete="url"
                    autoFocus
                    value={providerBaseUrl}
                    onChange={(event) => setProviderBaseUrl(event.target.value)}
                  />
                </label>
                <label className="provider-field">
                  <span>{t("model")}</span>
                  <input
                    type="text"
                    aria-label={t("model")}
                    autoComplete="off"
                    value={providerModel}
                    onChange={(event) => setProviderModel(event.target.value)}
                  />
                </label>
                <label className="provider-field">
                  <span>{t("backend")}</span>
                  <select
                    aria-label={t("backend")}
                    value={providerBackend}
                    onChange={(event) => setProviderBackend(event.target.value as ProviderBackend)}
                  >
                    <option value="chat_completions">{t("backendChatCompletions")}</option>
                    <option value="responses">{t("backendResponses")}</option>
                  </select>
                </label>
                <div className="provider-field">
                  <span>{t("providerCredential")}</span>
                  <p className="settings-security-note">
                    <KeyRound size={13} />
                    <span>
                      {t(canReuseProviderCredential && !replaceProviderCredential
                        ? "providerCredentialStored"
                        : "providerCredentialNativePrompt")}
                    </span>
                  </p>
                </div>
                {canReuseProviderCredential && (
                  <label className="provider-checkbox">
                    <input
                      type="checkbox"
                      checked={replaceProviderCredential}
                      onChange={(event) => setReplaceProviderCredential(event.target.checked)}
                    />
                    <span>{t("replaceProviderCredential")}</span>
                  </label>
                )}
                <label className="provider-checkbox">
                  <input
                    type="checkbox"
                    checked={allowInsecureTransport}
                    onChange={(event) => setAllowInsecureTransport(event.target.checked)}
                  />
                  <span>{t("allowInsecureHttp")}</span>
                </label>
              </div>
              {providerStatus === "error" && providerMessage && (
                <p className="provider-message error" role="alert">{providerMessage}</p>
              )}
              {providerStatus !== "error" && providerValidationMessage && (
                <p className="provider-message error" role="alert">{providerValidationMessage}</p>
              )}
              {providerStatus !== "error" && !providerValidationMessage && providerMessage && (
                <p
                  className="provider-message success"
                  role="status"
                >{providerMessage}</p>
              )}
              {restartRequired && (
                <p className="provider-message restart" role="status">
                  {t("languageRestartRequired")}
                </p>
              )}
              <section
                className="settings-feature-panel desktop-preferences-settings"
                aria-labelledby="desktop-preferences-title"
              >
                <header className="settings-feature-header">
                  <div>
                    <TerminalSquare size={15} />
                    <div>
                      <h3 id="desktop-preferences-title">{t("desktopPreferencesTitle")}</h3>
                      <p>{t("desktopPreferencesDescription")}</p>
                    </div>
                  </div>
                </header>
                <div className="settings-feature-body">
                  <label className="provider-checkbox">
                    <input
                      type="checkbox"
                      checked={notificationsEnabled}
                      onChange={(event) => setNotificationsEnabled(event.target.checked)}
                    />
                    <span>{t("desktopNotificationsEnabled")}</span>
                  </label>
                  <label className="provider-checkbox">
                    <input
                      type="checkbox"
                      checked={windowsAutomationEnabled}
                      disabled={!preferencesHydrated}
                      onChange={(event) => toggleWindowsAutomation(event.target.checked)}
                    />
                    <span>{t("windowsAutomationEnabled")}</span>
                  </label>
                  <p className="settings-security-note">
                    <ShieldAlert size={13} />
                    <span>{t("windowsAutomationExperimentalWarning")}</span>
                  </p>
                  <div className="extension-editor-grid windows-automation-fields">
                    <label className="provider-field">
                      <span>{t("windowsAutomationAction")}</span>
                      <select
                        aria-label={t("windowsAutomationAction")}
                        value={windowsAutomationAction}
                        disabled={!windowsAutomationHostEnabled || windowsAutomationPending !== undefined}
                        onChange={(event) => {
                          setWindowsAutomationAction(event.target.value as WindowsAutomationAction);
                          setWindowsAutomationNotice(undefined);
                        }}
                      >
                        <option value="focus-window">{t("windowsAutomationAction_focus-window")}</option>
                        <option value="invoke">{t("windowsAutomationAction_invoke")}</option>
                        <option value="set-value">{t("windowsAutomationAction_set-value")}</option>
                      </select>
                    </label>
                    <label className="provider-field">
                      <span>{t("windowsAutomationProcessId")}</span>
                      <input
                        type="number"
                        inputMode="numeric"
                        min="1"
                        max="2147483647"
                        step="1"
                        aria-label={t("windowsAutomationProcessId")}
                        value={windowsAutomationProcessId}
                        disabled={!windowsAutomationHostEnabled || windowsAutomationPending !== undefined}
                        onChange={(event) => setWindowsAutomationProcessId(event.target.value)}
                      />
                    </label>
                    {windowsAutomationAction !== "focus-window" && (
                      <>
                        <label className="provider-field">
                          <span>{t("windowsAutomationAutomationId")}</span>
                          <input
                            type="text"
                            maxLength={256}
                            autoComplete="off"
                            aria-label={t("windowsAutomationAutomationId")}
                            value={windowsAutomationId}
                            disabled={!windowsAutomationHostEnabled || windowsAutomationPending !== undefined}
                            onChange={(event) => setWindowsAutomationId(event.target.value)}
                          />
                        </label>
                        <label className="provider-field">
                          <span>{t("windowsAutomationName")}</span>
                          <input
                            type="text"
                            maxLength={256}
                            autoComplete="off"
                            aria-label={t("windowsAutomationName")}
                            value={windowsAutomationName}
                            disabled={!windowsAutomationHostEnabled || windowsAutomationPending !== undefined}
                            onChange={(event) => setWindowsAutomationName(event.target.value)}
                          />
                        </label>
                      </>
                    )}
                    {windowsAutomationAction === "set-value" && (
                      <label className="provider-field extension-wide-field">
                        <span>{t("windowsAutomationValue")}</span>
                        <input
                          ref={windowsAutomationValueRef}
                          type="text"
                          maxLength={8192}
                          autoComplete="off"
                          aria-label={t("windowsAutomationValue")}
                          disabled={!windowsAutomationHostEnabled || windowsAutomationPending !== undefined}
                        />
                      </label>
                    )}
                  </div>
                  <p className="settings-security-note">
                    <ShieldCheck size={13} />
                    <span>{t("windowsAutomationNativeApprovalWarning")}</span>
                  </p>
                  <button
                    type="button"
                    className="settings-wide-action primary"
                    disabled={!windowsAutomationExecuteReady}
                    onClick={executeWindowsAutomation}
                  >
                    {windowsAutomationPending
                      ? <LoaderCircle className="session-spinner" size={13} />
                      : <Play size={13} />}
                    {t("windowsAutomationExecute")}
                  </button>
                  {windowsAutomationNotice && (
                    <p
                      className={`settings-feature-notice ${windowsAutomationNotice.kind}`}
                      role={windowsAutomationNotice.kind === "error" ? "alert" : "status"}
                    >{windowsAutomationNotice.text}</p>
                  )}
                </div>
              </section>
              <section
                className="settings-feature-panel agents-settings"
                aria-labelledby="agents-settings-title"
              >
                <header className="settings-feature-header">
                  <div>
                    <Pencil size={15} />
                    <div>
                      <h3 id="agents-settings-title">{t("agentsTitle")}</h3>
                      <p>{t("agentsDescription")}</p>
                    </div>
                  </div>
                </header>
                <div className="settings-feature-body">
                  {agentsEditorStatus === "loading" ? (
                    <p className="settings-feature-notice" role="status">
                      <LoaderCircle className="session-spinner" size={13} />
                      {t("agentsLoading")}
                    </p>
                  ) : selectedAgentsPath ? (
                    <>
                      {agentsFiles.length > 0 ? (
                        <label className="provider-field">
                          <span>{t("agentsFile")}</span>
                          <select
                            aria-label={t("agentsFile")}
                            value={selectedAgentsPath}
                            disabled={agentsEditorStatus === "saving"}
                            onChange={(event) => {
                              const relativePath = event.target.value;
                              setSelectedAgentsPath(relativePath);
                              requestAgentsFile(relativePath);
                            }}
                          >
                            {agentsFiles.map((file) => (
                              <option key={file.relativePath} value={file.relativePath}>
                                {file.relativePath}
                              </option>
                            ))}
                          </select>
                        </label>
                      ) : (
                        <p className="settings-feature-notice">{t("agentsNoFiles")}</p>
                      )}
                      <label className="provider-field agents-editor-field">
                        <span>{t("agentsEditor")}</span>
                        <textarea
                          aria-label={t("agentsEditor")}
                          value={agentsContent}
                          maxLength={512 * 1024}
                          disabled={agentsEditorStatus === "saving"}
                          onChange={(event) => {
                            setAgentsContent(event.target.value);
                            setAgentsNotice("");
                          }}
                        />
                      </label>
                      <button
                        type="button"
                        className="settings-wide-action primary"
                          disabled={agentsEditorStatus !== "ready" ||
                            agentsContent === savedAgentsContent || agentsContentTooLarge}
                        onClick={saveAgentsInstructions}
                      >
                        {agentsEditorStatus === "saving"
                          ? <LoaderCircle className="session-spinner" size={13} />
                          : <Check size={13} />}
                        {t("saveAgentsInstructions")}
                      </button>
                      {agentsContentTooLarge && (
                        <p className="settings-feature-notice error" role="alert">
                          {t("agentsContentTooLarge")}
                        </p>
                      )}
                    </>
                  ) : (
                    <p className="settings-feature-notice">{t("agentsNoFiles")}</p>
                  )}
                  {agentsNotice && (
                    <p
                      className={`settings-feature-notice ${
                        agentsEditorStatus === "error" ? "error" : "status"
                      }`}
                      role={agentsEditorStatus === "error" ? "alert" : "status"}
                    >{agentsNotice}</p>
                  )}
                </div>
              </section>
              <section
                className="settings-feature-panel memory-settings"
                aria-labelledby="memory-settings-title"
              >
                <header className="settings-feature-header">
                  <div>
                    <Brain size={15} />
                    <div>
                      <h3 id="memory-settings-title">{t("memoryBrowserTitle")}</h3>
                      <p>{t("memoryBrowserDescription")}</p>
                    </div>
                  </div>
                  {memoryCanBrowse && (
                    <button
                      type="button"
                      className="icon-button"
                      aria-label={t("refreshMemoryFiles")}
                      title={t("refreshMemoryFiles")}
                      disabled={memoryBrowserStatus === "loading" ||
                        memoryBrowserStatus === "mutating"}
                      onClick={requestMemoryFiles}
                    >
                      <RefreshCw size={13} />
                    </button>
                  )}
                </header>
                <div className="settings-feature-body">
                  {!activeSessionId ? (
                    <p className="settings-feature-notice">{t("memorySessionRequired")}</p>
                  ) : !memoryCanBrowse ? (
                    <p className="settings-feature-notice">{t("memoryUnsupported")}</p>
                  ) : memoryFiles.length > 0 ? (
                    <>
                      <label className="provider-field">
                        <span>{t("memoryFile")}</span>
                        <select
                          aria-label={t("memoryFile")}
                          value={selectedMemoryFileId}
                          disabled={memoryBrowserStatus === "loading" ||
                            memoryBrowserStatus === "mutating"}
                          onChange={(event) => requestMemoryFile(event.target.value)}
                        >
                          {memoryFiles.map((file) => (
                            <option key={file.id} value={file.id}>
                              {file.name} ({t(`memoryScope_${file.scope}`)})
                            </option>
                          ))}
                        </select>
                      </label>
                      {selectedMemoryFile && (
                        <p className="memory-file-meta">
                          {t("memoryFileDetails")
                            .replace("{scope}", t(`memoryScope_${selectedMemoryFile.scope}`))
                            .replace("{bytes}", String(selectedMemoryFile.byteLength))}
                        </p>
                      )}
                      <label className="provider-field memory-editor-field">
                        <span>{t("memoryEditor")}</span>
                        <textarea
                          aria-label={t("memoryEditor")}
                          value={memoryContent}
                          maxLength={memoryContentByteLimit}
                          disabled={memoryBrowserStatus === "loading" ||
                            memoryBrowserStatus === "mutating" || !selectedMemoryFile}
                          onChange={(event) => {
                            setMemoryContent(event.target.value);
                            setMemoryMutationChallenge(undefined);
                            setMemoryBrowserNotice(undefined);
                            if (memoryBrowserStatus === "error") {
                              setMemoryBrowserStatus("ready");
                            }
                          }}
                        />
                      </label>
                      <div className="settings-inline-actions memory-file-actions">
                        <button
                          type="button"
                          className="primary"
                          disabled={!memoryCanWrite || memoryBrowserStatus !== "ready" ||
                            memoryMutationChallenge !== undefined || memoryContentTooLarge ||
                            memoryContent === savedMemoryContent}
                          onClick={saveMemoryFile}
                        >
                          {memoryBrowserStatus === "mutating" &&
                            memoryPendingRequestRef.current?.operation === "write"
                            ? <LoaderCircle className="session-spinner" size={13} />
                            : <Check size={13} />}
                          {t("saveMemoryFile")}
                        </button>
                        <button
                          type="button"
                          disabled={!memoryCanDelete || memoryBrowserStatus !== "ready" ||
                            memoryMutationChallenge !== undefined}
                          onClick={deleteMemoryFile}
                        >
                          <Trash2 size={13} />
                          {t("deleteMemoryFile")}
                        </button>
                      </div>
                      {!memoryMutationsProtected &&
                        (memoryCapabilities?.write || memoryCapabilities?.delete) && (
                          <p className="settings-security-note">
                            <ShieldAlert size={13} />
                            <span>{t("memoryMutationProtectionRequired")}</span>
                          </p>
                        )}
                      {memoryContentTooLarge && (
                        <p className="settings-feature-notice error" role="alert">
                          {t("memoryContentTooLarge")}
                        </p>
                      )}
                      {memoryMutationChallenge && (
                        <div className="memory-confirmation" role="alert">
                          <div>
                            <ShieldAlert size={14} />
                            <p>{memoryMutationChallenge.message}</p>
                          </div>
                          <div className="settings-inline-actions">
                            <button
                              type="button"
                              className="primary"
                              onClick={confirmMemoryMutation}
                            >
                              {memoryMutationChallenge.operation === "write"
                                ? <Check size={13} />
                                : <Trash2 size={13} />}
                              {t(memoryMutationChallenge.operation === "write"
                                ? "confirmMemoryWrite"
                                : "confirmMemoryDelete")}
                            </button>
                            <button type="button" onClick={cancelMemoryMutation}>
                              <X size={13} />
                              {t("cancelMemoryMutation")}
                            </button>
                          </div>
                        </div>
                      )}
                    </>
                  ) : memoryBrowserStatus === "loading" ? (
                    <p className="settings-feature-notice" role="status">
                      <LoaderCircle className="session-spinner" size={13} />
                      {t("memoryLoading")}
                    </p>
                  ) : (
                    <p className="settings-feature-notice">{t("memoryNoFiles")}</p>
                  )}
                  {memoryListingTruncated && (
                    <p className="settings-feature-notice">{t("memoryListTruncated")}</p>
                  )}
                  {memoryBrowserNotice && !memoryMutationChallenge && (
                    <p
                      className={`settings-feature-notice ${memoryBrowserNotice.kind}`}
                      role={memoryBrowserNotice.kind === "error" ? "alert" : "status"}
                    >{memoryBrowserNotice.text}</p>
                  )}
                </div>
              </section>
              <ExtensionSettingsPanel
                bridge={bridge}
                workspaceGeneration={workspaceGenerationRef.current}
                activeSessionId={activeSessionId}
                t={t}
              />
              <CloudSettingsPanel
                bridge={bridge}
                activeSessionId={activeSessionId}
                onSessionImported={(sessionId) => {
                  requestSessionList();
                  if (workspacePathRef.current) {
                    bridge.send({
                      type: "session/open",
                      sessionId,
                      workspacePath: workspacePathRef.current,
                      executionProfile
                    });
                  }
                }}
                t={t}
              />
              <section
                className="maintenance-panel"
                role="region"
                aria-label={t("localMaintenance")}
                aria-busy={maintenanceBusy}
              >
                <section className="maintenance-group" aria-labelledby="session-data-title">
                  <div className="maintenance-heading">
                    <Download size={14} />
                    <div>
                      <h3 id="session-data-title">{t("sessionData")}</h3>
                      <p>{t("sessionDataDescription")}</p>
                    </div>
                  </div>
                  <div className="maintenance-actions">
                    <button
                      type="button"
                      disabled={maintenanceActionsDisabled || !activeSessionId}
                      onClick={() => startMaintenance("session-export")}
                    >
                      {maintenanceRequest?.operation === "session-export"
                        ? <LoaderCircle className="session-spinner" size={13} />
                        : <Download size={13} />}
                      <span>{t("exportSession")}</span>
                    </button>
                    <button
                      type="button"
                      disabled={maintenanceActionsDisabled}
                      onClick={() => startMaintenance("session-import")}
                    >
                      {maintenanceRequest?.operation === "session-import"
                        ? <LoaderCircle className="session-spinner" size={13} />
                        : <Upload size={13} />}
                      <span>{t("importSession")}</span>
                    </button>
                  </div>
                </section>

                <section className="maintenance-group" aria-labelledby="backup-restore-title">
                  <div className="maintenance-heading">
                    <Database size={14} />
                    <div>
                      <h3 id="backup-restore-title">{t("backupAndRestore")}</h3>
                      <p>{t("backupDescription")}</p>
                    </div>
                  </div>
                  <div className="maintenance-actions">
                    <button
                      type="button"
                      disabled={maintenanceActionsDisabled}
                      onClick={() => startMaintenance("backup-create")}
                    >
                      {maintenanceRequest?.operation === "backup-create"
                        ? <LoaderCircle className="session-spinner" size={13} />
                        : <Download size={13} />}
                      <span>{t("createBackup")}</span>
                    </button>
                    <button
                      type="button"
                      className="danger"
                      disabled={maintenanceActionsDisabled}
                      onClick={() => startMaintenance("backup-restore")}
                    >
                      {maintenanceRequest?.operation === "backup-restore"
                        ? <LoaderCircle className="session-spinner" size={13} />
                        : <Upload size={13} />}
                      <span>{t("restoreBackup")}</span>
                    </button>
                  </div>
                </section>

                <section className="maintenance-group" aria-labelledby="software-update-title">
                  <div className="maintenance-heading">
                    <RefreshCw size={14} />
                    <div>
                      <h3 id="software-update-title">{t("softwareUpdate")}</h3>
                      <p>{t("portableUpdateOnly")}</p>
                    </div>
                    {stagedUpdateVersion && (
                      <strong className="maintenance-version">{stagedUpdateVersion}</strong>
                    )}
                  </div>
                  <div className="maintenance-actions">
                    <button
                      type="button"
                      disabled={maintenanceActionsDisabled || updatesUnsupported}
                      onClick={() => startMaintenance("update-check")}
                    >
                      {maintenanceRequest?.operation === "update-check"
                        ? <LoaderCircle className="session-spinner" size={13} />
                        : <RefreshCw size={13} />}
                      <span>{t("checkForUpdates")}</span>
                    </button>
                    <button
                      type="button"
                      className="primary"
                      disabled={maintenanceActionsDisabled || !stagedUpdateVersion ||
                        updatesUnsupported}
                      onClick={() => startMaintenance("update-apply")}
                    >
                      {maintenanceRequest?.operation === "update-apply"
                        ? <LoaderCircle className="session-spinner" size={13} />
                        : <Upload size={13} />}
                      <span>{t("installAndRestart")}</span>
                    </button>
                  </div>
                  <label className="provider-checkbox">
                    <input
                      type="checkbox"
                      checked={backgroundUpdateChecksEnabled}
                      disabled={!preferencesHydrated || updatesUnsupported}
                      onChange={(event) =>
                        setBackgroundUpdateChecksEnabled(event.target.checked)}
                    />
                    <span>{t("backgroundUpdateChecksEnabled")}</span>
                  </label>
                </section>

                {maintenanceNotice && (
                  <p
                    className={`maintenance-notice ${maintenanceNotice.kind}`}
                    role={maintenanceNotice.kind === "error" ? "alert" : "status"}
                  >{maintenanceNotice.text}</p>
                )}
              </section>
              <div className="dialog-actions">
                <button
                  type="button"
                  className="secondary-button"
                  onClick={closeSettings}
                >{t("cancel")}</button>
                <button type="submit" className="primary-button" disabled={!providerFormReady}>
                  {t("saveProvider")}
                </button>
              </div>
            </form>
          </section>
        </div>
      )}
    </div>
  );
}

type CloudProfileState = {
  localOnly: boolean;
  baseUri: string;
  teamId: string;
  deviceId: string;
  hasAccessToken: boolean;
};
type ClaimedRunnerJob = {
  claimHandle: string;
  jobId: string;
  requiredCapability: string;
  task: string;
  leaseExpiresAt: string;
};

type ExtensionTab = "mcp" | "skills" | "hooks" | "plugins" | "marketplace";

function ExtensionSettingsPanel({
  bridge,
  workspaceGeneration,
  activeSessionId,
  t
}: {
  bridge: HostBridge;
  workspaceGeneration: number;
  activeSessionId?: string;
  t: (key: string) => string;
}) {
  const [catalog, setCatalog] = useState<ExtensionsCatalog>();
  const [activeTab, setActiveTab] = useState<ExtensionTab>("mcp");
  const [pending, setPending] = useState<{ scope?: ExtensionScope; action: string }>();
  const [notice, setNotice] = useState<{ kind: "status" | "error"; text: string }>();
  const [mcpTransport, setMcpTransport] = useState<"stdio" | "http">("stdio");
  const [mcpName, setMcpName] = useState("");
  const [mcpCommandOrUrl, setMcpCommandOrUrl] = useState("");
  const [mcpArguments, setMcpArguments] = useState("");
  const [mcpEnvironment, setMcpEnvironment] = useState("");
  const [skillPath, setSkillPath] = useState("");
  const [hookPath, setHookPath] = useState("");
  const [pluginPath, setPluginPath] = useState("");
  const [pluginSource, setPluginSource] = useState("");
  const pendingRef = useRef<{
    requestId: string;
    operation: "list" | "action";
    scope?: ExtensionScope;
    action: string;
  } | undefined>(undefined);
  const initializedRef = useRef(false);

  function beginExtensionRequest(
    operation: "list" | "action",
    action: string,
    command: HostCommand,
    scope?: ExtensionScope
  ) {
    if (!bridge.available || pendingRef.current) {
      return;
    }
    const requestId = "requestId" in command ? command.requestId : "";
    pendingRef.current = { requestId, operation, scope, action };
    setPending({ scope, action });
    setNotice(undefined);
    bridge.send(command);
  }

  function refreshExtensions(useCache = false) {
    const requestId = createMaintenanceRequestId();
    beginExtensionRequest("list", "list", {
      type: "extensions/list",
      requestId,
      workspaceGeneration,
      ...(activeSessionId ? { sessionId: activeSessionId } : {}),
      useCache
    });
  }

  function runExtensionAction(
    scope: ExtensionScope,
    action: string,
    payload: Record<string, unknown>,
    confirmed = true
  ) {
    if (!activeSessionId) {
      setNotice({ kind: "error", text: t("extensionsSessionRequired") });
      return;
    }
    const requestId = createMaintenanceRequestId();
    beginExtensionRequest("action", action, {
      type: "extensions/action",
      requestId,
      workspaceGeneration,
      sessionId: activeSessionId,
      scope,
      action,
      confirmed,
      payload
    }, scope);
  }

  useEffect(() => bridge.subscribe((event) => {
    if (!("requestId" in event) || event.requestId !== pendingRef.current?.requestId) {
      return;
    }
    const request = pendingRef.current;
    pendingRef.current = undefined;
    setPending(undefined);
    switch (event.type) {
      case "extensions/catalog":
        setCatalog({
          mcp: event.mcp,
          skills: event.skills,
          hooks: event.hooks,
          plugins: event.plugins,
          marketplace: event.marketplace
        });
        setNotice({ kind: "status", text: t("extensionsLoaded") });
        break;
      case "extensions/action/completed": {
        const suffix = [event.requiresReload ? t("extensionsReloadRequired") : "",
          event.requiresRestart ? t("extensionsRestartRequired") : ""]
          .filter(Boolean).join(" ");
        setNotice({
          kind: event.status === "success" ? "status" : "error",
          text: [event.message || t(`extensionStatus_${event.status}`), suffix]
            .filter(Boolean).join(" ")
        });
        break;
      }
      case "extensions/error":
        setNotice({ kind: "error", text: event.message || t("extensionsError") });
        break;
      default:
        if (request) {
          setNotice({ kind: "error", text: t("extensionsError") });
        }
        break;
    }
  }), [bridge, t]);

  useEffect(() => {
    if (initializedRef.current || !bridge.available) {
      return;
    }
    initializedRef.current = true;
    refreshExtensions(true);
  }, [bridge]);

  const totalExtensions = (catalog?.mcp.servers.length ?? 0) +
    (catalog?.skills.skills.length ?? 0) + (catalog?.hooks.hooks.length ?? 0) +
    (catalog?.plugins.plugins.length ?? 0) +
    (catalog?.marketplace.sources.reduce((sum, source) => sum + source.plugins.length, 0) ?? 0);
  const busy = pending !== undefined;
  const environmentReferences = parseEnvironmentReferences(mcpEnvironment);
  const mcpReady = validExtensionName(mcpName) && mcpCommandOrUrl.trim().length > 0 &&
    environmentReferences !== null && (mcpTransport === "stdio" || validExtensionEndpoint(
      mcpCommandOrUrl
    ));

  return (
    <section className="settings-feature-panel extensions-settings" aria-labelledby="extensions-title">
      <header className="settings-feature-header">
        <div>
          <Puzzle size={15} />
          <div>
            <h3 id="extensions-title">{t("extensionsTitle")}</h3>
            <p>{t("extensionsDescription")}</p>
          </div>
        </div>
        <span className="local">{t("extensionsCount").replace("{count}", String(totalExtensions))}</span>
      </header>
      <div className="settings-feature-body">
        <div className="extension-tabs" role="tablist" aria-label={t("extensionsTitle")}>
          {(["mcp", "skills", "hooks", "plugins", "marketplace"] as ExtensionTab[])
            .map((tab) => (
              <button
                key={tab}
                type="button"
                role="tab"
                aria-selected={activeTab === tab}
                onClick={() => setActiveTab(tab)}
              >{t(`extensionTab_${tab}`)}</button>
            ))}
        </div>

        {activeTab === "mcp" && (
          <div className="extension-tab-panel" role="tabpanel">
            <div className="extension-editor-grid">
              <label className="provider-field">
                <span>{t("mcpTransport")}</span>
                <select
                  value={mcpTransport}
                  onChange={(event) => setMcpTransport(event.target.value as "stdio" | "http")}
                >
                  <option value="stdio">stdio</option>
                  <option value="http">HTTP</option>
                </select>
              </label>
              <label className="provider-field">
                <span>{t("mcpServerName")}</span>
                <input value={mcpName} onChange={(event) => setMcpName(event.target.value)} />
              </label>
              <label className="provider-field extension-wide-field">
                <span>{t(mcpTransport === "stdio" ? "mcpCommand" : "mcpUrl")}</span>
                <input
                  value={mcpCommandOrUrl}
                  onChange={(event) => setMcpCommandOrUrl(event.target.value)}
                />
              </label>
              {mcpTransport === "stdio" && (
                <label className="provider-field extension-wide-field">
                  <span>{t("mcpArguments")}</span>
                  <input
                    value={mcpArguments}
                    onChange={(event) => setMcpArguments(event.target.value)}
                  />
                </label>
              )}
              <label className="provider-field extension-wide-field">
                <span>{t("mcpEnvironmentReferences")}</span>
                <input
                  value={mcpEnvironment}
                  placeholder="NAME=SOURCE_ENV, TOKEN=AGENTDESK_TOKEN"
                  onChange={(event) => setMcpEnvironment(event.target.value)}
                />
              </label>
            </div>
            <p className="settings-security-note">
              <ShieldCheck size={13} />
              <span>{t("mcpEnvironmentSecurity")}</span>
            </p>
            <button
              type="button"
              className="settings-wide-action primary"
              disabled={busy || !activeSessionId || !mcpReady}
              onClick={() => runExtensionAction(
                "mcp",
                mcpTransport === "stdio" ? "upsert_stdio" : "upsert_http",
                mcpTransport === "stdio"
                  ? {
                      serverName: mcpName.trim(),
                      command: mcpCommandOrUrl.trim(),
                      args: splitArguments(mcpArguments),
                      environment: environmentReferences,
                      enabled: true
                    }
                  : {
                      serverName: mcpName.trim(),
                      url: mcpCommandOrUrl.trim(),
                      headers: environmentReferences?.map((item) => ({
                        name: item.name,
                        sourceVariable: item.sourceVariable
                      })),
                      enabled: true
                    }
              )}
            >{t("mcpSaveServer")}</button>
            <ExtensionList
              emptyText={t("mcpNoServers")}
              items={(catalog?.mcp.servers ?? []).map((server) => ({
                id: server.name,
                title: server.displayName || server.name,
                subtitle: [server.transport, server.sourceLabel || server.source,
                  server.environmentVariableNames.join(", ")].filter(Boolean).join(" · "),
                enabled: server.session?.enabled ?? true,
                onToggle: () => runExtensionAction("mcp", "toggle", {
                  serverName: server.name,
                  enabled: !(server.session?.enabled ?? true)
                }),
                onRemove: server.source === "local"
                  ? () => {
                      if (window.confirm(t("extensionDeleteConfirm").replace(
                        "{name}", server.displayName || server.name
                      ))) {
                        runExtensionAction("mcp", "delete", { serverName: server.name });
                      }
                    }
                  : undefined
              }))}
              busy={busy}
              t={t}
            />
          </div>
        )}

        {activeTab === "skills" && (
          <div className="extension-tab-panel" role="tabpanel">
            <div className="settings-input-action">
              <input
                type="text"
                value={skillPath}
                aria-label={t("skillPath")}
                placeholder={t("skillPath")}
                onChange={(event) => setSkillPath(event.target.value)}
              />
              <button
                type="button"
                disabled={busy || !activeSessionId || !validExtensionPath(skillPath)}
                onClick={() => runExtensionAction("skills", "add_path", {
                  path: skillPath.trim()
                })}
              ><Plus size={13} /> {t("skillAddPath")}</button>
            </div>
            <div className="settings-inline-actions">
              <button
                type="button"
                disabled={busy || !activeSessionId || !validExtensionPath(skillPath)}
                onClick={() => runExtensionAction("skills", "remove_path", {
                  path: skillPath.trim()
                })}
              >{t("skillRemovePath")}</button>
              <button
                type="button"
                disabled={busy || !activeSessionId}
                onClick={() => {
                  if (window.confirm(t("skillResetConfirm"))) {
                    runExtensionAction("skills", "reset", {});
                  }
                }}
              >{t("skillReset")}</button>
            </div>
            <ExtensionList
              emptyText={t("skillsEmpty")}
              items={(catalog?.skills.skills ?? []).map((skill) => ({
                id: `${skill.scope}:${skill.path}`,
                title: skill.displayName || skill.name,
                subtitle: `${t(`extensionSkillScope_${skill.scope}`)} · ${skill.path}`,
                enabled: skill.enabled
              }))}
              busy={busy}
              t={t}
            />
          </div>
        )}

        {activeTab === "hooks" && (
          <div className="extension-tab-panel" role="tabpanel">
            <div className="settings-input-action">
              <input
                type="text"
                value={hookPath}
                aria-label={t("hookPath")}
                placeholder={t("hookPath")}
                onChange={(event) => setHookPath(event.target.value)}
              />
              <button
                type="button"
                disabled={busy || !activeSessionId || !validExtensionPath(hookPath)}
                onClick={() => runExtensionAction("hooks", "add", {
                  path: hookPath.trim()
                })}
              ><Plus size={13} /> {t("hookAddPath")}</button>
            </div>
            <div className="settings-inline-actions">
              <button
                type="button"
                disabled={busy || !activeSessionId || !validExtensionPath(hookPath)}
                onClick={() => runExtensionAction("hooks", "remove", {
                  path: hookPath.trim()
                })}
              >{t("hookRemovePath")}</button>
              <button
                type="button"
                disabled={busy || !activeSessionId}
                onClick={() => runExtensionAction("hooks", "reload", {})}
              ><RefreshCw size={13} /> {t("extensionReload")}</button>
              <button
                type="button"
                disabled={busy || !activeSessionId}
                onClick={() => runExtensionAction(
                  "hooks",
                  catalog?.hooks.projectTrusted ? "untrust" : "trust",
                  {}
                )}
              >{t(catalog?.hooks.projectTrusted ? "hooksUntrust" : "hooksTrust")}</button>
            </div>
            <ExtensionList
              emptyText={t("hooksEmpty")}
              items={(catalog?.hooks.hooks ?? []).map((hook) => {
                const sourceHooks = (catalog?.hooks.hooks ?? []).filter(
                  (candidate) => candidate.sourceDirectory === hook.sourceDirectory
                );
                const sourceDisabled = sourceHooks.every((candidate) => candidate.disabled);
                return {
                  id: `${hook.event}:${hook.name}`,
                  title: hook.name,
                  subtitle: `${hook.event} · ${hook.handlerType} · ${hook.sourceDirectory}`,
                  enabled: !hook.disabled,
                  onToggle: () => runExtensionAction(
                    "hooks",
                    hook.disabled ? "enable" : "disable",
                    { hookName: hook.name }
                  ),
                  sourceDisabled,
                  onToggleSource: () => runExtensionAction("hooks", "toggle_source", {
                    hookNames: sourceHooks.map((candidate) => candidate.name),
                    disableSource: !sourceDisabled
                  })
                };
              })}
              busy={busy}
              t={t}
            />
          </div>
        )}

        {activeTab === "plugins" && (
          <div className="extension-tab-panel" role="tabpanel">
            <div className="settings-input-action">
              <input
                type="text"
                value={pluginPath}
                aria-label={t("pluginPath")}
                placeholder={t("pluginPath")}
                onChange={(event) => setPluginPath(event.target.value)}
              />
              <button
                type="button"
                disabled={busy || !activeSessionId || !validExtensionPath(pluginPath)}
                onClick={() => runExtensionAction("plugins", "add", {
                  path: pluginPath.trim()
                })}
              ><Plus size={13} /> {t("pluginAddPath")}</button>
            </div>
            <div className="settings-inline-actions">
              <button
                type="button"
                disabled={busy || !activeSessionId || !validExtensionPath(pluginPath)}
                onClick={() => runExtensionAction("plugins", "remove", {
                  path: pluginPath.trim()
                })}
              >{t("pluginRemovePath")}</button>
              <button
                type="button"
                disabled={busy || !activeSessionId}
                onClick={() => runExtensionAction("plugins", "reload", {})}
              ><RefreshCw size={13} /> {t("extensionReload")}</button>
            </div>
            <div className="settings-input-action stacked">
              <input
                type="text"
                value={pluginSource}
                aria-label={t("pluginInstallSource")}
                placeholder={t("pluginInstallSource")}
                onChange={(event) => setPluginSource(event.target.value)}
              />
              <button
                type="button"
                disabled={busy || !activeSessionId || !validPluginSource(pluginSource)}
                onClick={() => {
                  if (window.confirm(t("pluginInstallConfirm").replace(
                    "{source}", pluginSource.trim()
                  ))) {
                    runExtensionAction("plugins", "install", {
                      source: pluginSource.trim()
                    }, true);
                  }
                }}
              ><Download size={13} /> {t("pluginInstall")}</button>
            </div>
            <ExtensionList
              emptyText={t("pluginsEmpty")}
              items={(catalog?.plugins.plugins ?? []).map((plugin) => ({
                id: plugin.id,
                title: plugin.name,
                subtitle: [plugin.version, t(`pluginScope_${plugin.scope}`),
                  plugin.trusted ? t("pluginTrusted") : t("pluginUntrusted"),
                  plugin.conflict].filter(Boolean).join(" · "),
                enabled: plugin.enabled,
                onToggle: () => runExtensionAction(
                  "plugins",
                  plugin.enabled ? "disable" : "enable",
                  { pluginId: plugin.id }
                ),
                onUpdate: () => {
                  if (window.confirm(t("pluginUpdateConfirm").replace(
                    "{name}", plugin.name
                  ))) {
                    runExtensionAction("plugins", "update", {
                      pluginId: plugin.id
                    }, true);
                  }
                }
              }))}
              busy={busy}
              t={t}
            />
          </div>
        )}

        {activeTab === "marketplace" && (
          <div className="extension-tab-panel" role="tabpanel">
            <button
              type="button"
              className="settings-wide-action"
              disabled={busy || !activeSessionId}
              onClick={() => runExtensionAction("marketplace", "refresh", {}, false)}
            ><RefreshCw size={13} /> {t("marketplaceRefresh")}</button>
            <p className="settings-security-note">
              <ShieldCheck size={13} />
              <span>{t("marketplacePolicyNotice")}</span>
            </p>
            <div className="extension-marketplace-list">
              {(catalog?.marketplace.sources ?? []).flatMap((source) =>
                source.plugins.map((plugin) => ({ source, plugin }))).map(({ source, plugin }) => {
                  const action = plugin.installStatus === "not_installed"
                    ? "install"
                    : plugin.installStatus === "update_available"
                      ? "update"
                      : "uninstall";
                  return (
                    <article key={`${source.source}:${plugin.relativePath}`}>
                      <div>
                        <strong>{plugin.name}</strong>
                        <span>{[plugin.version, plugin.author, source.name]
                          .filter(Boolean).join(" · ")}</span>
                      </div>
                      <button
                        type="button"
                        disabled={busy || !activeSessionId || source.kind === "failed"}
                        onClick={() => {
                          if (window.confirm(t("marketplaceConfirm").replace(
                            "{action}", t(`marketplaceAction_${action}`)
                          ).replace("{name}", plugin.name))) {
                            runExtensionAction("marketplace", action, {
                              source: plugin.source,
                              relativePath: plugin.relativePath
                            }, true);
                          }
                        }}
                      >{t(`marketplaceAction_${action}`)}</button>
                    </article>
                  );
                })}
              {(catalog?.marketplace.sources ?? []).every((source) => source.plugins.length === 0) && (
                <p className="extension-empty">{t("marketplaceEmpty")}</p>
              )}
            </div>
          </div>
        )}

        <div className="settings-inline-actions">
          <button
            type="button"
            disabled={busy}
            onClick={() => refreshExtensions(false)}
          ><RefreshCw size={13} /> {t("extensionsRefresh")}</button>
        </div>
        {pending && (
          <p className="settings-feature-notice" role="status">
            <LoaderCircle className="session-spinner" size={13} />
            {t("extensionsRunning")}
          </p>
        )}
        {notice && (
          <p className={`settings-feature-notice ${notice.kind}`} role={
            notice.kind === "error" ? "alert" : "status"
          }>{notice.text}</p>
        )}
      </div>
    </section>
  );
}

function ExtensionList({
  items,
  emptyText,
  busy,
  t
}: {
  items: Array<{
    id: string;
    title: string;
    subtitle: string;
    enabled: boolean;
    onToggle?: () => void;
    sourceDisabled?: boolean;
    onToggleSource?: () => void;
    onUpdate?: () => void;
    onRemove?: () => void;
  }>;
  emptyText: string;
  busy: boolean;
  t: (key: string) => string;
}) {
  if (items.length === 0) {
    return <p className="extension-empty">{emptyText}</p>;
  }
  return (
    <div className="extension-list">
      {items.map((item) => (
        <article key={item.id}>
          <div>
            <strong>{item.title}</strong>
            <span>{item.subtitle}</span>
          </div>
          <div>
            {item.onToggle && (
              <button
                type="button"
                disabled={busy}
                aria-label={`${t(item.enabled ? "extensionDisable" : "extensionEnable")}: ${item.title}`}
                onClick={item.onToggle}
              >{item.enabled ? <Check size={13} /> : <Play size={13} />}</button>
            )}
            {item.onToggleSource && (
              <button
                type="button"
                disabled={busy}
                title={`${t(item.sourceDisabled ? "hookEnableSource" : "hookDisableSource")}: ${item.title}`}
                aria-label={`${t(item.sourceDisabled ? "hookEnableSource" : "hookDisableSource")}: ${item.title}`}
                onClick={item.onToggleSource}
              ><Power size={13} /></button>
            )}
            {item.onUpdate && (
              <button
                type="button"
                disabled={busy}
                title={`${t("pluginUpdate")}: ${item.title}`}
                aria-label={`${t("pluginUpdate")}: ${item.title}`}
                onClick={item.onUpdate}
              ><RefreshCw size={13} /></button>
            )}
            {item.onRemove && (
              <button
                type="button"
                className="danger"
                disabled={busy}
                aria-label={`${t("extensionDelete")}: ${item.title}`}
                onClick={item.onRemove}
              ><Trash2 size={13} /></button>
            )}
          </div>
        </article>
      ))}
    </div>
  );
}

function validExtensionName(value: string): boolean {
  const candidate = value.trim();
  return candidate.length > 0 && candidate.length <= 256 &&
    /^[A-Za-z0-9._:/\[\]@-]+$/u.test(candidate) &&
    !candidate.split(/[\\/]/u).some((part) => part === "." || part === "..");
}

function validExtensionPath(value: string): boolean {
  const candidate = value.trim();
  return candidate.length > 0 && candidate.length <= 32767 &&
    !candidate.split(/[\\/]/u).some((part) => part === "." || part === "..");
}

function validExtensionEndpoint(value: string): boolean {
  try {
    const endpoint = new URL(value.trim());
    const loopback = endpoint.hostname === "localhost" || endpoint.hostname === "127.0.0.1" ||
      endpoint.hostname === "[::1]" || endpoint.hostname === "::1";
    return endpoint.username === "" && endpoint.password === "" && endpoint.hash === "" &&
      (endpoint.protocol === "https:" || (endpoint.protocol === "http:" && loopback));
  } catch {
    return false;
  }
}

function validPluginSource(value: string): boolean {
  const candidate = value.trim();
  if (!candidate || candidate.length > 8192) {
    return false;
  }
  if (/^[A-Za-z]:[\\/]/u.test(candidate)) {
    return validExtensionPath(candidate);
  }
  try {
    new URL(candidate);
    return validExtensionEndpoint(candidate);
  } catch {
    return validExtensionPath(candidate);
  }
}

function parseEnvironmentReferences(
  value: string
): Array<{ name: string; sourceVariable: string }> | null {
  if (!value.trim()) {
    return [];
  }
  const result: Array<{ name: string; sourceVariable: string }> = [];
  const names = new Set<string>();
  for (const part of value.split(",")) {
    const [name, sourceVariable, ...rest] = part.split("=").map((item) => item.trim());
    if (rest.length > 0 || !/^[A-Za-z_][A-Za-z0-9_]{0,127}$/.test(name ?? "") ||
        !/^[A-Za-z_][A-Za-z0-9_]{0,127}$/.test(sourceVariable ?? "") ||
        names.has(name)) {
      return null;
    }
    names.add(name);
    result.push({ name, sourceVariable });
  }
  return result;
}

function splitArguments(value: string): string[] {
  return value.split(/\s+/u).map((item) => item.trim()).filter(Boolean).slice(0, 256);
}

type CloudPolicyState = {
  version: number;
  allowedExecutionProfiles: ExecutionProfile[];
  remoteRunnerEnabled: boolean;
  uiAutomationEnabled: boolean;
  maximumConcurrentJobs: number;
  allowedPluginPublishers: string;
};

function createLocalCloudProfile(): CloudProfileState {
  return {
    localOnly: true,
    baseUri: "",
    teamId: "",
    deviceId: "",
    hasAccessToken: false
  };
}

function createDefaultCloudPolicy(): CloudPolicyState {
  return {
    version: 1,
    allowedExecutionProfiles: ["NativeProtected"],
    remoteRunnerEnabled: false,
    uiAutomationEnabled: false,
    maximumConcurrentJobs: 1,
    allowedPluginPublishers: ""
  };
}

function sameCloudProfileConfiguration(
  left: CloudProfileState,
  right: CloudProfileState
): boolean {
  return left.localOnly === right.localOnly && left.baseUri === right.baseUri &&
    left.teamId === right.teamId && left.deviceId === right.deviceId;
}

function cloudProfileIdentity(profile: CloudProfileState): string {
  return profile.localOnly
    ? "local-only"
    : `${profile.baseUri.trim()}\n${profile.teamId.trim()}\n${profile.deviceId.trim()}`;
}

function CloudSettingsPanel({
  bridge,
  activeSessionId,
  onSessionImported,
  t
}: {
  bridge: HostBridge;
  activeSessionId?: string;
  onSessionImported: (sessionId: string) => void;
  t: (key: string) => string;
}) {
  const initialProfile = createLocalCloudProfile();
  const [activeProfile, setActiveProfile] = useState<CloudProfileState>(initialProfile);
  const [draftProfile, setDraftProfile] = useState<CloudProfileState>(initialProfile);
  const [policy, setPolicy] = useState<CloudPolicyState>(createDefaultCloudPolicy);
  const [remoteSessionId, setRemoteSessionId] = useState("");
  const [targetDeviceId, setTargetDeviceId] = useState("");
  const [runnerId, setRunnerId] = useState("");
  const [runnerCapabilities, setRunnerCapabilities] = useState("code,git");
  const [runnerRequiredCapability, setRunnerRequiredCapability] = useState("windows");
  const [runnerTask, setRunnerTask] = useState("");
  const [runnerLeaseSeconds, setRunnerLeaseSeconds] = useState(60);
  const [runnerResult, setRunnerResult] = useState("");
  const [queuedRunnerJobId, setQueuedRunnerJobId] = useState<string>();
  const [claimedRunnerJob, setClaimedRunnerJob] = useState<ClaimedRunnerJob>();
  const [automationName, setAutomationName] = useState("");
  const [automationIntervalSeconds, setAutomationIntervalSeconds] = useState(3600);
  const [automationRequiredCapability, setAutomationRequiredCapability] = useState("windows");
  const [automationTask, setAutomationTask] = useState("");
  const [automations, setAutomations] = useState<CloudAutomation[]>([]);
  const [handoffImports, setHandoffImports] = useState<CloudHandoffImport[]>([]);
  const [pendingOperation, setPendingOperation] = useState<CloudOperation>();
  const [notice, setNotice] = useState<{ kind: "status" | "error"; text: string }>();
  const pendingRef = useRef<{ requestId: string; operation: CloudOperation } | undefined>(
    undefined
  );
  const activeProfileRef = useRef(initialProfile);
  const initializedRef = useRef(false);

  function resetTenantState() {
    setPolicy(createDefaultCloudPolicy());
    setRemoteSessionId("");
    setTargetDeviceId("");
    setRunnerId("");
    setRunnerCapabilities("code,git");
    setRunnerRequiredCapability("windows");
    setRunnerTask("");
    setRunnerLeaseSeconds(60);
    setRunnerResult("");
    setQueuedRunnerJobId(undefined);
    setClaimedRunnerJob(undefined);
    setAutomationName("");
    setAutomationIntervalSeconds(3600);
    setAutomationRequiredCapability("windows");
    setAutomationTask("");
    setAutomations([]);
    setHandoffImports([]);
  }

  function beginCloudOperation(operation: CloudOperation, command: HostCommand): boolean {
    if (!bridge.available || pendingRef.current) {
      return false;
    }
    const requestId = "requestId" in command ? command.requestId : "";
    pendingRef.current = { requestId, operation };
    setPendingOperation(operation);
    setNotice(undefined);
    bridge.send(command);
    return true;
  }

  function runCloudOperation(
    operation: CloudOperation,
    build: (requestId: string) => HostCommand
  ) {
    const requestId = createMaintenanceRequestId();
    beginCloudOperation(operation, build(requestId));
  }

  function completeCloudOperation(requestId: string): boolean {
    if (pendingRef.current?.requestId !== requestId) {
      return false;
    }
    pendingRef.current = undefined;
    setPendingOperation(undefined);
    return true;
  }

  useEffect(() => bridge.subscribe((event) => {
    if (!("requestId" in event) || typeof event.requestId !== "string" ||
        !completeCloudOperation(event.requestId)) {
      return;
    }
    switch (event.type) {
      case "cloud/profile": {
        const nextProfile = {
          localOnly: event.localOnly,
          baseUri: event.baseUri ?? "",
          teamId: event.teamId ?? "",
          deviceId: event.deviceId ?? "",
          hasAccessToken: event.hasAccessToken
        };
        const identityChanged = cloudProfileIdentity(activeProfileRef.current) !==
          cloudProfileIdentity(nextProfile);
        activeProfileRef.current = nextProfile;
        setActiveProfile(nextProfile);
        setDraftProfile(nextProfile);
        if (identityChanged) {
          resetTenantState();
        }
        setNotice({
          kind: "status",
          text: t(event.localOnly ? "cloudLocalModeActive" : "cloudRemoteModeActive")
        });
        break;
      }
      case "cloud/pairing/completed":
        setNotice({
          kind: "status",
          text: t(event.operation === "export" ? "cloudPairingExported" : "cloudPairingImported")
        });
        break;
      case "cloud/session/uploaded":
        setNotice({
          kind: "status",
          text: t("cloudSessionUploaded").replace("{revision}", String(event.revision))
        });
        break;
      case "cloud/session/imported":
        if (event.found && event.importedSessionId) {
          setNotice({ kind: "status", text: t("cloudSessionImported") });
          onSessionImported(event.importedSessionId);
        } else {
          setNotice({ kind: "error", text: t("cloudSessionNotFound") });
        }
        break;
      case "cloud/session/deleted":
        setNotice(event.found && event.revision
          ? {
              kind: "status",
              text: t("cloudSessionDeleted").replace("{revision}", String(event.revision))
            }
          : { kind: "error", text: t("cloudSessionNotFound") });
        break;
      case "cloud/session/exported":
        setNotice({
          kind: "status",
          text: t("cloudSessionExported").replace("{fileName}", event.fileName)
        });
        break;
      case "cloud/handoff/created":
        setNotice({
          kind: "status",
          text: t("cloudHandoffCreated").replace("{id}", event.handoffId)
        });
        break;
      case "cloud/handoffs/received":
        setHandoffImports(event.imports);
        setNotice({
          kind: "status",
          text: t("cloudHandoffsReceived").replace("{count}", String(event.imports.length))
        });
        break;
      case "cloud/policy":
        setPolicy({
          version: event.version,
          allowedExecutionProfiles: event.allowedExecutionProfiles,
          remoteRunnerEnabled: event.remoteRunnerEnabled,
          uiAutomationEnabled: event.uiAutomationEnabled,
          maximumConcurrentJobs: event.maximumConcurrentJobs,
          allowedPluginPublishers: event.allowedPluginPublishers.join(", ")
        });
        setNotice({ kind: "status", text: t("cloudPolicyLoaded") });
        break;
      case "cloud/runner/registered":
        setNotice({ kind: "status", text: t("cloudRunnerRegistered") });
        break;
      case "cloud/runner/queued":
        setQueuedRunnerJobId(event.jobId);
        setNotice({
          kind: "status",
          text: t("cloudRunnerQueued").replace("{id}", event.jobId)
        });
        break;
      case "cloud/runner/claimed":
        if (event.found && event.claimHandle && event.jobId && event.requiredCapability && event.task &&
            event.leaseExpiresAt) {
          setClaimedRunnerJob({
            claimHandle: event.claimHandle,
            jobId: event.jobId,
            requiredCapability: event.requiredCapability,
            task: event.task,
            leaseExpiresAt: event.leaseExpiresAt
          });
          setRunnerResult("");
          setNotice({ kind: "status", text: t("cloudRunnerClaimed") });
        } else {
          setClaimedRunnerJob(undefined);
          setNotice({ kind: "status", text: t("cloudRunnerClaimEmpty") });
        }
        break;
      case "cloud/runner/completed":
        setClaimedRunnerJob((current) =>
          current?.claimHandle === event.claimHandle && current.jobId === event.jobId
            ? undefined
            : current);
        setRunnerResult("");
        setNotice({ kind: "status", text: t("cloudRunnerCompleted") });
        break;
      case "cloud/automations":
        setAutomations(event.automations);
        setNotice({ kind: "status", text: t("cloudAutomationsLoaded") });
        break;
      case "cloud/automation/created":
        setAutomations((current) => [
          event.automation,
          ...current.filter((item) => item.automationId !== event.automation.automationId)
        ]);
        setNotice({ kind: "status", text: t("cloudAutomationCreated") });
        break;
      case "cloud/automation/disabled":
        if (event.disabled) {
          setAutomations((current) => current.map((item) =>
            item.automationId === event.automationId ? { ...item, enabled: false } : item));
        }
        setNotice({ kind: "status", text: t("cloudAutomationDisabled") });
        break;
      case "cloud/error":
        setNotice({
          kind: "error",
          text: t("cloudOperationError").replace("{operation}", t(`cloudOperation_${event.operation}`))
        });
        break;
      case "cloud/cancelled":
        setNotice({ kind: "status", text: t("cloudOperationCancelled") });
        break;
      default:
        break;
    }
  }), [bridge, onSessionImported, t]);

  useEffect(() => {
    if (initializedRef.current || !bridge.available) {
      return;
    }
    initializedRef.current = true;
    runCloudOperation("profile-get", (requestId) => ({
      type: "cloud/profile/get",
      requestId
    }));
  }, [bridge]);

  const remoteProfileValid = validCloudEndpoint(draftProfile.baseUri) &&
    validCloudIdentifier(draftProfile.teamId) && validCloudIdentifier(draftProfile.deviceId);
  const profileDraftDirty = !sameCloudProfileConfiguration(activeProfile, draftProfile);
  const cloudReady = !activeProfile.localOnly && activeProfile.hasAccessToken && !profileDraftDirty;
  const busy = pendingOperation !== undefined;

  return (
    <section className="settings-feature-panel cloud-settings" aria-labelledby="cloud-settings-title">
      <header className="settings-feature-header">
        <div>
          <Cloud size={15} />
          <div>
            <h3 id="cloud-settings-title">{t("cloudTitle")}</h3>
            <p>{t("cloudDescription")}</p>
          </div>
        </div>
        <span className={activeProfile.localOnly ? "local" : "remote"}>
          {t(activeProfile.localOnly ? "cloudLocalOnly" : "cloudRemote")}
        </span>
      </header>

      <div className="settings-feature-body">
        <div className="cloud-profile-grid">
          <label className="provider-field">
            <span>{t("cloudBaseUri")}</span>
            <input
              type="url"
              value={draftProfile.baseUri}
              placeholder="https://cloud.example.com/"
              onChange={(event) => setDraftProfile((current) => ({
                ...current,
                baseUri: event.target.value
              }))}
            />
          </label>
          <label className="provider-field">
            <span>{t("cloudTeamId")}</span>
            <input
              type="text"
              value={draftProfile.teamId}
              onChange={(event) => setDraftProfile((current) => ({
                ...current,
                teamId: event.target.value
              }))}
            />
          </label>
          <label className="provider-field">
            <span>{t("cloudDeviceId")}</span>
            <input
              type="text"
              value={draftProfile.deviceId}
              onChange={(event) => setDraftProfile((current) => ({
                ...current,
                deviceId: event.target.value
              }))}
            />
          </label>
        </div>
        <p className="settings-security-note">
          <ShieldCheck size={13} />
          <span>{t("cloudTokenNativeOnly")}</span>
        </p>
        <div className="settings-inline-actions">
          <button
            type="button"
            disabled={busy || activeProfile.localOnly}
            onClick={() => runCloudOperation("profile-save-local", (requestId) => ({
              type: "cloud/profile/save-local",
              requestId
            }))}
          >{t("cloudUseLocalOnly")}</button>
          <button
            type="button"
            className="primary"
            disabled={busy || !remoteProfileValid}
            onClick={() => runCloudOperation("profile-save-remote", (requestId) => ({
              type: "cloud/profile/save-remote",
              requestId,
              baseUri: draftProfile.baseUri.trim(),
              teamId: draftProfile.teamId.trim(),
              deviceId: draftProfile.deviceId.trim()
            }))}
          >{t("cloudSaveRemote")}</button>
          <button
            type="button"
            disabled={busy}
            onClick={() => runCloudOperation("profile-get", (requestId) => ({
              type: "cloud/profile/get",
              requestId
            }))}
          ><RefreshCw size={13} /> {t("retry")}</button>
        </div>

        <div className="cloud-action-grid">
          <section>
            <h4>{t("cloudPairing")}</h4>
            <div className="settings-inline-actions">
              <button
                type="button"
                disabled={busy || !cloudReady}
                onClick={() => runCloudOperation("pairing-export", (requestId) => ({
                  type: "cloud/pairing/export",
                  requestId
                }))}
              ><Download size={13} /> {t("cloudExportPairing")}</button>
              <button
                type="button"
                disabled={busy || !cloudReady}
                onClick={() => runCloudOperation("pairing-import", (requestId) => ({
                  type: "cloud/pairing/import",
                  requestId
                }))}
              ><Upload size={13} /> {t("cloudImportPairing")}</button>
            </div>
          </section>

          <section>
            <h4>{t("cloudSessions")}</h4>
            <div className="settings-inline-actions">
              <button
                type="button"
                disabled={busy || !cloudReady || !activeSessionId}
                onClick={() => runCloudOperation("session-upload", (requestId) => ({
                  type: "cloud/session/upload",
                  requestId,
                  sessionId: activeSessionId!
                }))}
              ><Upload size={13} /> {t("cloudUploadSession")}</button>
              <button
                type="button"
                disabled={busy || !cloudReady || !activeSessionId}
                onClick={() => runCloudOperation("session-export", (requestId) => ({
                  type: "cloud/session/export",
                  requestId,
                  sessionId: activeSessionId!
                }))}
              ><Download size={13} /> {t("cloudExportSession")}</button>
            </div>
            <div className="settings-input-action">
              <input
                type="text"
                value={remoteSessionId}
                aria-label={t("cloudRemoteSessionId")}
                placeholder={t("cloudRemoteSessionId")}
                onChange={(event) => setRemoteSessionId(event.target.value)}
              />
              <button
                type="button"
                disabled={busy || !cloudReady || !validCloudIdentifier(remoteSessionId)}
                onClick={() => runCloudOperation("session-download", (requestId) => ({
                  type: "cloud/session/download",
                  requestId,
                  remoteSessionId: remoteSessionId.trim()
                }))}
              ><Download size={13} /> {t("cloudDownloadSession")}</button>
              <button
                type="button"
                className="danger"
                disabled={busy || !cloudReady || !validCloudIdentifier(remoteSessionId)}
                onClick={() => {
                  if (!window.confirm(t("cloudDeleteSessionConfirm"))) {
                    return;
                  }
                  runCloudOperation("session-delete", (requestId) => ({
                    type: "cloud/session/delete",
                    requestId,
                    remoteSessionId: remoteSessionId.trim()
                  }));
                }}
              >{t("cloudDeleteSession")}</button>
            </div>
          </section>

          <section>
            <h4>{t("cloudHandoff")}</h4>
            <div className="settings-input-action">
              <input
                type="text"
                value={targetDeviceId}
                aria-label={t("cloudTargetDeviceId")}
                placeholder={t("cloudTargetDeviceId")}
                onChange={(event) => setTargetDeviceId(event.target.value)}
              />
              <button
                type="button"
                disabled={busy || !cloudReady || !activeSessionId ||
                  !validCloudIdentifier(targetDeviceId)}
                onClick={() => runCloudOperation("handoff-create", (requestId) => ({
                  type: "cloud/handoff/create",
                  requestId,
                  sessionId: activeSessionId!,
                  targetDeviceId: targetDeviceId.trim()
                }))}
              ><GitFork size={13} /> {t("cloudCreateHandoff")}</button>
            </div>
            <button
              type="button"
              className="settings-wide-action"
              disabled={busy || !cloudReady}
              onClick={() => runCloudOperation("handoff-receive", (requestId) => ({
                type: "cloud/handoff/receive",
                requestId
              }))}
            ><Download size={13} /> {t("cloudReceiveHandoffs")}</button>
            {handoffImports.length > 0 && (
              <ul className="settings-compact-list">
                {handoffImports.map((item) => (
                  <li key={item.handoffId}>
                    <code>{item.sourceDeviceId}</code>
                    <span>{item.importedSessionId}</span>
                  </li>
                ))}
              </ul>
            )}
          </section>
        </div>

        <section className="cloud-policy-panel" aria-labelledby="cloud-policy-title">
          <header>
            <div>
              <ShieldCheck size={14} />
              <h4 id="cloud-policy-title">{t("cloudPolicy")}</h4>
            </div>
            <button
              type="button"
              disabled={busy || !cloudReady}
              onClick={() => runCloudOperation("policy-get", (requestId) => ({
                type: "cloud/policy/get",
                requestId
              }))}
            ><RefreshCw size={13} /> {t("cloudLoadPolicy")}</button>
          </header>
          <div className="cloud-policy-grid">
            <label className="provider-checkbox">
              <input
                type="checkbox"
                checked={policy.allowedExecutionProfiles.includes("NativeProtected")}
                onChange={(event) => setPolicy((current) => ({
                  ...current,
                  allowedExecutionProfiles: toggleExecutionProfile(
                    current.allowedExecutionProfiles,
                    "NativeProtected",
                    event.target.checked
                  )
                }))}
              />
              <span>{t("nativeProtected")}</span>
            </label>
            <label className="provider-checkbox">
              <input
                type="checkbox"
                checked={policy.allowedExecutionProfiles.includes("WslStrict")}
                onChange={(event) => setPolicy((current) => ({
                  ...current,
                  allowedExecutionProfiles: toggleExecutionProfile(
                    current.allowedExecutionProfiles,
                    "WslStrict",
                    event.target.checked
                  )
                }))}
              />
              <span>{t("wslStrict")}</span>
            </label>
            <label className="provider-checkbox">
              <input
                type="checkbox"
                checked={policy.remoteRunnerEnabled}
                onChange={(event) => setPolicy((current) => ({
                  ...current,
                  remoteRunnerEnabled: event.target.checked
                }))}
              />
              <span>{t("cloudRemoteRunnerExperimental")}</span>
            </label>
            <label className="provider-checkbox">
              <input
                type="checkbox"
                checked={policy.uiAutomationEnabled}
                onChange={(event) => setPolicy((current) => ({
                  ...current,
                  uiAutomationEnabled: event.target.checked
                }))}
              />
              <span>{t("cloudUiAutomationExperimental")}</span>
            </label>
            <label className="provider-field">
              <span>{t("cloudMaximumJobs")}</span>
              <input
                type="number"
                min={1}
                max={128}
                value={policy.maximumConcurrentJobs}
                onChange={(event) => setPolicy((current) => ({
                  ...current,
                  maximumConcurrentJobs: Number(event.target.value)
                }))}
              />
            </label>
            <label className="provider-field">
              <span>{t("cloudAllowedPublishers")}</span>
              <input
                type="text"
                value={policy.allowedPluginPublishers}
                onChange={(event) => setPolicy((current) => ({
                  ...current,
                  allowedPluginPublishers: event.target.value
                }))}
              />
            </label>
          </div>
          <button
            type="button"
            className="settings-wide-action primary"
            disabled={busy || !cloudReady || policy.allowedExecutionProfiles.length === 0 ||
              !Number.isInteger(policy.maximumConcurrentJobs) ||
              policy.maximumConcurrentJobs < 1 || policy.maximumConcurrentJobs > 128}
            onClick={() => runCloudOperation("policy-update", (requestId) => ({
              type: "cloud/policy/update",
              requestId,
              allowedExecutionProfiles: policy.allowedExecutionProfiles,
              remoteRunnerEnabled: policy.remoteRunnerEnabled,
              uiAutomationEnabled: policy.uiAutomationEnabled,
              maximumConcurrentJobs: policy.maximumConcurrentJobs,
              allowedPluginPublishers: splitCloudIdentifiers(policy.allowedPluginPublishers)
            }))}
          >{t("cloudSavePolicy")}</button>
        </section>

        <div className="cloud-action-grid">
          <section>
            <h4>{t("cloudRunner")}</h4>
            <div className="settings-input-action stacked">
              <input
                type="text"
                value={runnerId}
                aria-label={t("cloudRunnerId")}
                placeholder={t("cloudRunnerId")}
                onChange={(event) => setRunnerId(event.target.value)}
              />
              <input
                type="text"
                value={runnerCapabilities}
                aria-label={t("cloudRunnerCapabilities")}
                placeholder={t("cloudRunnerCapabilities")}
                onChange={(event) => setRunnerCapabilities(event.target.value)}
              />
              <button
                type="button"
                disabled={busy || !cloudReady || !validCloudIdentifier(runnerId) ||
                  splitCloudIdentifiers(runnerCapabilities).length === 0}
                onClick={() => runCloudOperation("runner-register", (requestId) => ({
                  type: "cloud/runner/register",
                  requestId,
                  runnerId: runnerId.trim(),
                  capabilities: splitCloudIdentifiers(runnerCapabilities)
                }))}
              ><Server size={13} /> {t("cloudRegisterRunner")}</button>
            </div>
            <div className="settings-input-action stacked">
              <input
                type="text"
                value={runnerRequiredCapability}
                aria-label={t("cloudRunnerRequiredCapability")}
                placeholder={t("cloudRunnerRequiredCapability")}
                onChange={(event) => setRunnerRequiredCapability(event.target.value)}
              />
              <input
                type="text"
                value={runnerTask}
                aria-label={t("cloudRunnerTask")}
                placeholder={t("cloudRunnerTask")}
                onChange={(event) => setRunnerTask(event.target.value)}
              />
              <button
                type="button"
                disabled={busy || !cloudReady ||
                  !validCloudIdentifier(runnerRequiredCapability) ||
                  !validCloudText(runnerTask, 64 * 1024)}
                onClick={() => runCloudOperation("runner-queue", (requestId) => ({
                  type: "cloud/runner/queue",
                  requestId,
                  requiredCapability: runnerRequiredCapability.trim(),
                  task: runnerTask.trim()
                }))}
              ><ListTodo size={13} /> {t("cloudQueueRunnerTask")}</button>
            </div>
            {queuedRunnerJobId && (
              <p className="settings-security-note">
                <ListTodo size={13} />
                <span>{t("cloudQueuedRunnerJob").replace("{id}", queuedRunnerJobId)}</span>
              </p>
            )}
            <div className="settings-input-action">
              <input
                type="number"
                min={1}
                max={3600}
                value={runnerLeaseSeconds}
                aria-label={t("cloudRunnerLeaseSeconds")}
                onChange={(event) => setRunnerLeaseSeconds(Number(event.target.value))}
              />
              <button
                type="button"
                disabled={busy || !cloudReady || !validCloudIdentifier(runnerId) ||
                  !Number.isInteger(runnerLeaseSeconds) || runnerLeaseSeconds < 10 ||
                  runnerLeaseSeconds > 600}
                onClick={() => runCloudOperation("runner-claim", (requestId) => ({
                  type: "cloud/runner/claim",
                  requestId,
                  runnerId: runnerId.trim(),
                  leaseSeconds: runnerLeaseSeconds
                }))}
              ><Download size={13} /> {t("cloudClaimRunnerTask")}</button>
            </div>
            {claimedRunnerJob && (
              <>
                <ul className="settings-compact-list">
                  <li>
                    <span>
                      <strong>{claimedRunnerJob.jobId}</strong>
                      <small>{claimedRunnerJob.task}</small>
                    </span>
                    <code>{claimedRunnerJob.requiredCapability}</code>
                  </li>
                </ul>
                <div className="settings-input-action stacked">
                  <input
                    type="text"
                    value={runnerResult}
                    aria-label={t("cloudRunnerResult")}
                    placeholder={t("cloudRunnerResult")}
                    onChange={(event) => setRunnerResult(event.target.value)}
                  />
                  <button
                    type="button"
                    disabled={busy || !cloudReady || !validCloudText(runnerResult, 256 * 1024)}
                    onClick={() => runCloudOperation("runner-complete", (requestId) => ({
                      type: "cloud/runner/complete",
                      requestId,
                      claimHandle: claimedRunnerJob.claimHandle,
                      jobId: claimedRunnerJob.jobId,
                      result: runnerResult.trim()
                    }))}
                  ><Check size={13} /> {t("cloudCompleteRunnerTask")}</button>
                </div>
              </>
            )}
          </section>

          <section>
            <h4>{t("cloudAutomations")}</h4>
            <div className="settings-input-action stacked">
              <input
                type="text"
                value={automationName}
                aria-label={t("cloudAutomationName")}
                placeholder={t("cloudAutomationName")}
                onChange={(event) => setAutomationName(event.target.value)}
              />
              <input
                type="number"
                min={1}
                max={2_678_400}
                value={automationIntervalSeconds}
                aria-label={t("cloudAutomationIntervalSeconds")}
                onChange={(event) => setAutomationIntervalSeconds(Number(event.target.value))}
              />
              <input
                type="text"
                value={automationRequiredCapability}
                aria-label={t("cloudAutomationRequiredCapability")}
                placeholder={t("cloudAutomationRequiredCapability")}
                onChange={(event) => setAutomationRequiredCapability(event.target.value)}
              />
              <input
                type="text"
                value={automationTask}
                aria-label={t("cloudAutomationTask")}
                placeholder={t("cloudAutomationTask")}
                onChange={(event) => setAutomationTask(event.target.value)}
              />
              <button
                type="button"
                disabled={busy || !cloudReady || !validCloudText(automationName, 128) ||
                  !Number.isInteger(automationIntervalSeconds) ||
                  automationIntervalSeconds < 1 || automationIntervalSeconds > 2_678_400 ||
                  !validCloudIdentifier(automationRequiredCapability) ||
                  !validCloudText(automationTask, 64 * 1024)}
                onClick={() => runCloudOperation("automation-create", (requestId) => ({
                  type: "cloud/automation/create",
                  requestId,
                  name: automationName.trim(),
                  intervalSeconds: automationIntervalSeconds,
                  requiredCapability: automationRequiredCapability.trim(),
                  task: automationTask.trim()
                }))}
              ><Plus size={13} /> {t("cloudCreateAutomation")}</button>
            </div>
            <button
              type="button"
              className="settings-wide-action"
              disabled={busy || !cloudReady}
              onClick={() => runCloudOperation("automation-list", (requestId) => ({
                type: "cloud/automation/list",
                requestId
              }))}
            ><RefreshCw size={13} /> {t("cloudLoadAutomations")}</button>
            {automations.length > 0 && (
              <ul className="settings-compact-list">
                {automations.map((automation) => (
                  <li key={automation.automationId}>
                    <span>
                      <strong>{automation.name}</strong>
                      <small>{automation.intervalSeconds}s</small>
                    </span>
                    <button
                      type="button"
                      disabled={busy || !automation.enabled}
                      onClick={() => runCloudOperation("automation-disable", (requestId) => ({
                        type: "cloud/automation/disable",
                        requestId,
                        automationId: automation.automationId
                      }))}
                    >{t(automation.enabled ? "cloudDisableAutomation" : "cloudAutomationDisabled")}</button>
                  </li>
                ))}
              </ul>
            )}
          </section>
        </div>

        {pendingOperation && (
          <p className="settings-feature-notice" role="status">
            <LoaderCircle className="session-spinner" size={13} />
            {t("cloudOperationRunning").replace(
              "{operation}",
              t(`cloudOperation_${pendingOperation}`)
            )}
          </p>
        )}
        {notice && (
          <p className={`settings-feature-notice ${notice.kind}`} role={
            notice.kind === "error" ? "alert" : "status"
          }>{notice.text}</p>
        )}
      </div>
    </section>
  );
}

function parseWindowsProcessId(value: string): number | undefined {
  const candidate = value.trim();
  if (!/^\d+$/u.test(candidate)) {
    return undefined;
  }
  const processId = Number(candidate);
  return Number.isSafeInteger(processId) && processId > 0 && processId <= 2_147_483_647
    ? processId
    : undefined;
}

function validWindowsAutomationTarget(value: string): boolean {
  const candidate = value.trim();
  return candidate.length > 0 && candidate.length <= 256;
}

function validCloudIdentifier(value: string): boolean {
  return /^[A-Za-z0-9._-]{1,128}$/.test(value.trim());
}

function validCloudText(value: string, maximumLength: number): boolean {
  const candidate = value.trim();
  return candidate.length > 0 && candidate.length <= maximumLength &&
    !/[\u0000-\u0008\u000b\u000c\u000e-\u001f\u007f]/u.test(candidate);
}

function validCloudEndpoint(value: string): boolean {
  try {
    const endpoint = new URL(value.trim());
    const loopback = endpoint.hostname === "localhost" || endpoint.hostname === "127.0.0.1" ||
      endpoint.hostname === "[::1]" || endpoint.hostname === "::1";
    return endpoint.username === "" && endpoint.password === "" && endpoint.search === "" &&
      endpoint.hash === "" && (endpoint.protocol === "https:" ||
        (endpoint.protocol === "http:" && loopback));
  } catch {
    return false;
  }
}

function splitCloudIdentifiers(value: string): string[] {
  return [...new Set(value.split(",").map((item) => item.trim()).filter(validCloudIdentifier))];
}

function toggleExecutionProfile(
  values: ExecutionProfile[],
  profile: ExecutionProfile,
  enabled: boolean
): ExecutionProfile[] {
  if (enabled) {
    return values.includes(profile) ? values : [...values, profile];
  }
  return values.filter((value) => value !== profile);
}

function buildWorktreeReviewRequest(
  worktree: WorktreeRecord,
  template: string
): string | undefined {
  const id = boundedWorktreeReviewValue(worktree.id, worktreeReviewIdMaxLength);
  const path = boundedWorktreeReviewValue(worktree.path, worktreeReviewPathMaxLength);
  const basePath = boundedWorktreeReviewValue(
    worktree.sourceRepository,
    worktreeReviewPathMaxLength
  );
  const baseReference = boundedWorktreeReviewValue(
    worktree.gitReference ?? worktree.headCommit ?? "HEAD",
    worktreeReviewBaseReferenceMaxLength
  );
  if (!id || !path || !basePath || !baseReference) {
    return undefined;
  }

  const request = template
    .replace("{id}", () => id)
    .replace("{path}", () => path)
    .replace("{baseReference}", () => baseReference)
    .replace("{basePath}", () => basePath);
  return request.length <= worktreeReviewRequestMaxLength ? request : undefined;
}

function boundedWorktreeReviewValue(value: string, maximumLength: number): string | undefined {
  const candidate = value.trim();
  return candidate.length > 0 && candidate.length <= maximumLength
    ? JSON.stringify(candidate)
    : undefined;
}

function WorktreeDashboard({
  worktrees,
  selectedWorktree,
  status,
  error,
  actionStatus,
  conflicts,
  operation,
  activeSessionId,
  engineStatus,
  workspaceGeneration,
  reviewDraft,
  interactionBlocked,
  onRefresh,
  onCreate,
  onInspect,
  onReview,
  onReviewChange,
  onStartReview,
  onCancelReview,
  onApply,
  onRemove,
  onGc,
  language,
  t
}: {
  worktrees: WorktreeRecord[];
  selectedWorktree?: WorktreeRecord;
  status: WorktreeStatus;
  error: string;
  actionStatus: string;
  conflicts: WorktreeConflict[];
  operation?: WorktreeOperation;
  activeSessionId?: string;
  engineStatus: EngineStatus;
  workspaceGeneration: number;
  reviewDraft?: WorktreeReviewDraft;
  interactionBlocked: boolean;
  onRefresh: () => void;
  onCreate: () => void;
  onInspect: (worktree: WorktreeRecord) => void;
  onReview: (worktree: WorktreeRecord) => void;
  onReviewChange: (request: string) => void;
  onStartReview: () => void;
  onCancelReview: () => void;
  onApply: (worktree: WorktreeRecord) => void;
  onRemove: (worktree: WorktreeRecord) => void;
  onGc: () => void;
  language: UiLanguage;
  t: (key: string) => string;
}) {
  const busy = operation !== undefined;
  const engineBusy = engineStatus === "starting" || engineStatus === "running";
  const currentReviewDraft = selectedWorktree && reviewDraft &&
    reviewDraft.worktreeId === selectedWorktree.id &&
    reviewDraft.workspaceGeneration === workspaceGeneration
    ? reviewDraft
    : undefined;
  const reviewContextAvailable = selectedWorktree
    ? buildWorktreeReviewRequest(selectedWorktree, t("worktreeReviewPrompt")) !== undefined
    : false;
  const reviewDisabled = busy || engineBusy || !activeSessionId ||
    selectedWorktree?.status !== "alive" || !reviewContextAvailable;

  return (
    <main
      className="worktree-dashboard"
      aria-label={t("worktrees")}
      aria-busy={busy}
      aria-hidden={interactionBlocked || undefined}
      inert={interactionBlocked || undefined}
    >
      <header className="worktree-dashboard-header">
        <div className="worktree-dashboard-title">
          <GitBranch size={17} />
          <div>
            <h1>{t("worktrees")}</h1>
            <span>{t("worktreeCount").replace("{count}", String(worktrees.length))}</span>
          </div>
        </div>
        <div className="worktree-toolbar" aria-label={t("worktreeActions")}>
          <button
            type="button"
            className="icon-button compact"
            aria-label={t("refreshWorktrees")}
            title={t("refreshWorktrees")}
            disabled={busy}
            onClick={onRefresh}
          >
            <RefreshCw className={operation === "list" ? "session-spinner" : undefined} size={15} />
          </button>
          <button
            type="button"
            className="worktree-command"
            aria-label={t("createWorktree")}
            disabled={busy || !activeSessionId}
            onClick={onCreate}
          >
            {operation === "create"
              ? <LoaderCircle className="session-spinner" size={14} />
              : <Plus size={14} />}
            <span>{t("createWorktree")}</span>
          </button>
          <button
            type="button"
            className="worktree-command secondary"
            aria-label={t("gcWorktrees")}
            disabled={busy}
            onClick={onGc}
          >
            {operation === "gc"
              ? <LoaderCircle className="session-spinner" size={14} />
              : <Trash2 size={14} />}
            <span>{t("gcWorktrees")}</span>
          </button>
        </div>
      </header>

      <div className="worktree-dashboard-scroll">
        {error && (
          <div className="worktree-alert error" role="alert">
            <span>{error}</span>
            {status === "error" && (
              <button type="button" disabled={busy} onClick={onRefresh}>{t("retry")}</button>
            )}
          </div>
        )}
        {conflicts.length > 0 && (
          <div className="worktree-alert conflict" role="alert">
            <strong>{t("worktreeConflicts")}</strong>
            <span>{conflicts.map((conflict) => conflict.path).join(", ")}</span>
          </div>
        )}
        {actionStatus && <p className="worktree-action-status" role="status">{actionStatus}</p>}

        {status === "loading" && worktrees.length === 0 ? (
          <div className="worktree-loading" role="status">
            <LoaderCircle className="session-spinner" size={16} />
            <span>{t("loadingWorktrees")}</span>
          </div>
        ) : status === "loaded" && worktrees.length === 0 ? (
          <div className="worktree-empty">
            <GitBranch size={25} />
            <strong>{t("noWorktrees")}</strong>
            <span>{t("noWorktreesDetail")}</span>
          </div>
        ) : (
          <div className="worktree-layout">
            <section className="worktree-list" aria-label={t("worktreeList")}>
              {worktrees.map((worktree) => {
                const label = worktreeLabel(worktree);
                return (
                  <article
                    key={worktree.id}
                    className={`worktree-row${selectedWorktree?.id === worktree.id ? " selected" : ""}`}
                  >
                    <div className="worktree-row-main">
                      <div className="worktree-row-title">
                        <GitBranch size={15} />
                        <strong>{label}</strong>
                        <span className={`worktree-state ${worktree.status}`}>{t(
                          `worktreeStatus_${worktree.status}`
                        )}</span>
                      </div>
                      <code title={worktree.path}>{worktree.path}</code>
                      <div className="worktree-row-meta">
                        <span>{t(`worktreeKind_${worktree.kind}`)}</span>
                        <time dateTime={worktree.createdAt}>
                          {formatSessionTime(worktree.createdAt, language)}
                        </time>
                      </div>
                    </div>
                    <div className="worktree-row-actions">
                      <button
                        type="button"
                        aria-label={t("inspectWorktreeLabel").replace("{label}", label)}
                        title={t("inspectWorktree")}
                        disabled={busy}
                        onClick={() => onInspect(worktree)}
                      ><Eye size={14} /></button>
                      <button
                        type="button"
                        aria-label={t("applyWorktreeLabel").replace("{label}", label)}
                        title={t("applyWorktree")}
                        disabled={busy || !activeSessionId || worktree.status !== "alive"}
                        onClick={() => onApply(worktree)}
                      ><GitMerge size={14} /></button>
                      <button
                        type="button"
                        className="danger"
                        aria-label={t("removeWorktreeLabel").replace("{label}", label)}
                        title={t("removeWorktree")}
                        disabled={busy}
                        onClick={() => onRemove(worktree)}
                      ><Trash2 size={14} /></button>
                    </div>
                  </article>
                );
              })}
            </section>

            {selectedWorktree && (
              <section className="worktree-detail" aria-labelledby="worktree-detail-title">
                <header>
                  <div>
                    <GitBranch size={15} />
                    <h2 id="worktree-detail-title">{t("worktreeDetails")}</h2>
                  </div>
                  <span>{t(`worktreeStatus_${selectedWorktree.status}`)}</span>
                </header>
                <strong>{worktreeLabel(selectedWorktree)}</strong>
                <dl>
                  <div><dt>{t("worktreePath")}</dt><dd><code>{selectedWorktree.path}</code></dd></div>
                  <div><dt>{t("sourceRepository")}</dt><dd><code>{selectedWorktree.sourceRepository}</code></dd></div>
                  <div><dt>{t("creationType")}</dt><dd>{t(
                    `worktreeCreation_${selectedWorktree.creationType}`
                  )}</dd></div>
                  {selectedWorktree.gitReference && (
                    <div><dt>{t("gitReference")}</dt><dd><code>{selectedWorktree.gitReference}</code></dd></div>
                  )}
                  {selectedWorktree.headCommit && (
                    <div><dt>{t("headCommit")}</dt><dd><code>{selectedWorktree.headCommit}</code></dd></div>
                  )}
                </dl>
                <button
                  type="button"
                  className="worktree-review-button"
                  title={reviewContextAvailable
                    ? t("reviewWorktree")
                    : t("worktreeReviewContextTooLarge")}
                  disabled={reviewDisabled}
                  onClick={() => onReview(selectedWorktree)}
                >
                  <GitPullRequest size={14} />
                  <span>{t("reviewWorktree")}</span>
                </button>
                {currentReviewDraft && (
                  <div className="worktree-review-editor">
                    <label htmlFor="worktree-review-request">{t("worktreeReviewRequest")}</label>
                    <textarea
                      id="worktree-review-request"
                      value={currentReviewDraft.request}
                      maxLength={worktreeReviewRequestMaxLength}
                      disabled={reviewDisabled}
                      onChange={(event) => onReviewChange(event.target.value)}
                    />
                    <div className="worktree-review-actions">
                      <button
                        type="button"
                        className="secondary"
                        disabled={engineBusy}
                        onClick={onCancelReview}
                      ><X size={13} /> {t("cancelWorktreeReview")}</button>
                      <button
                        type="button"
                        className="primary"
                        disabled={reviewDisabled || !currentReviewDraft.request.trim() ||
                          currentReviewDraft.request.length > worktreeReviewRequestMaxLength}
                        onClick={onStartReview}
                      ><Play size={13} /> {t("startWorktreeReview")}</button>
                    </div>
                  </div>
                )}
              </section>
            )}
          </div>
        )}
      </div>
    </main>
  );
}

function RuntimeDashboard({
  activeSessionId,
  backgroundTasks,
  subagents,
  selectedSubagent,
  status,
  error,
  actionStatus,
  pendingTaskKills,
  pendingSubagentCancels,
  interactionBlocked,
  onRefresh,
  onKillTask,
  onInspectSubagent,
  onCancelSubagent,
  language,
  t
}: {
  activeSessionId?: string;
  backgroundTasks: BackgroundTaskSnapshot[];
  subagents: SubagentSnapshot[];
  selectedSubagent?: SubagentSnapshot;
  status: RuntimeDashboardStatus;
  error: string;
  actionStatus: string;
  pendingTaskKills: Set<string>;
  pendingSubagentCancels: Set<string>;
  interactionBlocked: boolean;
  onRefresh: () => void;
  onKillTask: (task: BackgroundTaskSnapshot) => void;
  onInspectSubagent: (subagent: SubagentSnapshot) => void;
  onCancelSubagent: (subagent: SubagentSnapshot) => void;
  language: UiLanguage;
  t: (key: string) => string;
}) {
  const runningTaskCount = backgroundTasks.filter((task) => !task.completed).length;

  return (
    <main
      className="runtime-dashboard"
      aria-label={t("agentDashboard")}
      aria-hidden={interactionBlocked || undefined}
      inert={interactionBlocked || undefined}
    >
      <header className="runtime-dashboard-header">
        <div>
          <h1>{t("agentDashboard")}</h1>
          <span>{activeSessionId || t("noActiveSession")}</span>
        </div>
        <button
          type="button"
          className="icon-button compact"
          aria-label={t("refreshRuntime")}
          title={t("refreshRuntime")}
          disabled={!activeSessionId || status === "loading"}
          onClick={onRefresh}
        >
          <RefreshCw className={status === "loading" ? "session-spinner" : undefined} size={15} />
        </button>
      </header>

      <div className="runtime-dashboard-scroll">
        {!activeSessionId ? (
          <div className="runtime-empty">
            <Bot size={24} />
            <strong>{t("noActiveSession")}</strong>
            <span>{t("startTaskForDashboard")}</span>
          </div>
        ) : (
          <>
            {error && (
              <div className="runtime-error" role="alert">
                <span>{error}</span>
                <button type="button" onClick={onRefresh}>{t("retry")}</button>
              </div>
            )}
            {actionStatus && <p className="runtime-action-status" role="status">{actionStatus}</p>}

            {status === "loading" && backgroundTasks.length === 0 && subagents.length === 0 ? (
              <div className="runtime-loading" role="status">
                <LoaderCircle className="session-spinner" size={16} />
                <span>{t("loadingRuntime")}</span>
              </div>
            ) : (
              <div className="runtime-dashboard-content">
                <div className="runtime-summary" aria-label={t("runtimeSummary")}>
                  <span><strong>{runningTaskCount}</strong>{t("runningTasks")}</span>
                  <span><strong>{subagents.length}</strong>{t("runningAgents")}</span>
                </div>

                <section className="runtime-section" aria-labelledby="background-tasks-title">
                  <header>
                    <div>
                      <TerminalSquare size={15} />
                      <h2 id="background-tasks-title">{t("backgroundTasks")}</h2>
                    </div>
                    <span>{backgroundTasks.length}</span>
                  </header>
                  {backgroundTasks.length === 0 ? (
                    <p className="runtime-section-empty">{t("noBackgroundTasks")}</p>
                  ) : (
                    <div className="runtime-list">
                      {backgroundTasks.map((task) => {
                        const killing = pendingTaskKills.has(task.taskId);
                        return (
                          <article className="runtime-item" key={task.taskId}>
                            <div className="runtime-item-heading">
                              <span className={`runtime-state-dot ${task.completed ? "done" : "running"}`} />
                              <div>
                                <code>{task.command}</code>
                                <span title={task.workingDirectory}>{task.workingDirectory}</span>
                              </div>
                              {!task.completed && (
                                <button
                                  type="button"
                                  className="runtime-stop-button"
                                  aria-label={t("killTaskLabel").replace("{command}", task.command)}
                                  title={t("killBackgroundTask")}
                                  disabled={killing}
                                  onClick={() => onKillTask(task)}
                                >
                                  {killing
                                    ? <LoaderCircle className="session-spinner" size={13} />
                                    : <Square size={12} fill="currentColor" />}
                                </button>
                              )}
                            </div>
                            <div className="runtime-item-meta">
                              <span>{t(task.completed ? "runtimeCompleted" : "runtimeRunning")}</span>
                              <time dateTime={task.startedAt}>
                                {formatRuntimeTime(task.startedAt, language)}
                              </time>
                              {task.exitCode !== undefined && (
                                <span>{t("exitCode").replace("{code}", String(task.exitCode))}</span>
                              )}
                            </div>
                            {task.output && <pre>{truncateRuntimeOutput(task.output)}</pre>}
                          </article>
                        );
                      })}
                    </div>
                  )}
                </section>

                <section className="runtime-section" aria-labelledby="running-subagents-title">
                  <header>
                    <div>
                      <Bot size={15} />
                      <h2 id="running-subagents-title">{t("runningSubagents")}</h2>
                    </div>
                    <span>{subagents.length}</span>
                  </header>
                  {subagents.length === 0 ? (
                    <p className="runtime-section-empty">{t("noRunningSubagents")}</p>
                  ) : (
                    <div className="runtime-list">
                      {subagents.map((subagent) => {
                        const cancelling = pendingSubagentCancels.has(subagent.subagentId);
                        return (
                          <article className="runtime-item runtime-agent-item" key={subagent.subagentId}>
                            <button
                              type="button"
                              className="runtime-agent-summary"
                              aria-label={t("inspectSubagentLabel").replace(
                                "{description}",
                                subagent.description
                              )}
                              onClick={() => onInspectSubagent(subagent)}
                            >
                              <span className="runtime-state-dot running" />
                              <span>
                                <strong>{subagent.description}</strong>
                                <small>{subagent.subagentType} · {formatDuration(subagent.durationMs)}</small>
                              </span>
                            </button>
                            <div className="runtime-agent-metrics">
                              <span>{t("turnCount").replace("{count}", String(subagent.turnCount ?? 0))}</span>
                              <span>{t("toolCallCount").replace(
                                "{count}",
                                String(subagent.toolCallCount ?? 0)
                              )}</span>
                              {subagent.contextUsagePercent !== undefined && (
                                <span>{t("contextUsage").replace(
                                  "{percent}",
                                  String(subagent.contextUsagePercent)
                                )}</span>
                              )}
                            </div>
                            <button
                              type="button"
                              className="runtime-stop-button"
                              aria-label={t("cancelSubagentLabel").replace(
                                "{description}",
                                subagent.description
                              )}
                              title={t("cancelSubagent")}
                              disabled={cancelling}
                              onClick={() => onCancelSubagent(subagent)}
                            >
                              {cancelling
                                ? <LoaderCircle className="session-spinner" size={13} />
                                : <X size={14} />}
                            </button>
                          </article>
                        );
                      })}
                    </div>
                  )}
                </section>

                {selectedSubagent && (
                  <section className="runtime-detail" aria-labelledby="subagent-detail-title">
                    <header>
                      <div>
                        <Bot size={15} />
                        <h2 id="subagent-detail-title">{t("subagentDetail")}</h2>
                      </div>
                      <span>{t(`subagentStatus_${selectedSubagent.status}`)}</span>
                    </header>
                    <strong>{selectedSubagent.description}</strong>
                    <dl>
                      <div><dt>{t("duration")}</dt><dd>{formatDuration(selectedSubagent.durationMs)}</dd></div>
                      <div><dt>{t("subagentType")}</dt><dd>{selectedSubagent.subagentType}</dd></div>
                      <div><dt>{t("childSession")}</dt><dd>{selectedSubagent.childSessionId}</dd></div>
                    </dl>
                    {selectedSubagent.output && (
                      <pre>{truncateRuntimeOutput(selectedSubagent.output)}</pre>
                    )}
                    {selectedSubagent.failureError && (
                      <p className="runtime-detail-error">{selectedSubagent.failureError}</p>
                    )}
                  </section>
                )}
              </div>
            )}
          </>
        )}
      </div>
    </main>
  );
}

function withoutSetItem(values: Set<string>, item: string): Set<string> {
  const next = new Set(values);
  next.delete(item);
  return next;
}

function formatDuration(milliseconds: number): string {
  const totalSeconds = Math.max(0, Math.floor(milliseconds / 1000));
  if (totalSeconds < 60) {
    return `${totalSeconds}s`;
  }
  const minutes = Math.floor(totalSeconds / 60);
  const seconds = totalSeconds % 60;
  return `${minutes}m ${seconds}s`;
}

function formatRuntimeTime(timestamp: string, language: UiLanguage): string {
  return new Intl.DateTimeFormat(language, {
    hour: "2-digit",
    minute: "2-digit",
    second: "2-digit"
  }).format(new Date(timestamp));
}

function truncateRuntimeOutput(output: string): string {
  const maximumLength = 4000;
  return output.length <= maximumLength
    ? output
    : `${output.slice(0, maximumLength)}\n...`;
}

function trimConversationAtPrompt(
  entries: ConversationEntry[],
  targetPromptIndex: number
): ConversationEntry[] {
  let promptIndex = 0;
  for (let index = 0; index < entries.length; index += 1) {
    const entry = entries[index];
    if (entry.type !== "message" || entry.role !== "user") {
      continue;
    }
    if (promptIndex === targetPromptIndex) {
      return entries.slice(0, index);
    }
    promptIndex += 1;
  }
  return entries;
}

function radioNavigationIndex(
  key: string,
  index: number,
  length: number
): number | undefined {
  if (length === 0) {
    return undefined;
  }
  if (key === "Home") {
    return 0;
  }
  if (key === "End") {
    return length - 1;
  }
  if (key === "ArrowLeft" || key === "ArrowUp") {
    return (index - 1 + length) % length;
  }
  if (key === "ArrowRight" || key === "ArrowDown") {
    return (index + 1) % length;
  }
  return undefined;
}

function rewindModeTranslationKey(mode: SessionRewindMode): string {
  switch (mode) {
    case "all":
      return "rewindAll";
    case "conversation_only":
      return "rewindConversationOnly";
    case "files_only":
      return "rewindFilesOnly";
  }
}

function mergeSessionPages(
  current: SessionSummary[],
  incoming: SessionSummary[]
): SessionSummary[] {
  const merged = [...current];
  const indexes = new Map(merged.map((session, index) => [session.sessionId, index]));
  for (const session of incoming) {
    const existingIndex = indexes.get(session.sessionId);
    if (existingIndex === undefined) {
      indexes.set(session.sessionId, merged.length);
      merged.push(session);
    } else {
      merged[existingIndex] = session;
    }
  }
  return merged;
}

function mergeExistingSessionUpdates(
  current: SessionSummary[],
  incoming: SessionSummary[]
): SessionSummary[] {
  const updates = new Map(incoming.map((session) => [session.sessionId, session]));
  return current.map((session) => updates.get(session.sessionId) ?? session);
}

function workspaceName(workspacePath: string): string {
  const segments = workspacePath.split(/[\\/]/).filter(Boolean);
  return segments.at(-1) || workspacePath;
}

function worktreeLabel(worktree: WorktreeRecord): string {
  return worktree.metadata?.label || worktree.repositoryName || worktree.id;
}

function sameWorktreePath(left: string, right: string): boolean {
  const normalize = (value: string) => value.replaceAll("/", "\\").toLocaleLowerCase();
  return normalize(left) === normalize(right);
}

function formatSessionTime(timestamp: string, language: UiLanguage): string {
  return new Intl.DateTimeFormat(language, {
    month: "numeric",
    day: "numeric",
    hour: "2-digit",
    minute: "2-digit"
  }).format(new Date(timestamp));
}

function attachmentErrorKey(error: ImageAttachmentError): string {
  switch (error) {
    case "unsupported_type":
      return "attachmentUnsupportedType";
    case "too_many":
      return "attachmentTooMany";
    case "too_large":
      return "attachmentTooLarge";
    case "total_too_large":
      return "attachmentTotalTooLarge";
    case "duplicate_name":
      return "attachmentDuplicateName";
    case "content_mismatch":
      return "attachmentContentMismatch";
    case "read_failed":
      return "attachmentReadFailed";
  }
}

function validateProviderUrl(
  value: string,
  allowInsecureTransport: boolean,
  invalidMessage: string,
  insecureMessage: string
): string | undefined {
  const candidate = value.trim();
  if (!candidate) {
    return undefined;
  }

  try {
    const url = new URL(candidate);
    if (url.protocol !== "https:" && url.protocol !== "http:") {
      return invalidMessage;
    }
    if (url.protocol === "http:" && !allowInsecureTransport) {
      return insecureMessage;
    }
    return undefined;
  } catch {
    return invalidMessage;
  }
}

function MarkdownMessage({ text }: { text: string }) {
  return (
    <div className="markdown-body">
      <ReactMarkdown
        remarkPlugins={[remarkGfm]}
        skipHtml
        disallowedElements={["img"]}
        components={{
          a(properties) {
            const { node, ...anchorProperties } = properties;
            void node;
            return <a {...anchorProperties} target="_blank" rel="noopener noreferrer" />;
          }
        }}
      >
        {text}
      </ReactMarkdown>
    </div>
  );
}

function ToolTimeline({
  entry,
  t
}: {
  entry: ToolTimelineEntry;
  t: (key: string) => string;
}) {
  const input = formatToolInput(entry.rawInput);
  const execute = entry.kind === "execute";
  return (
    <section
      className="tool-timeline"
      role="group"
      aria-label={`${t("toolCall")}：${entry.title}`}
    >
      <div className="tool-timeline-heading">
        {execute ? <TerminalSquare size={15} /> : <Bot size={15} />}
        <span className="tool-timeline-title">{entry.title}</span>
        <span className={`tool-status ${toolStatusClass(entry.status)}`}>
          {toolStatusLabel(entry.status, t)}
        </span>
      </div>
      {input && <pre><code>{input}</code></pre>}
    </section>
  );
}

function IconButton({
  label,
  active = false,
  buttonRef,
  onClick,
  children
}: {
  label: string;
  active?: boolean;
  buttonRef?: React.Ref<HTMLButtonElement>;
  onClick?: () => void;
  children: React.ReactNode;
}) {
  return (
    <button
      className={`rail-button${active ? " active" : ""}`}
      ref={buttonRef}
      aria-label={label}
      title={label}
      onClick={onClick}
    >{children}</button>
  );
}

function formatToolInput(value: unknown): string {
  const record = asRecord(value);
  if (record) {
    const command = nonEmptyString(record.command) ?? nonEmptyString(record.cmd);
    if (command) {
      const args = Array.isArray(record.args)
        ? record.args.filter((arg): arg is string | number =>
          typeof arg === "string" || typeof arg === "number")
        : [];
      return [command, ...args.map(String)].join(" ");
    }
  }
  if (value === undefined) {
    return "";
  }
  const serialized = typeof value === "string" ? value : JSON.stringify(value, null, 2);
  if (!serialized) {
    return "";
  }
  const maximumLength = 8 * 1024;
  return serialized.length <= maximumLength
    ? serialized
    : `${serialized.slice(0, maximumLength)}\n…`;
}

function toolStatusLabel(status: string, t: (key: string) => string): string {
  switch (status) {
    case "completed":
      return t("toolCompleted");
    case "failed":
      return t("toolFailed");
    case "in_progress":
      return t("toolRunning");
    default:
      return t("toolPending");
  }
}

function toolStatusClass(status: string): string {
  return status === "completed" || status === "failed" || status === "in_progress"
    ? status
    : "pending";
}

function nonEmptyString(value: unknown): string | undefined {
  return typeof value === "string" && value.trim() ? value : undefined;
}

function asRecord(value: unknown): Record<string, unknown> | undefined {
  return typeof value === "object" && value !== null && !Array.isArray(value)
    ? value as Record<string, unknown>
    : undefined;
}

function formatPermissionInput(value: unknown): string {
  if (typeof value === "string") {
    return value;
  }

  const serialized = JSON.stringify(value, null, 2);
  return serialized ?? String(value);
}

function permissionKindLabel(
  kind: PermissionRequestEvent["options"][number]["kind"],
  t: (key: string) => string
): string {
  switch (kind) {
    case "allow_once":
      return t("permissionAllowOnce");
    case "allow_always":
      return t("permissionAllowAlways");
    case "reject_once":
      return t("permissionRejectOnce");
    case "reject_always":
      return t("permissionRejectAlways");
  }
}
