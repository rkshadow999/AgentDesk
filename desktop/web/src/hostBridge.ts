export const hostSchemaVersion = 1 as const;

const maximumPendingHostRequests = 256;
const maximumQueuedHostCommands = 256;
const maximumRememberedRequestIds = 4096;
const maximumMemoryFiles = 512;
const maximumMemoryContentBytes = 64 * 1024;
const maximumMemoryMessageCharacters = 4 * 1024;
const memoryConfirmationTokenPattern = /^[0-9A-F]{64}$/u;
const documentTokenPattern = /^[0-9A-F]{64}$/u;

export type ExecutionProfile = "NativeProtected" | "WslStrict";

export type SessionMode = "default" | "plan";

export type UiLanguage = "zh-CN" | "en-US";

export type WindowsAutomationAction = "focus-window" | "invoke" | "set-value";

export type WindowsAutomationErrorReason = "disabled" | "busy" | "failed";

export type PromptAttachment = {
  token: string;
  name: string;
  mimeType: "image/png" | "image/jpeg" | "image/gif" | "image/webp";
  size: number;
};

export type ImageAttachmentError =
  | "unsupported_type"
  | "too_many"
  | "too_large"
  | "total_too_large"
  | "duplicate_name"
  | "content_mismatch"
  | "read_failed";

export type RuntimeSkillScope = "local" | "repo" | "user" | "plugin";

export type WorktreeCopyMode = "clean" | "dirty";
export type WorktreeCreationType = "linked" | "standalone" | "git";
export type WorktreeCreateStatus = "creating" | "exists";
export type WorktreeKind = "session" | "ab" | "pool" | "fork" | "manual" | "subagent";
export type WorktreeRecordStatus = "alive" | "dead";
export type WorktreeApplyMode = "overwrite" | "merge";
export type WorktreeApplyStatus = "success" | "conflicts";
export type WorktreeChangeType =
  | "create"
  | "edit"
  | "delete"
  | "rename"
  | "copy"
  | "type_change"
  | "untracked";
export type WorktreeOperation = "create" | "list" | "show" | "apply" | "remove" | "gc";

export type WorktreeRecord = {
  id: string;
  path: string;
  sourceRepository: string;
  repositoryName: string;
  kind: WorktreeKind;
  creationType: WorktreeCreationType;
  gitReference?: string;
  headCommit?: string;
  sessionId?: string;
  creatorProcessId?: number;
  createdAt: string;
  lastAccessedAt?: string;
  status: WorktreeRecordStatus;
  metadata?: { label: string; userProvided: boolean };
};

export type WorktreeFileChange = {
  path: string;
  oldPath?: string;
  changeType: WorktreeChangeType;
  staged?: boolean;
  additions: number;
  deletions: number;
  patch?: string;
  patchBytes?: number;
  patchLines?: number;
  oldText?: string;
  newText?: string;
};

export type WorktreeConflict = {
  path: string;
  changeType: WorktreeChangeType;
  base?: string;
  ours?: string;
  theirs?: string;
};

export type RuntimeCommand = {
  name: string;
  description: string;
  input?: { hint: string };
  skill?: { scope: RuntimeSkillScope; path: string };
};

export type SessionRewindMode = "all" | "conversation_only" | "files_only";

export type SessionRewindPoint = {
  promptIndex: number;
  createdAt: string;
  fileSnapshotCount: number;
  hasFileChanges: boolean;
  promptPreview?: string;
};

export type SessionRewindConflict = {
  path: string;
  conflictType: string;
};

export type SessionSummary = {
  sessionId: string;
  title: string;
  workspacePath: string;
  createdAt: string;
  updatedAt: string;
  messageCount: number;
  modelId?: string;
  parentSessionId?: string;
  branch?: string;
  worktreeLabel?: string;
  sourceWorkspacePath?: string;
};

export type BackgroundTaskKind = "bash" | "monitor";

export type BackgroundTaskKillOutcome = "killed" | "already_exited" | "not_found";

export type BackgroundTaskSnapshot = {
  taskId: string;
  command: string;
  workingDirectory: string;
  startedAt: string;
  endedAt?: string;
  output: string;
  truncated: boolean;
  exitCode?: number;
  signal?: string;
  completed: boolean;
  kind: BackgroundTaskKind;
  explicitlyKilled: boolean;
  ownerSessionId?: string;
};

export type SubagentStatus = "initializing" | "running" | "completed" | "failed" | "cancelled";

export type SubagentCancelOutcome = "cancelled" | "already_finished" | "not_found";

export type RuntimeDashboardOperation =
  | "refresh"
  | "task_kill"
  | "subagent_get"
  | "subagent_cancel";

export type SubagentSnapshot = {
  subagentId: string;
  parentSessionId: string;
  childSessionId: string;
  subagentType: string;
  description: string;
  startedAt: string;
  durationMs: number;
  status: SubagentStatus;
  turnCount?: number;
  toolCallCount?: number;
  tokensUsed?: number;
  contextWindowTokens?: number;
  contextUsagePercent?: number;
  toolsUsed?: string[];
  errorCount?: number;
  output?: string;
  worktreePath?: string;
  failureError?: string;
  cancelReason?: string;
  forkContextSource?: string;
  forkParentPromptId?: string;
  resumedFrom?: string;
};

export type ProviderBackend = "chat_completions" | "responses";

export type MaintenanceOperation =
  | "session-export"
  | "session-import"
  | "backup-create"
  | "backup-restore"
  | "update-check"
  | "update-apply";

export type CloudOperation =
  | "profile-get"
  | "profile-save-local"
  | "profile-save-remote"
  | "pairing-export"
  | "pairing-import"
  | "session-upload"
  | "session-download"
  | "session-delete"
  | "session-export"
  | "handoff-create"
  | "handoff-receive"
  | "policy-get"
  | "policy-update"
  | "runner-register"
  | "runner-queue"
  | "runner-claim"
  | "runner-complete"
  | "automation-create"
  | "automation-list"
  | "automation-disable";

export type CloudHandoffImport = {
  handoffId: string;
  sourceDeviceId: string;
  remoteSessionId: string;
  importedSessionId: string;
};

export type CloudAutomation = {
  automationId: string;
  name: string;
  intervalSeconds: number;
  enabled: boolean;
  nextRunAt: string;
};

export type ExtensionScope = "mcp" | "skills" | "hooks" | "plugins" | "marketplace";

export type ExtensionActionStatus =
  | "success"
  | "validation_error"
  | "confirmation_required"
  | "not_found"
  | "internal_error"
  | "unsupported";

export type ExtensionMcpServer = {
  name: string;
  displayName?: string;
  source: "managed" | "local";
  sourceLabel?: string;
  transport: "http" | "stdio" | "managed_gateway";
  url?: string;
  scope?: string;
  scopeId?: string;
  scopeName?: string;
  command?: string;
  arguments: string[];
  environmentVariableNames: string[];
  session?: {
    enabled: boolean;
    status?: "ready" | "initializing" | "unavailable";
    tools: Array<{
      name: string;
      displayName?: string;
      description?: string;
      enabled: boolean;
    }>;
    authRequired: boolean;
  };
};

export type ExtensionSkill = {
  name: string;
  displayName?: string;
  description: string;
  paths: string[];
  path: string;
  scope: "local" | "repo" | "user" | "server" | "bundled" | "plugin";
  pluginName?: string;
  pluginVersion?: string;
  allowedTools: string[];
  model?: string;
  effort?: string;
  userInvocable: boolean;
  disableModelInvocation: boolean;
  enabled: boolean;
};

export type ExtensionHook = {
  name: string;
  event: string;
  handlerType: "command" | "http";
  matcher?: string;
  hasCommand: boolean;
  hasUrl: boolean;
  timeoutMs: number;
  sourceDirectory: string;
  disabled: boolean;
};

export type ExtensionPlugin = {
  name: string;
  id: string;
  root: string;
  scope: "cli" | "project" | "user" | "config";
  trusted: boolean;
  enabled: boolean;
  version?: string;
  description?: string;
  skillCount: number;
  skillNames: string[];
  agentCount: number;
  agentNames: string[];
  hookStatus: "active" | "active_inline" | "blocked" | "none";
  hookCount: number;
  mcpServerCount: number;
  mcpStatus: "active" | "active_inline" | "blocked" | "none";
  marketplaceSource?: string;
  origin?: { type: string; marketplace?: string; sourceName?: string };
  conflict?: string;
};

export type ExtensionMarketplaceSource = {
  name: string;
  kind: "git" | "local" | "failed";
  source: string;
  plugins: Array<{
    name: string;
    source: string;
    version?: string;
    description?: string;
    category?: string;
    author?: string;
    tags: string[];
    keywords: string[];
    domains: string[];
    homepage?: string;
    relativePath: string;
    skillCount: number;
    hasHooks: boolean;
    hasAgents: boolean;
    hasMcp: boolean;
    installStatus: "not_installed" | "installed" | "update_available";
    installedVersion?: string;
  }>;
};

export type ExtensionsCatalog = {
  mcp: { servers: ExtensionMcpServer[] };
  skills: {
    skills: ExtensionSkill[];
    configuration?: { paths: string[]; ignoredPaths: string[]; totalSkills: number };
  };
  hooks: { hooks: ExtensionHook[]; projectTrusted: boolean; loadErrorCount: number };
  plugins: { plugins: ExtensionPlugin[] };
  marketplace: { sources: ExtensionMarketplaceSource[] };
};

export type UpdateStatus =
  | "checking"
  | "up-to-date"
  | "available"
  | "launching"
  | "unsupported"
  | "error";

export type EngineCapabilities = {
  executionProfiles: ExecutionProfile[];
  wslStrictReason?: string;
  imagePrompts?: boolean;
  sessionModes?: SessionMode[];
};

export type PermissionOptionKind =
  | "allow_once"
  | "allow_always"
  | "reject_once"
  | "reject_always";

export type PermissionOption = {
  optionId: string;
  name: string;
  kind: PermissionOptionKind;
};

export type WorkspaceContextFile = {
  relativePath: string;
  byteLength: number;
  lastWriteTime: string;
};

export type WorkspaceContextOperation =
  | "instructions-list"
  | "file-read"
  | "instructions-write"
  | "file-search";

export type MemoryOperation = "list" | "read" | "write" | "delete";
export type MemoryFileScope = "global" | "workspace" | "session";
export type MemoryMutationStatus = "confirmation_required" | "success" | "not_found";

export type MemoryCapabilities = {
  schemaVersion: 0 | 1;
  list: boolean;
  read: boolean;
  write: boolean;
  delete: boolean;
  mutationConfirmationRequired: boolean;
};

export type MemoryFile = {
  id: string;
  scope: MemoryFileScope;
  name: string;
  byteLength: number;
  modifiedAt?: string;
  writable: boolean;
};

export type HostCommand =
  | { type: "ui/ready" }
  | { type: "ui/modal"; isOpen: boolean }
  | {
      type: "ui/preferences/save";
      language: UiLanguage;
      composerDraft: string;
      sessionMode: SessionMode;
      executionProfile: ExecutionProfile;
      notificationsEnabled: boolean;
      windowsAutomationEnabled: boolean;
      backgroundUpdateChecksEnabled: boolean;
    }
  | { type: "workspace/select" }
  | {
      type: "workspace/context/instructions/list";
      requestId: string;
      workspaceGeneration: number;
    }
  | {
      type: "workspace/context/file/read";
      requestId: string;
      workspaceGeneration: number;
      relativePath: string;
    }
  | {
      type: "workspace/context/instructions/write";
      requestId: string;
      workspaceGeneration: number;
      relativePath: string;
      content: string;
    }
  | {
      type: "workspace/context/file/search";
      requestId: string;
      workspaceGeneration: number;
      query: string;
    }
  | {
      type: "session/list";
      requestId: string;
      query: string;
      cursor?: string;
      limit: number;
      archived?: boolean;
    }
  | {
      type: "session/open";
      sessionId: string;
      workspacePath: string;
      executionProfile: ExecutionProfile;
    }
  | {
      type: "session/rename";
      requestId: string;
      sessionId: string;
      title: string;
      workspacePath: string;
    }
  | { type: "session/archive"; requestId: string; sessionId: string; archived: boolean }
  | {
      type: "session/fork";
      sessionId: string;
      sourceWorkspacePath: string;
      targetWorkspacePath: string;
      targetPromptIndex?: number;
    }
  | { type: "session/compact"; sessionId: string; userContext?: string }
  | { type: "session/rewind/points"; sessionId: string }
  | {
      type: "session/rewind";
      sessionId: string;
      targetPromptIndex: number;
      mode: SessionRewindMode;
      force: boolean;
    }
  | { type: "runtime/dashboard/refresh"; sessionId: string }
  | { type: "runtime/task/kill"; sessionId: string; taskId: string }
  | { type: "runtime/subagent/get"; sessionId: string; subagentId: string }
  | { type: "runtime/subagent/cancel"; sessionId: string; subagentId: string }
  | { type: "runtime/commands/list"; workspaceGeneration: number }
  | { type: "runtime/memory/flush"; sessionId: string }
  | {
      type: "memory/list";
      requestId: string;
      workspaceGeneration: number;
      sessionId: string;
    }
  | {
      type: "memory/read";
      requestId: string;
      workspaceGeneration: number;
      sessionId: string;
      fileId: string;
    }
  | {
      type: "memory/write";
      requestId: string;
      workspaceGeneration: number;
      sessionId: string;
      fileId: string;
      content: string;
      confirmed: boolean;
      confirmationToken?: string;
    }
  | {
      type: "memory/delete";
      requestId: string;
      workspaceGeneration: number;
      sessionId: string;
      fileId: string;
      confirmed: boolean;
      confirmationToken?: string;
    }
  | {
      type: "worktree/create";
      workspaceGeneration: number;
      sessionId: string;
      copyMode: WorktreeCopyMode;
      gitReference?: string;
      copyIgnoredInBackground: boolean;
      ignoredSkipPatterns: string[];
      creationType?: WorktreeCreationType;
      label?: string;
      destinationPath?: string;
    }
  | {
      type: "worktree/list";
      workspaceGeneration: number;
      includeAll: boolean;
      types: WorktreeKind[];
    }
  | { type: "worktree/show"; workspaceGeneration: number; idOrPath: string }
  | {
      type: "worktree/apply";
      workspaceGeneration: number;
      sessionId: string;
      worktreePath: string;
      mode: WorktreeApplyMode;
    }
  | {
      type: "worktree/remove";
      workspaceGeneration: number;
      idOrPath: string;
      force: boolean;
      dryRun: boolean;
    }
  | {
      type: "worktree/gc";
      workspaceGeneration: number;
      dryRun: boolean;
      maximumAgeSeconds?: number;
      force: boolean;
    }
  | {
      type: "provider/save";
      baseUrl: string;
      model: string;
      backend: ProviderBackend;
      allowInsecureTransport: boolean;
      useExistingCredential: boolean;
      replaceCredential: boolean;
    }
  | { type: "attachment/select"; requestId: string }
  | { type: "attachment/discard"; tokens: string[] }
  | {
      type: "engine/prompt";
      text: string;
      executionProfile: ExecutionProfile;
      sessionMode: SessionMode;
      nativeRiskAcknowledged: boolean;
      workspaceGeneration: number;
      attachments?: PromptAttachment[];
    }
  | { type: "engine/cancel"; sessionId: string }
  | { type: "session/export"; requestId: string; sessionId: string }
  | { type: "session/import"; requestId: string }
  | { type: "backup/create"; requestId: string }
  | { type: "backup/restore"; requestId: string }
  | { type: "update/check"; requestId: string }
  | { type: "update/apply"; requestId: string }
  | { type: "cloud/profile/get"; requestId: string }
  | { type: "cloud/profile/save-local"; requestId: string }
  | {
      type: "cloud/profile/save-remote";
      requestId: string;
      baseUri: string;
      teamId: string;
      deviceId: string;
    }
  | { type: "cloud/pairing/export"; requestId: string }
  | { type: "cloud/pairing/import"; requestId: string }
  | { type: "cloud/session/upload"; requestId: string; sessionId: string }
  | { type: "cloud/session/download"; requestId: string; remoteSessionId: string }
  | { type: "cloud/session/delete"; requestId: string; remoteSessionId: string }
  | { type: "cloud/session/export"; requestId: string; sessionId: string }
  | {
      type: "cloud/handoff/create";
      requestId: string;
      sessionId: string;
      targetDeviceId: string;
    }
  | { type: "cloud/handoff/receive"; requestId: string }
  | { type: "cloud/policy/get"; requestId: string }
  | {
      type: "cloud/policy/update";
      requestId: string;
      allowedExecutionProfiles: ExecutionProfile[];
      remoteRunnerEnabled: boolean;
      uiAutomationEnabled: boolean;
      maximumConcurrentJobs: number;
      allowedPluginPublishers: string[];
    }
  | {
      type: "cloud/runner/register";
      requestId: string;
      runnerId: string;
      capabilities: string[];
    }
  | {
      type: "cloud/runner/queue";
      requestId: string;
      requiredCapability: string;
      task: string;
    }
  | {
      type: "cloud/runner/claim";
      requestId: string;
      runnerId: string;
      leaseSeconds: number;
    }
  | {
      type: "cloud/runner/complete";
      requestId: string;
      claimHandle: string;
      jobId: string;
      result: string;
    }
  | {
      type: "cloud/automation/create";
      requestId: string;
      name: string;
      intervalSeconds: number;
      requiredCapability: string;
      task: string;
    }
  | { type: "cloud/automation/list"; requestId: string }
  | { type: "cloud/automation/disable"; requestId: string; automationId: string }
  | {
      type: "extensions/list";
      requestId: string;
      workspaceGeneration: number;
      sessionId?: string;
      useCache: boolean;
    }
  | {
      type: "extensions/action";
      requestId: string;
      workspaceGeneration: number;
      sessionId: string;
      scope: ExtensionScope;
      action: string;
      confirmed: boolean;
      payload: Record<string, unknown>;
    }
  | {
      type: "windows/automation/execute";
      requestId: string;
      action: WindowsAutomationAction;
      processId: number;
      automationId?: string;
      name?: string;
      value?: string;
    }
  | {
      type: "permission/respond";
      requestId: string;
      outcome: "selected";
      optionId: string;
    }
  | { type: "permission/respond"; requestId: string; outcome: "cancelled" };

export type EngineStatus = "idle" | "starting" | "ready" | "running" | "stopped" | "error";

export type HostEvent =
  | {
      type: "engine/status";
      status: EngineStatus;
      message?: string;
      sessionId?: string;
      engineEpoch?: number;
      capabilities?: EngineCapabilities;
    }
  | { type: "workspace/selected"; path: string; workspaceGeneration: number }
  | {
      type: "workspace/context/instructions/list";
      requestId: string;
      workspaceGeneration: number;
      files: WorkspaceContextFile[];
    }
  | {
      type: "workspace/context/file/read";
      requestId: string;
      workspaceGeneration: number;
      relativePath: string;
      content: string;
    }
  | {
      type: "workspace/context/instructions/write";
      requestId: string;
      workspaceGeneration: number;
      relativePath: string;
    }
  | {
      type: "workspace/context/file/search";
      requestId: string;
      workspaceGeneration: number;
      query: string;
      files: WorkspaceContextFile[];
    }
  | {
      type: "workspace/context/error";
      requestId: string;
      workspaceGeneration: number;
      operation: WorkspaceContextOperation;
    }
  | {
      type: "engine/capabilities";
      sessionId: string;
      imagePrompts: boolean;
      sessionModes: SessionMode[];
    }
  | {
      type: "attachment/changed";
      requestId: string;
      attachments: PromptAttachment[];
      error?: ImageAttachmentError;
      cancelled?: boolean;
    }
  | { type: "credential/status"; status: "saved" | "deleted" | "error"; message?: string }
  | {
      type: "provider/status";
      status: "loaded" | "saved" | "error";
      baseUrl: string;
      model: string;
      backend: ProviderBackend;
      allowInsecureTransport: boolean;
      hasCredential: boolean;
      message?: string;
    }
  | {
      type: "session/update";
      sessionId: string;
      updateKind: string;
      engineEpoch: number;
      text?: string;
      update?: unknown;
    }
  | { type: "prompt/completed"; sessionId: string; stopReason: string }
  | {
      type: "session/list/changed";
      requestId?: string;
      sessions: SessionSummary[];
      nextCursor?: string;
    }
  | { type: "session/list/error"; requestId: string; message: string }
  | {
      type: "session/active/changed";
      sessionId: string;
      workspacePath: string;
      engineEpoch: number;
    }
  | { type: "session/renamed"; requestId: string; sessionId: string; title: string }
  | {
      type: "session/archive/changed";
      requestId: string;
      sessionId: string;
      archived: boolean;
    }
  | {
      type: "session/operation/error";
      requestId: string;
      operation: "rename" | "archive";
      sessionId: string;
      message: string;
    }
  | {
      type: "session/forked";
      sessionId: string;
      workspacePath: string;
      parentSessionId: string;
      chatMessagesCopied: number;
      updatesCopied: number;
      planStateCopied: boolean;
      modelId?: string;
    }
  | { type: "session/compacted"; sessionId: string }
  | { type: "session/rewind/points"; sessionId: string; points: SessionRewindPoint[] }
  | { type: "session/rewind/points/error"; sessionId: string; message: string }
  | {
      type: "session/rewound";
      sessionId: string;
      success: boolean;
      targetPromptIndex: number;
      mode: SessionRewindMode;
      revertedFiles: string[];
      cleanFiles: string[];
      conflicts: SessionRewindConflict[];
      promptText?: string;
      error?: string;
    }
  | {
      type: "session/mode/changed";
      sessionId: string;
      mode: SessionMode;
      planAvailable: boolean;
    }
  | {
      type: "runtime/dashboard/changed";
      sessionId: string;
      backgroundTasks: BackgroundTaskSnapshot[];
      subagents: SubagentSnapshot[];
    }
  | {
      type: "runtime/task/killed";
      sessionId: string;
      taskId: string;
      outcome: BackgroundTaskKillOutcome;
    }
  | {
      type: "runtime/subagent/detail";
      sessionId: string;
      subagentId: string;
      snapshot?: SubagentSnapshot;
    }
  | {
      type: "runtime/subagent/cancelled";
      sessionId: string;
      subagentId: string;
      outcome: SubagentCancelOutcome;
      terminalStatus?: SubagentStatus;
    }
  | {
      type: "runtime/dashboard/error";
      sessionId: string;
      message: string;
      operation: RuntimeDashboardOperation;
      itemId?: string;
    }
  | {
      type: "runtime/commands/changed";
      workspaceGeneration: number;
      commands: RuntimeCommand[];
    }
  | {
      type: "runtime/commands/error";
      workspaceGeneration: number;
      message: string;
    }
  | {
      type: "worktree/created";
      workspaceGeneration: number;
      status: WorktreeCreateStatus;
      sessionId: string;
      worktreePath: string;
      sourceGitRoot?: string;
      commit?: string;
    }
  | {
      type: "worktree/list/changed";
      workspaceGeneration: number;
      worktrees: WorktreeRecord[];
    }
  | {
      type: "worktree/detail";
      workspaceGeneration: number;
      worktree?: WorktreeRecord;
    }
  | {
      type: "worktree/applied";
      workspaceGeneration: number;
      status: WorktreeApplyStatus;
      files: WorktreeFileChange[];
      conflicts: WorktreeConflict[];
      gitRoot?: string;
    }
  | {
      type: "worktree/removed";
      workspaceGeneration: number;
      idOrPath: string;
      removed: boolean;
      resolvedPath?: string;
    }
  | {
      type: "worktree/gc/completed";
      workspaceGeneration: number;
      deadRemoved: number;
      expiredRemoved: number;
      skippedAlive: number;
      removeFailed: number;
    }
  | {
      type: "worktree/error";
      workspaceGeneration: number;
      message: string;
      operation: WorktreeOperation;
      itemId?: string;
    }
  | {
      type: "runtime/memory/status";
      sessionId: string;
      status: "running" | "succeeded" | "error";
      message?: string;
    }
  | { type: "memory/capabilities"; sessionId: string; memory: MemoryCapabilities }
  | {
      type: "memory/listed";
      requestId: string;
      workspaceGeneration: number;
      sessionId: string;
      files: MemoryFile[];
      truncated: boolean;
    }
  | {
      type: "memory/document";
      requestId: string;
      workspaceGeneration: number;
      sessionId: string;
      file: MemoryFile;
      content: string;
    }
  | {
      type: "memory/mutation";
      requestId: string;
      workspaceGeneration: number;
      sessionId: string;
      operation: "write" | "delete";
      fileId: string;
      status: MemoryMutationStatus;
      message: string;
      file?: MemoryFile;
      confirmationToken?: string;
    }
  | {
      type: "memory/error";
      requestId: string;
      workspaceGeneration: number;
      sessionId: string;
      operation: MemoryOperation;
      fileId?: string;
      message: string;
    }
  | {
      type: "ui/preferences/changed";
      language: UiLanguage;
      composerDraft: string;
      sessionMode: SessionMode;
      executionProfile: ExecutionProfile;
      notificationsEnabled: boolean;
      windowsAutomationEnabled: boolean;
      backgroundUpdateChecksEnabled: boolean;
      restartRequired: boolean;
    }
  | {
      type: "session/exported";
      requestId: string;
      sessionId: string;
      fileName: string;
    }
  | {
      type: "session/imported";
      requestId: string;
      sessionId: string;
      workspacePath: string;
    }
  | {
      type: "backup/completed";
      requestId: string;
      operation: "create" | "restore";
      fileCount: number;
      totalBytes: number;
      restartRequired: boolean;
    }
  | {
      type: "update/status";
      requestId: string;
      status: UpdateStatus;
      version?: string;
    }
  | {
      type: "update/background-available";
      version: string;
    }
  | {
      type: "maintenance/error" | "maintenance/cancelled";
      requestId: string;
      operation: MaintenanceOperation;
    }
  | {
      type: "cloud/profile";
      requestId: string;
      localOnly: boolean;
      baseUri: string | null;
      teamId: string | null;
      deviceId: string | null;
      hasAccessToken: boolean;
    }
  | {
      type: "cloud/pairing/completed";
      requestId: string;
      operation: "export" | "import";
    }
  | {
      type: "cloud/session/uploaded";
      requestId: string;
      sessionId: string;
      revision: number;
    }
  | {
      type: "cloud/session/imported";
      requestId: string;
      remoteSessionId: string;
      found: boolean;
      revision?: number;
      importedSessionId?: string;
    }
  | {
      type: "cloud/session/deleted";
      requestId: string;
      remoteSessionId: string;
      found: boolean;
      revision?: number;
    }
  | {
      type: "cloud/session/exported";
      requestId: string;
      sessionId: string;
      fileName: string;
    }
  | {
      type: "cloud/notification";
      kind: "handoff-changed" | "job-changed";
      resourceId: string;
    }
  | {
      type: "cloud/notification";
      kind: "policy-changed";
      policyVersion: number;
    }
  | {
      type: "cloud/handoff/created";
      requestId: string;
      handoffId: string;
      sessionId: string;
      targetDeviceId: string;
    }
  | {
      type: "cloud/handoffs/received";
      requestId: string;
      imports: CloudHandoffImport[];
    }
  | {
      type: "cloud/policy";
      requestId: string;
      version: number;
      allowedExecutionProfiles: ExecutionProfile[];
      remoteRunnerEnabled: boolean;
      uiAutomationEnabled: boolean;
      maximumConcurrentJobs: number;
      allowedPluginPublishers: string[];
    }
  | {
      type: "cloud/runner/registered";
      requestId: string;
      runnerId: string;
      capabilities: string[];
    }
  | { type: "cloud/runner/queued"; requestId: string; jobId: string }
  | {
      type: "cloud/runner/claimed";
      requestId: string;
      found: boolean;
      claimHandle?: string;
      jobId?: string;
      requiredCapability?: string;
      task?: string;
      leaseExpiresAt?: string;
    }
  | {
      type: "cloud/runner/completed";
      requestId: string;
      claimHandle: string;
      jobId: string;
    }
  | { type: "cloud/automations"; requestId: string; automations: CloudAutomation[] }
  | {
      type: "cloud/automation/created";
      requestId: string;
      automation: CloudAutomation;
    }
  | {
      type: "cloud/automation/disabled";
      requestId: string;
      automationId: string;
      disabled: boolean;
    }
  | {
      type: "cloud/error" | "cloud/cancelled";
      requestId: string;
      operation: CloudOperation;
    }
  | ({
      type: "extensions/catalog";
      requestId: string;
      sessionId: string;
    } & ExtensionsCatalog)
  | {
      type: "extensions/action/completed";
      requestId: string;
      sessionId: string;
      scope: ExtensionScope;
      action: string;
      status: ExtensionActionStatus;
      message: string;
      requiresReload: boolean;
      requiresRestart: boolean;
    }
  | {
      type: "extensions/error";
      requestId: string;
      sessionId?: string;
      scope?: ExtensionScope;
      action?: string;
      message: string;
    }
  | {
      type: "windows/automation/completed";
      requestId: string;
      action: WindowsAutomationAction;
      processId: number;
      target: string;
    }
  | { type: "windows/automation/cancelled"; requestId: string }
  | {
      type: "windows/automation/error";
      requestId: string;
      reason: WindowsAutomationErrorReason;
    }
  | {
      type: "permission/requested";
      requestId: string;
      sessionId: string;
      toolCallId: string;
      title: string;
      toolKind?: string;
      rawInput?: unknown;
      options: PermissionOption[];
      locations: string[];
    };

export interface HostBridge {
  readonly available: boolean;
  send(command: HostCommand): void;
  subscribe(listener: (event: HostEvent) => void): () => void;
}

interface WebViewTransport {
  postMessage(message: unknown): void;
  addEventListener?(
    name: "message",
    listener: (event: MessageEvent<unknown>) => void
  ): void;
  removeEventListener?(
    name: "message",
    listener: (event: MessageEvent<unknown>) => void
  ): void;
}

export function createHostBridge(transport?: WebViewTransport): HostBridge {
  const listeners = new Set<(event: HostEvent) => void>();
  const pendingMaintenance = new Map<string, MaintenancePendingRequest>();
  const pendingCloud = new Map<string, CloudPendingRequest>();
  const pendingExtensions = new Map<string, ExtensionPendingRequest>();
  const pendingWindowsAutomation = new Map<string, WindowsAutomationPendingRequest>();
  const pendingWorkspaceContext = new Map<string, WorkspaceContextPendingRequest>();
  const pendingMemory = new Map<string, MemoryPendingRequest>();
  const pendingSessionLists = new Set<string>();
  const usedRequestIds = new Set<string>();
  const usedRequestIdOrder: string[] = [];
  const queuedCommands: Array<Record<string, unknown>> = [];
  let documentToken: string | undefined;
  let listening = false;

  const postCommand = (command: Record<string, unknown>) => {
    if (!transport || !documentToken) {
      return;
    }
    transport.postMessage({ ...command, documentToken });
  };
  const acceptDocumentToken = (message: MessageEvent<unknown>) => {
    const value = message.data;
    if (!isRecord(value) || value.schemaVersion !== hostSchemaVersion ||
        value.type !== "host/document-token" || !hasOnlyKeys(value, [
          "schemaVersion", "type", "documentToken"
        ]) || !isDocumentToken(value.documentToken)) {
      return false;
    }

    documentToken = value.documentToken;
    for (const command of queuedCommands.splice(0)) {
      postCommand(command);
    }
    return true;
  };

  const pendingRequestCount = () => pendingMaintenance.size + pendingCloud.size +
    pendingExtensions.size + pendingWindowsAutomation.size + pendingWorkspaceContext.size +
    pendingMemory.size + pendingSessionLists.size;
  const requestIsPending = (requestId: string) => pendingMaintenance.has(requestId) ||
    pendingCloud.has(requestId) || pendingExtensions.has(requestId) ||
    pendingWindowsAutomation.has(requestId) || pendingWorkspaceContext.has(requestId) ||
    pendingMemory.has(requestId) || pendingSessionLists.has(requestId);
  const rememberRequestId = (requestId: string) => {
    usedRequestIds.add(requestId);
    usedRequestIdOrder.push(requestId);
    let attempts = usedRequestIdOrder.length;
    while (usedRequestIds.size > maximumRememberedRequestIds && attempts > 0) {
      attempts -= 1;
      const candidate = usedRequestIdOrder.shift();
      if (!candidate) {
        break;
      }
      if (requestIsPending(candidate)) {
        usedRequestIdOrder.push(candidate);
      } else {
        usedRequestIds.delete(candidate);
      }
    }
  };

  const handler = (message: MessageEvent<unknown>) => {
    if (acceptDocumentToken(message)) {
      return;
    }
    const event = parseHostEvent(
      message.data,
      pendingMaintenance,
      pendingCloud,
      pendingExtensions,
      pendingWindowsAutomation,
      pendingWorkspaceContext,
      pendingMemory,
      pendingSessionLists
    );
    if (!event) {
      return;
    }
    if (event.type === "workspace/selected") {
      for (const [requestId, pendingRequest] of pendingWorkspaceContext) {
        if (pendingRequest.workspaceGeneration !== event.workspaceGeneration) {
          pendingWorkspaceContext.delete(requestId);
        }
      }
      for (const [requestId, pendingRequest] of pendingMemory) {
        if (pendingRequest.workspaceGeneration !== event.workspaceGeneration) {
          pendingMemory.delete(requestId);
        }
      }
    } else if (event.type === "session/active/changed") {
      for (const [requestId, pendingRequest] of pendingMemory) {
        if (pendingRequest.sessionId !== event.sessionId) {
          pendingMemory.delete(requestId);
        }
      }
    } else if (event.type === "engine/status" &&
        (event.status === "stopped" || event.status === "error")) {
      pendingMemory.clear();
    }
    for (const listener of listeners) {
      listener(event);
    }
  };

  transport?.addEventListener?.("message", acceptDocumentToken);

  return {
    available: transport !== undefined,
    send(command) {
      if (!transport || (!documentToken && queuedCommands.length >= maximumQueuedHostCommands)) {
        return;
      }
      const pending = maintenancePendingRequest(command);
      const cloudPending = cloudPendingRequest(command);
      const extensionPending = extensionPendingRequest(command);
      const windowsAutomationPending = windowsAutomationPendingRequest(command);
      const workspaceContextPending = workspaceContextPendingRequest(command);
      const memoryPending = memoryPendingRequest(command);
      if (command.type === "windows/automation/execute" && !windowsAutomationPending) {
        return;
      }
      if (command.type.startsWith("workspace/context/") && !workspaceContextPending) {
        return;
      }
      if (command.type.startsWith("memory/") && !memoryPending) {
        return;
      }
      if (command.type === "session/list" && !isMaintenanceRequestId(command.requestId)) {
        return;
      }
      if ((command.type === "session/rename" || command.type === "session/archive") &&
          !isMaintenanceRequestId(command.requestId)) {
        return;
      }
      const requestId = pending?.requestId ?? cloudPending?.requestId ??
        extensionPending?.requestId ?? windowsAutomationPending?.requestId ??
        workspaceContextPending?.requestId ?? memoryPending?.requestId ??
        (command.type === "session/list" || command.type === "session/rename" ||
          command.type === "session/archive" ? command.requestId : undefined);
      if (requestId) {
        if (!isMaintenanceRequestId(requestId) || usedRequestIds.has(requestId)) {
          return;
        }
        if (workspaceContextPending?.operation === "file-search") {
          for (const [pendingRequestId, pendingRequest] of pendingWorkspaceContext) {
            if (pendingRequest.operation === "file-search") {
              pendingWorkspaceContext.delete(pendingRequestId);
            }
          }
        }
        if (pendingRequestCount() >= maximumPendingHostRequests) {
          return;
        }
        rememberRequestId(requestId);
      }
      if (command.type === "session/list") {
        pendingSessionLists.add(command.requestId);
      }
      if (pending) {
        pendingMaintenance.set(pending.requestId, pending);
      }
      if (cloudPending) {
        pendingCloud.set(cloudPending.requestId, cloudPending);
      }
      if (extensionPending) {
        pendingExtensions.set(extensionPending.requestId, extensionPending);
      }
      if (windowsAutomationPending) {
        pendingWindowsAutomation.set(windowsAutomationPending.requestId, windowsAutomationPending);
      }
      if (workspaceContextPending) {
        pendingWorkspaceContext.set(workspaceContextPending.requestId, workspaceContextPending);
      }
      if (memoryPending) {
        pendingMemory.set(memoryPending.requestId, memoryPending);
      }
      const envelope = { schemaVersion: hostSchemaVersion, ...command };
      if (documentToken) {
        postCommand(envelope);
      } else {
        queuedCommands.push(envelope);
      }
    },
    subscribe(listener) {
      if (!transport?.addEventListener) {
        return () => undefined;
      }
      listeners.add(listener);
      if (!listening) {
        transport.addEventListener("message", handler);
        listening = true;
      }
      return () => {
        listeners.delete(listener);
        if (listening && listeners.size === 0) {
          transport.removeEventListener?.("message", handler);
          listening = false;
        }
      };
    }
  };
}

function isDocumentToken(value: unknown): value is string {
  return typeof value === "string" && documentTokenPattern.test(value);
}

type MaintenancePendingRequest = {
  requestId: string;
  operation: MaintenanceOperation;
  sessionId?: string;
};

type CloudPendingRequest = {
  requestId: string;
  operation: CloudOperation;
  sessionId?: string;
  remoteSessionId?: string;
  targetDeviceId?: string;
  runnerId?: string;
  claimHandle?: string;
  jobId?: string;
  automationName?: string;
  automationId?: string;
};

type ExtensionPendingRequest = {
  requestId: string;
  operation: "list" | "action";
  sessionId?: string;
  scope?: ExtensionScope;
  action?: string;
};

type WindowsAutomationPendingRequest = {
  requestId: string;
  action: WindowsAutomationAction;
  processId: number;
};

type WorkspaceContextPendingRequest = {
  requestId: string;
  operation: WorkspaceContextOperation;
  workspaceGeneration: number;
  relativePath?: string;
  query?: string;
};

type MemoryPendingRequest = {
  requestId: string;
  operation: MemoryOperation;
  workspaceGeneration: number;
  sessionId: string;
  fileId?: string;
  confirmed?: boolean;
};

function parseHostEvent(
  value: unknown,
  pendingMaintenance?: Map<string, MaintenancePendingRequest>,
  pendingCloud?: Map<string, CloudPendingRequest>,
  pendingExtensions?: Map<string, ExtensionPendingRequest>,
  pendingWindowsAutomation?: Map<string, WindowsAutomationPendingRequest>,
  pendingWorkspaceContext?: Map<string, WorkspaceContextPendingRequest>,
  pendingMemory?: Map<string, MemoryPendingRequest>,
  pendingSessionLists?: Set<string>
): HostEvent | null {
  if (!isRecord(value) || value.schemaVersion !== hostSchemaVersion || typeof value.type !== "string") {
    return null;
  }
  if (!hasOnlyKnownHostEventKeys(value)) {
    return null;
  }

  switch (value.type) {
    case "engine/status":
      if (!isEngineStatus(value.status) ||
          (Object.hasOwn(value, "engineEpoch") &&
            !isNonNegativeSafeInteger(value.engineEpoch))) {
        return null;
      }
      return {
        type: value.type,
        status: value.status,
        ...optionalString(value, "message"),
        ...optionalString(value, "sessionId"),
        ...(isNonNegativeSafeInteger(value.engineEpoch)
          ? { engineEpoch: value.engineEpoch }
          : {}),
        ...optionalEngineCapabilities(value.capabilities)
      };
    case "workspace/selected":
      return typeof value.path === "string" && isWorkspaceGeneration(value.workspaceGeneration)
        ? {
            type: value.type,
            path: value.path,
            workspaceGeneration: value.workspaceGeneration
          }
        : null;
    case "workspace/context/instructions/list":
    case "workspace/context/file/read":
    case "workspace/context/instructions/write":
    case "workspace/context/file/search":
    case "workspace/context/error":
      return pendingWorkspaceContext
        ? parseWorkspaceContextEvent(value, pendingWorkspaceContext)
        : null;
    case "engine/capabilities":
      return parseEngineCapabilitiesChanged(value);
    case "attachment/changed": {
      if (!isMaintenanceRequestId(value.requestId) || !Array.isArray(value.attachments) ||
          value.attachments.length > 4 ||
          (Object.hasOwn(value, "error") && !isImageAttachmentError(value.error)) ||
          (Object.hasOwn(value, "cancelled") && typeof value.cancelled !== "boolean")) {
        return null;
      }
      const attachments = parsePromptAttachmentReferences(value.attachments);
      if (!attachments) {
        return null;
      }
      return {
        type: value.type,
        requestId: value.requestId,
        attachments,
        ...(isImageAttachmentError(value.error) ? { error: value.error } : {}),
        ...(typeof value.cancelled === "boolean" ? { cancelled: value.cancelled } : {})
      };
    }
    case "memory/capabilities":
      return parseMemoryCapabilities(value);
    case "memory/listed":
    case "memory/document":
    case "memory/mutation":
    case "memory/error":
      return pendingMemory ? parseMemoryEvent(value, pendingMemory) : null;
    case "credential/status":
      return value.status === "saved" || value.status === "deleted" || value.status === "error"
        ? { type: value.type, status: value.status, ...optionalString(value, "message") }
        : null;
    case "provider/status":
      return parseProviderStatus(value);
    case "session/update":
      return typeof value.sessionId === "string" &&
        typeof value.updateKind === "string" &&
        isNonNegativeSafeInteger(value.engineEpoch)
        ? {
            type: value.type,
            sessionId: value.sessionId,
            updateKind: value.updateKind,
            engineEpoch: value.engineEpoch,
            ...optionalString(value, "text"),
            ...(Object.hasOwn(value, "update") ? { update: value.update } : {})
          }
        : null;
    case "prompt/completed":
      return typeof value.sessionId === "string" && typeof value.stopReason === "string"
        ? { type: value.type, sessionId: value.sessionId, stopReason: value.stopReason }
        : null;
    case "session/list/changed":
      return pendingSessionLists ? parseSessionListChanged(value, pendingSessionLists) : null;
    case "session/list/error":
      return pendingSessionLists ? parseSessionListError(value, pendingSessionLists) : null;
    case "session/active/changed":
      return isNonEmptyString(value.sessionId) &&
        isNonEmptyString(value.workspacePath) &&
        isNonNegativeSafeInteger(value.engineEpoch)
        ? {
            type: value.type,
            sessionId: value.sessionId,
            workspacePath: value.workspacePath,
            engineEpoch: value.engineEpoch
        }
        : null;
    case "session/renamed":
      return isMaintenanceRequestId(value.requestId) &&
        isNonEmptyString(value.sessionId) &&
        isBoundedNonEmptyString(value.title, 160)
        ? {
            type: value.type,
            requestId: value.requestId,
            sessionId: value.sessionId,
            title: value.title
          }
        : null;
    case "session/archive/changed":
      return isMaintenanceRequestId(value.requestId) &&
        isNonEmptyString(value.sessionId) &&
        typeof value.archived === "boolean"
        ? {
            type: value.type,
            requestId: value.requestId,
            sessionId: value.sessionId,
            archived: value.archived
          }
        : null;
    case "session/operation/error":
      return isMaintenanceRequestId(value.requestId) &&
        (value.operation === "rename" || value.operation === "archive") &&
        isNonEmptyString(value.sessionId) &&
        isBoundedNonEmptyString(value.message, 4096)
        ? {
            type: value.type,
            requestId: value.requestId,
            operation: value.operation,
            sessionId: value.sessionId,
            message: value.message
          }
        : null;
    case "session/forked":
      return parseSessionForked(value);
    case "session/compacted":
      return isNonEmptyString(value.sessionId)
        ? { type: value.type, sessionId: value.sessionId }
        : null;
    case "session/rewind/points":
      return parseSessionRewindPoints(value);
    case "session/rewind/points/error":
      return isNonEmptyString(value.sessionId) &&
        isBoundedNonEmptyString(value.message, 4096)
        ? { type: value.type, sessionId: value.sessionId, message: value.message }
        : null;
    case "session/rewound":
      return parseSessionRewound(value);
    case "session/mode/changed":
      return isNonEmptyString(value.sessionId) &&
        isSessionMode(value.mode) &&
        typeof value.planAvailable === "boolean"
        ? {
            type: value.type,
            sessionId: value.sessionId,
            mode: value.mode,
            planAvailable: value.planAvailable
          }
        : null;
    case "runtime/dashboard/changed":
      return parseRuntimeDashboardChanged(value);
    case "runtime/task/killed":
      return isNonEmptyString(value.sessionId) &&
        isNonEmptyString(value.taskId) &&
        isBackgroundTaskKillOutcome(value.outcome)
        ? {
            type: value.type,
            sessionId: value.sessionId,
            taskId: value.taskId,
            outcome: value.outcome
          }
        : null;
    case "runtime/subagent/detail":
      return parseRuntimeSubagentDetail(value);
    case "runtime/subagent/cancelled":
      return isNonEmptyString(value.sessionId) &&
        isNonEmptyString(value.subagentId) &&
        isSubagentCancelOutcome(value.outcome) &&
        (!Object.hasOwn(value, "terminalStatus") || isSubagentStatus(value.terminalStatus))
        ? {
            type: value.type,
            sessionId: value.sessionId,
            subagentId: value.subagentId,
            outcome: value.outcome,
            ...(isSubagentStatus(value.terminalStatus)
              ? { terminalStatus: value.terminalStatus }
              : {})
          }
        : null;
    case "runtime/dashboard/error":
      return isNonEmptyString(value.sessionId) &&
        isNonEmptyString(value.message) &&
        isRuntimeDashboardOperation(value.operation) &&
        (value.operation === "refresh" || isNonEmptyString(value.itemId))
        ? {
            type: value.type,
            sessionId: value.sessionId,
            message: value.message,
            operation: value.operation,
            ...(isNonEmptyString(value.itemId) ? { itemId: value.itemId } : {})
          }
        : null;
    case "runtime/commands/changed":
      return parseRuntimeCommandsChanged(value);
    case "runtime/commands/error":
      return isWorkspaceGeneration(value.workspaceGeneration) && isBoundedNonEmptyString(value.message, 4096)
        ? {
            type: value.type,
            workspaceGeneration: value.workspaceGeneration,
            message: value.message
          }
        : null;
    case "worktree/created":
      return parseWorktreeCreated(value);
    case "worktree/list/changed":
      return parseWorktreeListChanged(value);
    case "worktree/detail":
      return parseWorktreeDetail(value);
    case "worktree/applied":
      return parseWorktreeApplied(value);
    case "worktree/removed":
      return parseWorktreeRemoved(value);
    case "worktree/gc/completed":
      return parseWorktreeGcCompleted(value);
    case "worktree/error":
      return parseWorktreeError(value);
    case "runtime/memory/status":
      return isNonEmptyString(value.sessionId) &&
        isMemoryFlushStatus(value.status) &&
        isOptionalBoundedString(value, "message", 4096)
        ? {
            type: value.type,
            sessionId: value.sessionId,
            status: value.status,
            ...optionalString(value, "message")
          }
        : null;
    case "ui/preferences/changed":
      return parseUiPreferencesChanged(value);
    case "session/exported":
    case "session/imported":
    case "backup/completed":
    case "update/status":
    case "maintenance/error":
    case "maintenance/cancelled":
      return pendingMaintenance
        ? parseMaintenanceEvent(value, pendingMaintenance)
        : null;
    case "update/background-available":
      return isSemanticVersion(value.version)
        ? { type: value.type, version: value.version }
        : null;
    case "cloud/profile":
    case "cloud/pairing/completed":
    case "cloud/session/uploaded":
    case "cloud/session/imported":
    case "cloud/session/deleted":
    case "cloud/session/exported":
    case "cloud/handoff/created":
    case "cloud/handoffs/received":
    case "cloud/policy":
    case "cloud/runner/registered":
    case "cloud/runner/queued":
    case "cloud/runner/claimed":
    case "cloud/runner/completed":
    case "cloud/automations":
    case "cloud/automation/created":
    case "cloud/automation/disabled":
    case "cloud/error":
    case "cloud/cancelled":
      return pendingCloud ? parseCloudEvent(value, pendingCloud) : null;
    case "cloud/notification":
      return parseCloudNotification(value);
    case "extensions/catalog":
    case "extensions/action/completed":
    case "extensions/error":
      return pendingExtensions ? parseExtensionEvent(value, pendingExtensions) : null;
    case "windows/automation/completed":
    case "windows/automation/cancelled":
    case "windows/automation/error":
      return pendingWindowsAutomation
        ? parseWindowsAutomationEvent(value, pendingWindowsAutomation)
        : null;
    case "permission/requested":
      return parsePermissionRequest(value);
    default:
      return null;
  }
}

function maintenancePendingRequest(command: HostCommand): MaintenancePendingRequest | null {
  switch (command.type) {
    case "session/export":
      return {
        requestId: command.requestId,
        operation: "session-export",
        sessionId: command.sessionId
      };
    case "session/import":
      return { requestId: command.requestId, operation: "session-import" };
    case "backup/create":
      return { requestId: command.requestId, operation: "backup-create" };
    case "backup/restore":
      return { requestId: command.requestId, operation: "backup-restore" };
    case "update/check":
      return { requestId: command.requestId, operation: "update-check" };
    case "update/apply":
      return { requestId: command.requestId, operation: "update-apply" };
    default:
      return null;
  }
}

function cloudPendingRequest(command: HostCommand): CloudPendingRequest | null {
  switch (command.type) {
    case "cloud/profile/get":
      return { requestId: command.requestId, operation: "profile-get" };
    case "cloud/profile/save-local":
      return { requestId: command.requestId, operation: "profile-save-local" };
    case "cloud/profile/save-remote":
      return { requestId: command.requestId, operation: "profile-save-remote" };
    case "cloud/pairing/export":
      return { requestId: command.requestId, operation: "pairing-export" };
    case "cloud/pairing/import":
      return { requestId: command.requestId, operation: "pairing-import" };
    case "cloud/session/upload":
      return {
        requestId: command.requestId,
        operation: "session-upload",
        sessionId: command.sessionId
      };
    case "cloud/session/download":
      return {
        requestId: command.requestId,
        operation: "session-download",
        remoteSessionId: command.remoteSessionId
      };
    case "cloud/session/delete":
      return {
        requestId: command.requestId,
        operation: "session-delete",
        remoteSessionId: command.remoteSessionId
      };
    case "cloud/session/export":
      return {
        requestId: command.requestId,
        operation: "session-export",
        sessionId: command.sessionId
      };
    case "cloud/handoff/create":
      return {
        requestId: command.requestId,
        operation: "handoff-create",
        sessionId: command.sessionId,
        targetDeviceId: command.targetDeviceId
      };
    case "cloud/handoff/receive":
      return { requestId: command.requestId, operation: "handoff-receive" };
    case "cloud/policy/get":
      return { requestId: command.requestId, operation: "policy-get" };
    case "cloud/policy/update":
      return { requestId: command.requestId, operation: "policy-update" };
    case "cloud/runner/register":
      return {
        requestId: command.requestId,
        operation: "runner-register",
        runnerId: command.runnerId
      };
    case "cloud/runner/queue":
      return { requestId: command.requestId, operation: "runner-queue" };
    case "cloud/runner/claim":
      return {
        requestId: command.requestId,
        operation: "runner-claim",
        runnerId: command.runnerId
      };
    case "cloud/runner/complete":
      return {
        requestId: command.requestId,
        operation: "runner-complete",
        claimHandle: command.claimHandle,
        jobId: command.jobId
      };
    case "cloud/automation/create":
      return {
        requestId: command.requestId,
        operation: "automation-create",
        automationName: command.name
      };
    case "cloud/automation/list":
      return { requestId: command.requestId, operation: "automation-list" };
    case "cloud/automation/disable":
      return {
        requestId: command.requestId,
        operation: "automation-disable",
        automationId: command.automationId
      };
    default:
      return null;
  }
}

function extensionPendingRequest(command: HostCommand): ExtensionPendingRequest | null {
  switch (command.type) {
    case "extensions/list":
      return {
        requestId: command.requestId,
        operation: "list",
        sessionId: command.sessionId
      };
    case "extensions/action":
      return {
        requestId: command.requestId,
        operation: "action",
        sessionId: command.sessionId,
        scope: command.scope,
        action: command.action
      };
    default:
      return null;
  }
}

function windowsAutomationPendingRequest(
  command: HostCommand
): WindowsAutomationPendingRequest | null {
  if (command.type !== "windows/automation/execute" ||
      !isWindowsAutomationAction(command.action) ||
      !isWindowsProcessId(command.processId) ||
      !isOptionalCommandTarget(command.automationId) ||
      !isOptionalCommandTarget(command.name) ||
      !isOptionalAutomationValue(command.value)) {
    return null;
  }
  if (command.action !== "focus-window" &&
      !isBoundedNonEmptyString(command.automationId, 256) &&
      !isBoundedNonEmptyString(command.name, 256)) {
    return null;
  }
  return {
    requestId: command.requestId,
    action: command.action,
    processId: command.processId
  };
}

function parseWindowsAutomationEvent(
  value: Record<string, unknown>,
  pendingWindowsAutomation: Map<string, WindowsAutomationPendingRequest>
): HostEvent | null {
  if (!isMaintenanceRequestId(value.requestId)) {
    return null;
  }
  const pending = pendingWindowsAutomation.get(value.requestId);
  if (!pending) {
    return null;
  }

  let event: HostEvent | null = null;
  switch (value.type) {
    case "windows/automation/completed":
      event = value.action === pending.action &&
        value.processId === pending.processId &&
        isBoundedNonEmptyString(value.target, 256)
        ? {
            type: value.type,
            requestId: value.requestId,
            action: pending.action,
            processId: pending.processId,
            target: value.target
          }
        : null;
      break;
    case "windows/automation/cancelled":
      event = { type: value.type, requestId: value.requestId };
      break;
    case "windows/automation/error":
      event = isWindowsAutomationErrorReason(value.reason)
        ? { type: value.type, requestId: value.requestId, reason: value.reason }
        : null;
      break;
  }

  if (event) {
    pendingWindowsAutomation.delete(value.requestId);
  }
  return event;
}

function parseMaintenanceEvent(
  value: Record<string, unknown>,
  pendingMaintenance: Map<string, MaintenancePendingRequest>
): HostEvent | null {
  if (!isMaintenanceRequestId(value.requestId)) {
    return null;
  }
  const pending = pendingMaintenance.get(value.requestId);
  if (!pending) {
    return null;
  }

  let event: HostEvent | null = null;
  let terminal = true;
  switch (value.type) {
    case "session/exported":
      event = pending.operation === "session-export" &&
        value.sessionId === pending.sessionId &&
        isBoundedNonEmptyString(value.sessionId, 512) &&
        isSafeFileName(value.fileName)
        ? {
            type: value.type,
            requestId: value.requestId,
            sessionId: value.sessionId,
            fileName: value.fileName
          }
        : null;
      break;
    case "session/imported":
      event = pending.operation === "session-import" &&
        isBoundedNonEmptyString(value.sessionId, 512) &&
        isBoundedNonEmptyString(value.workspacePath, 32_767)
        ? {
            type: value.type,
            requestId: value.requestId,
            sessionId: value.sessionId,
            workspacePath: value.workspacePath
          }
        : null;
      break;
    case "backup/completed": {
      const expectedOperation = pending.operation === "backup-create"
        ? "create"
        : pending.operation === "backup-restore"
          ? "restore"
          : undefined;
      event = expectedOperation !== undefined && value.operation === expectedOperation &&
        isNonNegativeSafeInteger(value.fileCount) &&
        isNonNegativeSafeInteger(value.totalBytes) &&
        typeof value.restartRequired === "boolean"
        ? {
            type: value.type,
            requestId: value.requestId,
            operation: expectedOperation,
            fileCount: value.fileCount,
            totalBytes: value.totalBytes,
            restartRequired: value.restartRequired
          }
        : null;
      break;
    }
    case "update/status":
      if ((pending.operation !== "update-check" && pending.operation !== "update-apply") ||
          !isUpdateStatus(value.status) ||
          !isOptionalSemanticVersion(value, "version") ||
          !isValidUpdateStatusForOperation(value.status, pending.operation)) {
        break;
      }
      terminal = value.status !== "checking";
      event = {
        type: value.type,
        requestId: value.requestId,
        status: value.status,
        ...optionalString(value, "version")
      };
      break;
    case "maintenance/error":
    case "maintenance/cancelled":
      event = value.operation === pending.operation && isMaintenanceOperation(value.operation)
        ? {
            type: value.type,
            requestId: value.requestId,
            operation: value.operation
          }
        : null;
      break;
  }

  if (event && terminal) {
    pendingMaintenance.delete(value.requestId);
  }
  return event;
}

function parseCloudEvent(
  value: Record<string, unknown>,
  pendingCloud: Map<string, CloudPendingRequest>
): HostEvent | null {
  if (!isMaintenanceRequestId(value.requestId)) {
    return null;
  }
  const pending = pendingCloud.get(value.requestId);
  if (!pending) {
    return null;
  }

  let event: HostEvent | null = null;
  switch (value.type) {
    case "cloud/profile": {
      const expected = pending.operation === "profile-get" ||
        pending.operation === "profile-save-local" ||
        pending.operation === "profile-save-remote";
      const localOnly = value.localOnly;
      const baseUri = value.baseUri;
      const teamId = value.teamId;
      const deviceId = value.deviceId;
      if (!expected || typeof localOnly !== "boolean" ||
          typeof value.hasAccessToken !== "boolean" ||
          !isNullableBoundedString(baseUri, 2048) ||
          !isNullableCloudIdentifier(teamId) ||
          !isNullableCloudIdentifier(deviceId) ||
          (localOnly && (baseUri !== null || teamId !== null || deviceId !== null ||
            value.hasAccessToken)) ||
          (!localOnly && (!isCloudEndpoint(baseUri) || !isCloudIdentifier(teamId) ||
            !isCloudIdentifier(deviceId)))) {
        break;
      }
      event = {
        type: value.type,
        requestId: value.requestId,
        localOnly,
        baseUri,
        teamId,
        deviceId,
        hasAccessToken: value.hasAccessToken
      };
      break;
    }
    case "cloud/pairing/completed": {
      const operation = pending.operation === "pairing-export"
        ? "export"
        : pending.operation === "pairing-import"
          ? "import"
          : undefined;
      if (!operation || value.operation !== operation) {
        break;
      }
      event = { type: value.type, requestId: value.requestId, operation };
      break;
    }
    case "cloud/session/uploaded":
      if (pending.operation !== "session-upload" || value.sessionId !== pending.sessionId ||
          !isCloudIdentifier(value.sessionId) || !isPositiveSafeInteger(value.revision)) {
        break;
      }
      event = {
        type: value.type,
        requestId: value.requestId,
        sessionId: value.sessionId,
        revision: value.revision
      };
      break;
    case "cloud/session/imported": {
      if (pending.operation !== "session-download" ||
          value.remoteSessionId !== pending.remoteSessionId ||
          !isCloudIdentifier(value.remoteSessionId) || typeof value.found !== "boolean") {
        break;
      }
      if (value.found) {
        if (!isPositiveSafeInteger(value.revision) ||
            !isCloudIdentifier(value.importedSessionId)) {
          break;
        }
      } else if (Object.hasOwn(value, "revision") || Object.hasOwn(value, "importedSessionId")) {
        break;
      }
      event = {
        type: value.type,
        requestId: value.requestId,
        remoteSessionId: value.remoteSessionId,
        found: value.found,
        ...(value.found
          ? { revision: value.revision as number, importedSessionId: value.importedSessionId as string }
          : {})
      };
      break;
    }
    case "cloud/session/deleted": {
      if (pending.operation !== "session-delete" ||
          value.remoteSessionId !== pending.remoteSessionId ||
          !isCloudIdentifier(value.remoteSessionId) || typeof value.found !== "boolean") {
        break;
      }
      if (value.found) {
        if (!isPositiveSafeInteger(value.revision)) {
          break;
        }
      } else if (Object.hasOwn(value, "revision")) {
        break;
      }
      event = {
        type: value.type,
        requestId: value.requestId,
        remoteSessionId: value.remoteSessionId,
        found: value.found,
        ...(value.found ? { revision: value.revision as number } : {})
      };
      break;
    }
    case "cloud/session/exported":
      if (pending.operation !== "session-export" || value.sessionId !== pending.sessionId ||
          !isCloudIdentifier(value.sessionId) || !isSafeFileName(value.fileName)) {
        break;
      }
      event = {
        type: value.type,
        requestId: value.requestId,
        sessionId: value.sessionId,
        fileName: value.fileName
      };
      break;
    case "cloud/handoff/created":
      if (pending.operation !== "handoff-create" ||
          value.sessionId !== pending.sessionId ||
          value.targetDeviceId !== pending.targetDeviceId ||
          !isCloudIdentifier(value.handoffId) ||
          !isCloudIdentifier(value.sessionId) ||
          !isCloudIdentifier(value.targetDeviceId)) {
        break;
      }
      event = {
        type: value.type,
        requestId: value.requestId,
        handoffId: value.handoffId,
        sessionId: value.sessionId,
        targetDeviceId: value.targetDeviceId
      };
      break;
    case "cloud/handoffs/received": {
      if (pending.operation !== "handoff-receive" || !Array.isArray(value.imports) ||
          value.imports.length > 1000) {
        break;
      }
      const imports: CloudHandoffImport[] = [];
      const ids = new Set<string>();
      for (const candidate of value.imports) {
        if (!isRecord(candidate) ||
            !hasOnlyKeys(candidate, [
              "handoffId", "sourceDeviceId", "remoteSessionId", "importedSessionId"
            ]) ||
            !isCloudIdentifier(candidate.handoffId) ||
            !isCloudIdentifier(candidate.sourceDeviceId) ||
            !isCloudIdentifier(candidate.remoteSessionId) ||
            !isCloudIdentifier(candidate.importedSessionId) ||
            !ids.add(candidate.handoffId)) {
          return null;
        }
        imports.push({
          handoffId: candidate.handoffId,
          sourceDeviceId: candidate.sourceDeviceId,
          remoteSessionId: candidate.remoteSessionId,
          importedSessionId: candidate.importedSessionId
        });
      }
      event = { type: value.type, requestId: value.requestId, imports };
      break;
    }
    case "cloud/policy": {
      const expected = pending.operation === "policy-get" || pending.operation === "policy-update";
      if (!expected || !isPositiveSafeInteger(value.version) ||
          !Array.isArray(value.allowedExecutionProfiles) ||
          value.allowedExecutionProfiles.length > 2 ||
          !value.allowedExecutionProfiles.every(isExecutionProfile) ||
          new Set(value.allowedExecutionProfiles).size !== value.allowedExecutionProfiles.length ||
          typeof value.remoteRunnerEnabled !== "boolean" ||
          typeof value.uiAutomationEnabled !== "boolean" ||
          !isPositiveSafeInteger(value.maximumConcurrentJobs) ||
          value.maximumConcurrentJobs > 128 ||
          !Array.isArray(value.allowedPluginPublishers) ||
          value.allowedPluginPublishers.length > 256 ||
          !value.allowedPluginPublishers.every(isCloudIdentifier)) {
        break;
      }
      event = {
        type: value.type,
        requestId: value.requestId,
        version: value.version,
        allowedExecutionProfiles: [...value.allowedExecutionProfiles],
        remoteRunnerEnabled: value.remoteRunnerEnabled,
        uiAutomationEnabled: value.uiAutomationEnabled,
        maximumConcurrentJobs: value.maximumConcurrentJobs,
        allowedPluginPublishers: [...value.allowedPluginPublishers]
      };
      break;
    }
    case "cloud/runner/registered":
      if (pending.operation !== "runner-register" || value.runnerId !== pending.runnerId ||
          !isCloudIdentifier(value.runnerId) || !Array.isArray(value.capabilities) ||
          value.capabilities.length === 0 || value.capabilities.length > 64 ||
          !value.capabilities.every(isCloudIdentifier)) {
        break;
      }
      event = {
        type: value.type,
        requestId: value.requestId,
        runnerId: value.runnerId,
        capabilities: [...value.capabilities]
      };
      break;
    case "cloud/runner/queued":
      if (pending.operation !== "runner-queue" || !isCloudIdentifier(value.jobId)) {
        break;
      }
      event = {
        type: value.type,
        requestId: value.requestId,
        jobId: value.jobId
      };
      break;
    case "cloud/runner/claimed": {
      if (pending.operation !== "runner-claim" || typeof value.found !== "boolean") {
        break;
      }
      if (!value.found) {
        if ([value.claimHandle, value.jobId, value.requiredCapability, value.task,
          value.leaseExpiresAt]
          .some((candidate) => candidate !== null && candidate !== undefined)) {
          break;
        }
        event = { type: value.type, requestId: value.requestId, found: false };
        break;
      }
      if (!isCloudIdentifier(value.claimHandle) ||
          !isCloudIdentifier(value.jobId) ||
          !isCloudIdentifier(value.requiredCapability) ||
          !isBoundedNonEmptyString(value.task, 64 * 1024) ||
          !isIsoTimestamp(value.leaseExpiresAt)) {
        break;
      }
      event = {
        type: value.type,
        requestId: value.requestId,
        found: true,
        claimHandle: value.claimHandle,
        jobId: value.jobId,
        requiredCapability: value.requiredCapability,
        task: value.task,
        leaseExpiresAt: value.leaseExpiresAt
      };
      break;
    }
    case "cloud/runner/completed":
      if (pending.operation !== "runner-complete" || value.claimHandle !== pending.claimHandle ||
          value.jobId !== pending.jobId || !isCloudIdentifier(value.claimHandle) ||
          !isCloudIdentifier(value.jobId)) {
        break;
      }
      event = {
        type: value.type,
        requestId: value.requestId,
        claimHandle: value.claimHandle,
        jobId: value.jobId
      };
      break;
    case "cloud/automations": {
      if (pending.operation !== "automation-list" || !Array.isArray(value.automations) ||
          value.automations.length > 1000) {
        break;
      }
      const automations: CloudAutomation[] = [];
      const ids = new Set<string>();
      for (const candidate of value.automations) {
        const automation = parseCloudAutomation(candidate);
        if (!automation || !ids.add(automation.automationId)) {
          return null;
        }
        automations.push(automation);
      }
      event = { type: value.type, requestId: value.requestId, automations };
      break;
    }
    case "cloud/automation/created": {
      const automation = parseCloudAutomation(value.automation);
      if (pending.operation !== "automation-create" || !automation ||
          automation.name !== pending.automationName) {
        break;
      }
      event = {
        type: value.type,
        requestId: value.requestId,
        automation
      };
      break;
    }
    case "cloud/automation/disabled":
      if (pending.operation !== "automation-disable" ||
          value.automationId !== pending.automationId ||
          !isCloudIdentifier(value.automationId) || typeof value.disabled !== "boolean") {
        break;
      }
      event = {
        type: value.type,
        requestId: value.requestId,
        automationId: value.automationId,
        disabled: value.disabled
      };
      break;
    case "cloud/error":
    case "cloud/cancelled":
      if (value.operation !== pending.operation || !isCloudOperation(value.operation)) {
        break;
      }
      event = {
        type: value.type,
        requestId: value.requestId,
        operation: value.operation
      };
      break;
  }

  if (event) {
    pendingCloud.delete(value.requestId);
  }
  return event;
}

function parseCloudNotification(value: Record<string, unknown>): HostEvent | null {
  switch (value.kind) {
    case "handoff-changed":
    case "job-changed":
      return isCloudIdentifier(value.resourceId) && value.policyVersion === null
        ? { type: "cloud/notification", kind: value.kind, resourceId: value.resourceId }
        : null;
    case "policy-changed":
      return value.resourceId === null && isPositiveSafeInteger(value.policyVersion)
        ? { type: "cloud/notification", kind: value.kind, policyVersion: value.policyVersion }
        : null;
    default:
      return null;
  }
}

function parseCloudAutomation(value: unknown): CloudAutomation | null {
  if (!isRecord(value) ||
      !hasOnlyKeys(value, [
        "automationId", "name", "intervalSeconds", "enabled", "nextRunAt"
      ]) ||
      !isCloudIdentifier(value.automationId) ||
      !isBoundedNonEmptyString(value.name, 256) ||
      !isPositiveSafeInteger(value.intervalSeconds) ||
      typeof value.enabled !== "boolean" ||
      !isIsoTimestamp(value.nextRunAt)) {
    return null;
  }
  return {
    automationId: value.automationId,
    name: value.name,
    intervalSeconds: value.intervalSeconds,
    enabled: value.enabled,
    nextRunAt: value.nextRunAt
  };
}

function parseExtensionEvent(
  value: Record<string, unknown>,
  pendingExtensions: Map<string, ExtensionPendingRequest>
): HostEvent | null {
  if (!isMaintenanceRequestId(value.requestId)) {
    return null;
  }
  const pending = pendingExtensions.get(value.requestId);
  if (!pending) {
    return null;
  }

  let event: HostEvent | null = null;
  switch (value.type) {
    case "extensions/catalog": {
      if (pending.operation !== "list" || !isBoundedNonEmptyString(value.sessionId, 512) ||
          (pending.sessionId !== undefined && value.sessionId !== pending.sessionId)) {
        break;
      }
      const catalog = parseExtensionsCatalog(value);
      if (!catalog) {
        break;
      }
      event = {
        type: value.type,
        requestId: value.requestId,
        sessionId: value.sessionId,
        ...catalog
      };
      break;
    }
    case "extensions/action/completed":
      if (pending.operation !== "action" || value.sessionId !== pending.sessionId ||
          value.scope !== pending.scope || value.action !== pending.action ||
          !isBoundedNonEmptyString(value.sessionId, 512) ||
          !isExtensionScope(value.scope) || !isBoundedNonEmptyString(value.action, 128) ||
          !isExtensionActionStatus(value.status) ||
          !isBoundedString(value.message, 4096) ||
          typeof value.requiresReload !== "boolean" ||
          typeof value.requiresRestart !== "boolean") {
        break;
      }
      event = {
        type: value.type,
        requestId: value.requestId,
        sessionId: value.sessionId,
        scope: value.scope,
        action: value.action,
        status: value.status,
        message: value.message,
        requiresReload: value.requiresReload,
        requiresRestart: value.requiresRestart
      };
      break;
    case "extensions/error":
      if (!isBoundedNonEmptyString(value.message, 4096) ||
          (Object.hasOwn(value, "sessionId") && value.sessionId !== pending.sessionId) ||
          (Object.hasOwn(value, "scope") && value.scope !== pending.scope) ||
          (Object.hasOwn(value, "action") && value.action !== pending.action) ||
          !isOptionalBoundedString(value, "sessionId", 512) ||
          (Object.hasOwn(value, "scope") && !isExtensionScope(value.scope)) ||
          !isOptionalBoundedString(value, "action", 128)) {
        break;
      }
      event = {
        type: value.type,
        requestId: value.requestId,
        ...optionalString(value, "sessionId"),
        ...(isExtensionScope(value.scope) ? { scope: value.scope } : {}),
        ...optionalString(value, "action"),
        message: value.message
      };
      break;
  }

  if (event) {
    pendingExtensions.delete(value.requestId);
  }
  return event;
}

function parseExtensionsCatalog(value: Record<string, unknown>): ExtensionsCatalog | null {
  const mcp = parseExtensionMcpCatalog(value.mcp);
  const skills = parseExtensionSkillsCatalog(value.skills);
  const hooks = parseExtensionHooksCatalog(value.hooks);
  const plugins = parseExtensionPluginsCatalog(value.plugins);
  const marketplace = parseExtensionMarketplaceCatalog(value.marketplace);
  return mcp && skills && hooks && plugins && marketplace
    ? { mcp, skills, hooks, plugins, marketplace }
    : null;
}

function parseExtensionMcpCatalog(value: unknown): ExtensionsCatalog["mcp"] | null {
  if (!isRecord(value) || !hasOnlyKeys(value, ["servers"]) ||
      !Array.isArray(value.servers) || value.servers.length > 4096) {
    return null;
  }
  const servers: ExtensionMcpServer[] = [];
  const names = new Set<string>();
  for (const candidate of value.servers) {
    if (!isRecord(candidate) || !hasOnlyKeys(candidate, [
      "name", "displayName", "source", "sourceLabel", "transport", "url", "scope",
      "scopeId", "scopeName", "command", "arguments", "environmentVariableNames", "session"
    ]) || !isBoundedNonEmptyString(candidate.name, 256) ||
        (candidate.source !== "managed" && candidate.source !== "local") ||
        !isExtensionMcpTransport(candidate.transport) ||
        !isOptionalBoundedString(candidate, "displayName", 4096) ||
        !isOptionalBoundedString(candidate, "sourceLabel", 4096) ||
        !isOptionalBoundedString(candidate, "url", 2048) ||
        !isOptionalBoundedString(candidate, "scope", 256) ||
        !isOptionalBoundedString(candidate, "scopeId", 256) ||
        !isOptionalBoundedString(candidate, "scopeName", 4096) ||
        !isOptionalBoundedString(candidate, "command", 4096) ||
        !isBoundedStringArray(candidate.arguments, 256, 4096) ||
        !isEnvironmentNameArray(candidate.environmentVariableNames, 4096) ||
        !names.add(candidate.name)) {
      return null;
    }
    const session = Object.hasOwn(candidate, "session")
      ? parseExtensionMcpSession(candidate.session)
      : undefined;
    if (Object.hasOwn(candidate, "session") && !session) {
      return null;
    }
    servers.push({
      name: candidate.name,
      ...optionalString(candidate, "displayName"),
      source: candidate.source,
      ...optionalString(candidate, "sourceLabel"),
      transport: candidate.transport,
      ...optionalString(candidate, "url"),
      ...optionalString(candidate, "scope"),
      ...optionalString(candidate, "scopeId"),
      ...optionalString(candidate, "scopeName"),
      ...optionalString(candidate, "command"),
      arguments: [...candidate.arguments],
      environmentVariableNames: [...candidate.environmentVariableNames],
      ...(session ? { session } : {})
    });
  }
  return { servers };
}

function parseExtensionMcpSession(value: unknown): ExtensionMcpServer["session"] | null {
  if (!isRecord(value) || !hasOnlyKeys(value, ["enabled", "status", "tools", "authRequired"]) ||
      typeof value.enabled !== "boolean" || typeof value.authRequired !== "boolean" ||
      (Object.hasOwn(value, "status") && value.status !== "ready" &&
        value.status !== "initializing" && value.status !== "unavailable") ||
      !Array.isArray(value.tools) || value.tools.length > 4096) {
    return null;
  }
  const tools: NonNullable<ExtensionMcpServer["session"]>["tools"] = [];
  for (const candidate of value.tools) {
    if (!isRecord(candidate) ||
        !hasOnlyKeys(candidate, ["name", "displayName", "description", "enabled"]) ||
        !isBoundedNonEmptyString(candidate.name, 256) ||
        !isOptionalBoundedString(candidate, "displayName", 4096) ||
        !isOptionalBoundedString(candidate, "description", 4096) ||
        typeof candidate.enabled !== "boolean") {
      return null;
    }
    tools.push({
      name: candidate.name,
      ...optionalString(candidate, "displayName"),
      ...optionalString(candidate, "description"),
      enabled: candidate.enabled
    });
  }
  return {
    enabled: value.enabled,
    ...(typeof value.status === "string" ? { status: value.status as "ready" | "initializing" | "unavailable" } : {}),
    tools,
    authRequired: value.authRequired
  };
}

function parseExtensionSkillsCatalog(value: unknown): ExtensionsCatalog["skills"] | null {
  if (!isRecord(value) || !hasOnlyKeys(value, ["skills", "configuration"]) ||
      !Array.isArray(value.skills) || value.skills.length > 4096) {
    return null;
  }
  const skills: ExtensionSkill[] = [];
  for (const candidate of value.skills) {
    if (!isRecord(candidate) || !hasOnlyKeys(candidate, [
      "name", "displayName", "description", "paths", "path", "scope", "pluginName",
      "pluginVersion", "allowedTools", "model", "effort", "userInvocable",
      "disableModelInvocation", "enabled"
    ]) || !isBoundedNonEmptyString(candidate.name, 256) ||
        !isBoundedString(candidate.description, 4096) ||
        !isBoundedStringArray(candidate.paths, 256, 32767) ||
        !isBoundedNonEmptyString(candidate.path, 32767) ||
        !isExtensionSkillScope(candidate.scope) ||
        !isOptionalBoundedString(candidate, "displayName", 4096) ||
        !isOptionalBoundedString(candidate, "pluginName", 256) ||
        !isOptionalBoundedString(candidate, "pluginVersion", 256) ||
        !isBoundedStringArray(candidate.allowedTools, 4096, 256) ||
        !isOptionalBoundedString(candidate, "model", 256) ||
        !isOptionalBoundedString(candidate, "effort", 256) ||
        typeof candidate.userInvocable !== "boolean" ||
        typeof candidate.disableModelInvocation !== "boolean" ||
        typeof candidate.enabled !== "boolean") {
      return null;
    }
    skills.push({
      name: candidate.name,
      ...optionalString(candidate, "displayName"),
      description: candidate.description,
      paths: [...candidate.paths],
      path: candidate.path,
      scope: candidate.scope,
      ...optionalString(candidate, "pluginName"),
      ...optionalString(candidate, "pluginVersion"),
      allowedTools: [...candidate.allowedTools],
      ...optionalString(candidate, "model"),
      ...optionalString(candidate, "effort"),
      userInvocable: candidate.userInvocable,
      disableModelInvocation: candidate.disableModelInvocation,
      enabled: candidate.enabled
    });
  }
  let configuration: ExtensionsCatalog["skills"]["configuration"];
  if (Object.hasOwn(value, "configuration")) {
    const candidate = value.configuration;
    if (!isRecord(candidate) || !hasOnlyKeys(candidate, ["paths", "ignoredPaths", "totalSkills"]) ||
        !isBoundedStringArray(candidate.paths, 256, 32767) ||
        !isBoundedStringArray(candidate.ignoredPaths, 256, 32767) ||
        !isNonNegativeSafeInteger(candidate.totalSkills)) {
      return null;
    }
    configuration = {
      paths: [...candidate.paths],
      ignoredPaths: [...candidate.ignoredPaths],
      totalSkills: candidate.totalSkills
    };
  }
  return { skills, ...(configuration ? { configuration } : {}) };
}

function parseExtensionHooksCatalog(value: unknown): ExtensionsCatalog["hooks"] | null {
  if (!isRecord(value) || !hasOnlyKeys(value, ["hooks", "projectTrusted", "loadErrorCount"]) ||
      !Array.isArray(value.hooks) || value.hooks.length > 4096 ||
      typeof value.projectTrusted !== "boolean" ||
      !isNonNegativeSafeInteger(value.loadErrorCount)) {
    return null;
  }
  const hooks: ExtensionHook[] = [];
  for (const candidate of value.hooks) {
    if (!isRecord(candidate) || !hasOnlyKeys(candidate, [
      "name", "event", "handlerType", "matcher", "hasCommand", "hasUrl", "timeoutMs",
      "sourceDirectory", "disabled"
    ]) || !isBoundedNonEmptyString(candidate.name, 256) ||
        !isBoundedNonEmptyString(candidate.event, 128) ||
        (candidate.handlerType !== "command" && candidate.handlerType !== "http") ||
        !isOptionalBoundedString(candidate, "matcher", 4096) ||
        typeof candidate.hasCommand !== "boolean" || typeof candidate.hasUrl !== "boolean" ||
        !isNonNegativeSafeInteger(candidate.timeoutMs) ||
        !isBoundedNonEmptyString(candidate.sourceDirectory, 32767) ||
        typeof candidate.disabled !== "boolean") {
      return null;
    }
    hooks.push({
      name: candidate.name,
      event: candidate.event,
      handlerType: candidate.handlerType,
      ...optionalString(candidate, "matcher"),
      hasCommand: candidate.hasCommand,
      hasUrl: candidate.hasUrl,
      timeoutMs: candidate.timeoutMs,
      sourceDirectory: candidate.sourceDirectory,
      disabled: candidate.disabled
    });
  }
  return { hooks, projectTrusted: value.projectTrusted, loadErrorCount: value.loadErrorCount };
}

function parseExtensionPluginsCatalog(value: unknown): ExtensionsCatalog["plugins"] | null {
  if (!isRecord(value) || !hasOnlyKeys(value, ["plugins"]) ||
      !Array.isArray(value.plugins) || value.plugins.length > 4096) {
    return null;
  }
  const plugins: ExtensionPlugin[] = [];
  for (const candidate of value.plugins) {
    if (!isRecord(candidate) || !hasOnlyKeys(candidate, [
      "name", "id", "root", "scope", "trusted", "enabled", "version", "description",
      "skillCount", "skillNames", "agentCount", "agentNames", "hookStatus", "hookCount",
      "mcpServerCount", "mcpStatus", "marketplaceSource", "origin", "conflict"
    ]) || !isBoundedNonEmptyString(candidate.name, 256) ||
        !isBoundedNonEmptyString(candidate.id, 256) ||
        !isBoundedNonEmptyString(candidate.root, 32767) ||
        !isExtensionPluginScope(candidate.scope) ||
        typeof candidate.trusted !== "boolean" || typeof candidate.enabled !== "boolean" ||
        !isOptionalBoundedString(candidate, "version", 256) ||
        !isOptionalBoundedString(candidate, "description", 4096) ||
        !isNonNegativeSafeInteger(candidate.skillCount) ||
        !isBoundedStringArray(candidate.skillNames, 4096, 256) ||
        !isNonNegativeSafeInteger(candidate.agentCount) ||
        !isBoundedStringArray(candidate.agentNames, 4096, 256) ||
        !isExtensionPluginStatus(candidate.hookStatus) ||
        !isNonNegativeSafeInteger(candidate.hookCount) ||
        !isNonNegativeSafeInteger(candidate.mcpServerCount) ||
        !isExtensionPluginStatus(candidate.mcpStatus) ||
        !isOptionalBoundedString(candidate, "marketplaceSource", 32767) ||
        !isOptionalBoundedString(candidate, "conflict", 4096)) {
      return null;
    }
    const origin = Object.hasOwn(candidate, "origin")
      ? parseExtensionPluginOrigin(candidate.origin)
      : undefined;
    if (Object.hasOwn(candidate, "origin") && !origin) {
      return null;
    }
    plugins.push({
      name: candidate.name,
      id: candidate.id,
      root: candidate.root,
      scope: candidate.scope,
      trusted: candidate.trusted,
      enabled: candidate.enabled,
      ...optionalString(candidate, "version"),
      ...optionalString(candidate, "description"),
      skillCount: candidate.skillCount,
      skillNames: [...candidate.skillNames],
      agentCount: candidate.agentCount,
      agentNames: [...candidate.agentNames],
      hookStatus: candidate.hookStatus,
      hookCount: candidate.hookCount,
      mcpServerCount: candidate.mcpServerCount,
      mcpStatus: candidate.mcpStatus,
      ...optionalString(candidate, "marketplaceSource"),
      ...(origin ? { origin } : {}),
      ...optionalString(candidate, "conflict")
    });
  }
  return { plugins };
}

function parseExtensionPluginOrigin(value: unknown): ExtensionPlugin["origin"] | null {
  if (!isRecord(value) || !hasOnlyKeys(value, ["type", "marketplace", "sourceName"]) ||
      !isBoundedNonEmptyString(value.type, 128) ||
      !isOptionalBoundedString(value, "marketplace", 256) ||
      !isOptionalBoundedString(value, "sourceName", 256)) {
    return null;
  }
  return {
    type: value.type,
    ...optionalString(value, "marketplace"),
    ...optionalString(value, "sourceName")
  };
}

function parseExtensionMarketplaceCatalog(
  value: unknown
): ExtensionsCatalog["marketplace"] | null {
  if (!isRecord(value) || !hasOnlyKeys(value, ["sources"]) ||
      !Array.isArray(value.sources) || value.sources.length > 1024) {
    return null;
  }
  const sources: ExtensionMarketplaceSource[] = [];
  for (const candidate of value.sources) {
    if (!isRecord(candidate) || !hasOnlyKeys(candidate, ["name", "kind", "source", "plugins"]) ||
        !isBoundedNonEmptyString(candidate.name, 256) ||
        (candidate.kind !== "git" && candidate.kind !== "local" && candidate.kind !== "failed") ||
        !isBoundedNonEmptyString(candidate.source, 32767) ||
        !Array.isArray(candidate.plugins) || candidate.plugins.length > 4096) {
      return null;
    }
    const plugins: ExtensionMarketplaceSource["plugins"] = [];
    for (const plugin of candidate.plugins) {
      if (!isRecord(plugin) || !hasOnlyKeys(plugin, [
        "name", "source", "version", "description", "category", "author", "tags", "keywords",
        "domains", "homepage", "relativePath", "skillCount", "hasHooks", "hasAgents",
        "hasMcp", "installStatus", "installedVersion"
      ]) || !isBoundedNonEmptyString(plugin.name, 256) ||
          !isBoundedNonEmptyString(plugin.source, 32767) ||
          !isOptionalBoundedString(plugin, "version", 256) ||
          !isOptionalBoundedString(plugin, "description", 4096) ||
          !isOptionalBoundedString(plugin, "category", 256) ||
          !isOptionalBoundedString(plugin, "author", 256) ||
          !isBoundedStringArray(plugin.tags, 256, 256) ||
          !isBoundedStringArray(plugin.keywords, 256, 256) ||
          !isBoundedStringArray(plugin.domains, 256, 256) ||
          !isOptionalBoundedString(plugin, "homepage", 2048) ||
          !isBoundedNonEmptyString(plugin.relativePath, 32767) ||
          !isNonNegativeSafeInteger(plugin.skillCount) ||
          typeof plugin.hasHooks !== "boolean" || typeof plugin.hasAgents !== "boolean" ||
          typeof plugin.hasMcp !== "boolean" ||
          (plugin.installStatus !== "not_installed" && plugin.installStatus !== "installed" &&
            plugin.installStatus !== "update_available") ||
          !isOptionalBoundedString(plugin, "installedVersion", 256)) {
        return null;
      }
      plugins.push({
        name: plugin.name,
        source: plugin.source,
        ...optionalString(plugin, "version"),
        ...optionalString(plugin, "description"),
        ...optionalString(plugin, "category"),
        ...optionalString(plugin, "author"),
        tags: [...plugin.tags],
        keywords: [...plugin.keywords],
        domains: [...plugin.domains],
        ...optionalString(plugin, "homepage"),
        relativePath: plugin.relativePath,
        skillCount: plugin.skillCount,
        hasHooks: plugin.hasHooks,
        hasAgents: plugin.hasAgents,
        hasMcp: plugin.hasMcp,
        installStatus: plugin.installStatus,
        ...optionalString(plugin, "installedVersion")
      });
    }
    sources.push({
      name: candidate.name,
      kind: candidate.kind,
      source: candidate.source,
      plugins
    });
  }
  return { sources };
}

function isMaintenanceRequestId(value: unknown): value is string {
  return typeof value === "string" &&
    /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/u.test(value);
}

function memoryPendingRequest(command: HostCommand): MemoryPendingRequest | null | undefined {
  switch (command.type) {
    case "memory/list":
      return isMemoryRequest(command)
        ? {
            requestId: command.requestId,
            operation: "list",
            workspaceGeneration: command.workspaceGeneration,
            sessionId: command.sessionId
          }
        : null;
    case "memory/read":
      return isMemoryRequest(command) && isMemoryFileId(command.fileId)
        ? {
            requestId: command.requestId,
            operation: "read",
            workspaceGeneration: command.workspaceGeneration,
            sessionId: command.sessionId,
            fileId: command.fileId
          }
        : null;
    case "memory/write":
      return isMemoryRequest(command) && isMemoryFileId(command.fileId) &&
        isMemoryContent(command.content) && typeof command.confirmed === "boolean" &&
        (command.confirmed
          ? isMemoryConfirmationToken(command.confirmationToken)
          : command.confirmationToken === undefined)
        ? {
            requestId: command.requestId,
            operation: "write",
            workspaceGeneration: command.workspaceGeneration,
            sessionId: command.sessionId,
            fileId: command.fileId,
            confirmed: command.confirmed
          }
        : null;
    case "memory/delete":
      return isMemoryRequest(command) && isMemoryFileId(command.fileId) &&
        typeof command.confirmed === "boolean" &&
        (command.confirmed
          ? isMemoryConfirmationToken(command.confirmationToken)
          : command.confirmationToken === undefined)
        ? {
            requestId: command.requestId,
            operation: "delete",
            workspaceGeneration: command.workspaceGeneration,
            sessionId: command.sessionId,
            fileId: command.fileId,
            confirmed: command.confirmed
          }
        : null;
    default:
      return undefined;
  }
}

function isMemoryRequest(command: {
  requestId: string;
  workspaceGeneration: number;
  sessionId: string;
}): boolean {
  return isMaintenanceRequestId(command.requestId) &&
    isWorkspaceGeneration(command.workspaceGeneration) && isMemorySessionId(command.sessionId);
}

function workspaceContextPendingRequest(
  command: HostCommand
): WorkspaceContextPendingRequest | null | undefined {
  switch (command.type) {
    case "workspace/context/instructions/list":
      return isWorkspaceContextRequest(command)
        ? {
            requestId: command.requestId,
            operation: "instructions-list",
            workspaceGeneration: command.workspaceGeneration
          }
        : null;
    case "workspace/context/file/read":
      return isWorkspaceContextRequest(command) &&
        isWorkspaceInstructionPath(command.relativePath)
        ? {
            requestId: command.requestId,
            operation: "file-read",
            workspaceGeneration: command.workspaceGeneration,
            relativePath: command.relativePath
          }
        : null;
    case "workspace/context/instructions/write":
      return isWorkspaceContextRequest(command) &&
        isWorkspaceInstructionPath(command.relativePath) &&
        isWorkspaceContextContent(command.content)
        ? {
            requestId: command.requestId,
            operation: "instructions-write",
            workspaceGeneration: command.workspaceGeneration,
            relativePath: command.relativePath
          }
        : null;
    case "workspace/context/file/search":
      return isWorkspaceContextRequest(command) &&
        isWorkspaceContextQuery(command.query)
        ? {
            requestId: command.requestId,
            operation: "file-search",
            workspaceGeneration: command.workspaceGeneration,
            query: command.query
          }
        : null;
    default:
      return undefined;
  }
}

function isWorkspaceContextRequest(command: {
  requestId: string;
  workspaceGeneration: number;
}): boolean {
  return isMaintenanceRequestId(command.requestId) &&
    isWorkspaceGeneration(command.workspaceGeneration);
}

function parseWorkspaceContextEvent(
  value: Record<string, unknown>,
  pendingRequests: Map<string, WorkspaceContextPendingRequest>
): HostEvent | null {
  if (!isMaintenanceRequestId(value.requestId) ||
      !isWorkspaceGeneration(value.workspaceGeneration)) {
    return null;
  }
  const pending = pendingRequests.get(value.requestId);
  if (!pending || value.workspaceGeneration !== pending.workspaceGeneration) {
    return null;
  }

  let event: HostEvent | null = null;
  switch (value.type) {
    case "workspace/context/instructions/list": {
      const files = pending.operation === "instructions-list"
        ? parseWorkspaceContextFiles(value.files)
        : null;
      if (files) {
        event = {
          type: value.type,
          requestId: value.requestId,
          workspaceGeneration: value.workspaceGeneration,
          files
        };
      }
      break;
    }
    case "workspace/context/file/read":
      if (pending.operation === "file-read" &&
          value.relativePath === pending.relativePath &&
          isWorkspaceRelativePath(value.relativePath) &&
          isWorkspaceContextContent(value.content)) {
        event = {
          type: value.type,
          requestId: value.requestId,
          workspaceGeneration: value.workspaceGeneration,
          relativePath: value.relativePath,
          content: value.content
        };
      }
      break;
    case "workspace/context/instructions/write":
      if (pending.operation === "instructions-write" &&
          value.relativePath === pending.relativePath &&
          isWorkspaceInstructionPath(value.relativePath)) {
        event = {
          type: value.type,
          requestId: value.requestId,
          workspaceGeneration: value.workspaceGeneration,
          relativePath: value.relativePath
        };
      }
      break;
    case "workspace/context/file/search": {
      const files = pending.operation === "file-search" && value.query === pending.query
        ? parseWorkspaceContextFiles(value.files)
        : null;
      if (files && isWorkspaceContextQuery(value.query)) {
        event = {
          type: value.type,
          requestId: value.requestId,
          workspaceGeneration: value.workspaceGeneration,
          query: value.query,
          files
        };
      }
      break;
    }
    case "workspace/context/error":
      if (value.operation === pending.operation &&
          isWorkspaceContextOperation(value.operation)) {
        event = {
          type: value.type,
          requestId: value.requestId,
          workspaceGeneration: value.workspaceGeneration,
          operation: value.operation
        };
      }
      break;
  }

  if (event) {
    pendingRequests.delete(value.requestId);
  }
  return event;
}

function parseMemoryCapabilities(value: Record<string, unknown>): HostEvent | null {
  if (!isMemorySessionId(value.sessionId) || !isRecord(value.memory) ||
      !hasOnlyKeys(value.memory, [
        "schemaVersion", "list", "read", "write", "delete", "mutationConfirmationRequired"
      ])) {
    return null;
  }
  const memory = value.memory;
  if ((memory.schemaVersion !== 0 && memory.schemaVersion !== 1) ||
      typeof memory.list !== "boolean" || typeof memory.read !== "boolean" ||
      typeof memory.write !== "boolean" || typeof memory.delete !== "boolean" ||
      typeof memory.mutationConfirmationRequired !== "boolean" ||
      (memory.schemaVersion === 0 && (memory.list || memory.read || memory.write || memory.delete ||
        memory.mutationConfirmationRequired)) ||
      (memory.mutationConfirmationRequired && !memory.write && !memory.delete)) {
    return null;
  }
  return {
    type: "memory/capabilities",
    sessionId: value.sessionId,
    memory: {
      schemaVersion: memory.schemaVersion,
      list: memory.list,
      read: memory.read,
      write: memory.write,
      delete: memory.delete,
      mutationConfirmationRequired: memory.mutationConfirmationRequired
    }
  };
}

function parseMemoryEvent(
  value: Record<string, unknown>,
  pendingRequests: Map<string, MemoryPendingRequest>
): HostEvent | null {
  if (!isMaintenanceRequestId(value.requestId) ||
      !isWorkspaceGeneration(value.workspaceGeneration) || !isMemorySessionId(value.sessionId)) {
    return null;
  }
  const pending = pendingRequests.get(value.requestId);
  if (!pending || pending.workspaceGeneration !== value.workspaceGeneration ||
      pending.sessionId !== value.sessionId) {
    return null;
  }

  let event: HostEvent | null = null;
  switch (value.type) {
    case "memory/listed": {
      const files = pending.operation === "list" ? parseMemoryFiles(value.files) : null;
      if (files && typeof value.truncated === "boolean") {
        event = {
          type: value.type,
          requestId: value.requestId,
          workspaceGeneration: value.workspaceGeneration,
          sessionId: value.sessionId,
          files,
          truncated: value.truncated
        };
      }
      break;
    }
    case "memory/document": {
      const file = pending.operation === "read" ? parseMemoryFile(value.file) : null;
      if (file && file.id === pending.fileId && isMemoryContent(value.content) &&
          file.byteLength === new TextEncoder().encode(value.content).byteLength) {
        event = {
          type: value.type,
          requestId: value.requestId,
          workspaceGeneration: value.workspaceGeneration,
          sessionId: value.sessionId,
          file,
          content: value.content
        };
      }
      break;
    }
    case "memory/mutation": {
      const mutationOperation = value.operation === "write" || value.operation === "delete"
        ? value.operation
        : null;
      const status = isMemoryMutationStatus(value.status) ? value.status : null;
      const file = Object.hasOwn(value, "file") ? parseMemoryFile(value.file) : undefined;
      const confirmationToken = Object.hasOwn(value, "confirmationToken")
        ? value.confirmationToken
        : undefined;
      const confirmationShapeValid = status === "confirmation_required"
        ? pending.confirmed === false && isMemoryConfirmationToken(confirmationToken)
        : confirmationToken === undefined &&
          (status !== "success" || pending.confirmed === true);
      if ((pending.operation === "write" || pending.operation === "delete") &&
          mutationOperation === pending.operation && value.fileId === pending.fileId &&
          isMemoryFileId(value.fileId) && status && isMemoryMessage(value.message) &&
          confirmationShapeValid &&
          (file === undefined || (status === "success" && file?.id === pending.fileId))) {
        event = {
          type: value.type,
          requestId: value.requestId,
          workspaceGeneration: value.workspaceGeneration,
          sessionId: value.sessionId,
          operation: mutationOperation,
          fileId: value.fileId,
          status,
          message: value.message,
          ...(file ? { file } : {}),
          ...(typeof confirmationToken === "string" ? { confirmationToken } : {})
        };
      }
      break;
    }
    case "memory/error": {
      const operation = isMemoryOperation(value.operation) ? value.operation : null;
      const fileId = Object.hasOwn(value, "fileId") && isMemoryFileId(value.fileId)
        ? value.fileId
        : undefined;
      const targetMatches = pending.operation === "list"
        ? !Object.hasOwn(value, "fileId")
        : fileId === pending.fileId;
      if (operation === pending.operation && targetMatches && isMemoryMessage(value.message)) {
        event = {
          type: value.type,
          requestId: value.requestId,
          workspaceGeneration: value.workspaceGeneration,
          sessionId: value.sessionId,
          operation,
          ...(fileId ? { fileId } : {}),
          message: value.message
        };
      }
      break;
    }
  }

  if (event) {
    pendingRequests.delete(value.requestId);
  }
  return event;
}

function parseMemoryFiles(value: unknown): MemoryFile[] | null {
  if (!Array.isArray(value) || value.length > maximumMemoryFiles) {
    return null;
  }
  const files: MemoryFile[] = [];
  const identifiers = new Set<string>();
  for (const candidate of value) {
    const file = parseMemoryFile(candidate);
    if (!file || identifiers.has(file.id)) {
      return null;
    }
    identifiers.add(file.id);
    files.push(file);
  }
  return files;
}

function parseMemoryFile(value: unknown): MemoryFile | null {
  if (!isRecord(value) || !hasOnlyKeys(value, [
    "id", "scope", "name", "byteLength", "modifiedAt", "writable"
  ]) || !isMemoryFileId(value.id) || !isMemoryFileScope(value.scope) ||
      !isBoundedNonEmptyString(value.name, 255) ||
      !isNonNegativeSafeInteger(value.byteLength) || value.byteLength > maximumMemoryContentBytes ||
      typeof value.writable !== "boolean" ||
      (Object.hasOwn(value, "modifiedAt") && !isIsoTimestamp(value.modifiedAt))) {
    return null;
  }
  const expectedScope: MemoryFileScope = value.id === "global"
    ? "global"
    : value.id === "workspace"
      ? "workspace"
      : "session";
  const expectedName = expectedScope === "session" ? value.id.slice("session/".length) : "MEMORY.md";
  if (value.scope !== expectedScope || value.name !== expectedName) {
    return null;
  }
  return {
    id: value.id,
    scope: value.scope,
    name: value.name,
    byteLength: value.byteLength,
    ...(typeof value.modifiedAt === "string" ? { modifiedAt: value.modifiedAt } : {}),
    writable: value.writable
  };
}

function parseWorkspaceContextFiles(value: unknown): WorkspaceContextFile[] | null {
  if (!Array.isArray(value) || value.length > 100) {
    return null;
  }
  const files: WorkspaceContextFile[] = [];
  const paths = new Set<string>();
  for (const candidate of value) {
    if (!isRecord(candidate) ||
        !hasOnlyKeys(candidate, ["relativePath", "byteLength", "lastWriteTime"]) ||
        !isWorkspaceRelativePath(candidate.relativePath) ||
        !isNonNegativeSafeInteger(candidate.byteLength) ||
        !isIsoTimestamp(candidate.lastWriteTime) ||
        paths.has(candidate.relativePath)) {
      return null;
    }
    paths.add(candidate.relativePath);
    files.push({
      relativePath: candidate.relativePath,
      byteLength: candidate.byteLength,
      lastWriteTime: candidate.lastWriteTime
    });
  }
  return files;
}

function isWorkspaceContextOperation(value: unknown): value is WorkspaceContextOperation {
  return value === "instructions-list" || value === "file-read" ||
    value === "instructions-write" || value === "file-search";
}

function isWorkspaceRelativePath(value: unknown): value is string {
  if (!isBoundedNonEmptyString(value, 32_767) ||
      value.startsWith("/") || value.endsWith("/") ||
      value.includes("\\") || value.includes(":") ||
      /[\u0000-\u001f\u007f]/u.test(value)) {
    return false;
  }
  return value.split("/").every((segment) =>
    segment.length > 0 && segment !== "." && segment !== "..");
}

function isWorkspaceInstructionPath(value: unknown): value is string {
  return isWorkspaceRelativePath(value) && value.split("/").at(-1) === "AGENTS.md";
}

function isWorkspaceContextContent(value: unknown): value is string {
  return typeof value === "string" && new TextEncoder().encode(value).byteLength <= 512 * 1024 &&
    !/[\u0000\u000b\u000c\u000e-\u001f\u007f]/u.test(value);
}

function isWorkspaceContextQuery(value: unknown): value is string {
  return isBoundedNonEmptyString(value, 512) &&
    !/[\u0000-\u001f\u007f]/u.test(value);
}

function isMaintenanceOperation(value: unknown): value is MaintenanceOperation {
  return value === "session-export" || value === "session-import" ||
    value === "backup-create" || value === "backup-restore" ||
    value === "update-check" || value === "update-apply";
}

function isCloudOperation(value: unknown): value is CloudOperation {
  return value === "profile-get" || value === "profile-save-local" ||
    value === "profile-save-remote" || value === "pairing-export" ||
    value === "pairing-import" || value === "session-upload" ||
    value === "session-download" || value === "session-delete" ||
    value === "session-export" || value === "handoff-create" ||
    value === "handoff-receive" || value === "policy-get" ||
    value === "policy-update" || value === "runner-register" ||
    value === "runner-queue" || value === "runner-claim" ||
    value === "runner-complete" || value === "automation-create" ||
    value === "automation-list" || value === "automation-disable";
}

function isExtensionScope(value: unknown): value is ExtensionScope {
  return value === "mcp" || value === "skills" || value === "hooks" ||
    value === "plugins" || value === "marketplace";
}

function isExtensionActionStatus(value: unknown): value is ExtensionActionStatus {
  return value === "success" || value === "validation_error" ||
    value === "confirmation_required" || value === "not_found" ||
    value === "internal_error" || value === "unsupported";
}

function isExtensionMcpTransport(
  value: unknown
): value is ExtensionMcpServer["transport"] {
  return value === "http" || value === "stdio" || value === "managed_gateway";
}

function isExtensionSkillScope(value: unknown): value is ExtensionSkill["scope"] {
  return value === "local" || value === "repo" || value === "user" ||
    value === "server" || value === "bundled" || value === "plugin";
}

function isExtensionPluginScope(value: unknown): value is ExtensionPlugin["scope"] {
  return value === "cli" || value === "project" || value === "user" || value === "config";
}

function isExtensionPluginStatus(
  value: unknown
): value is ExtensionPlugin["hookStatus"] {
  return value === "active" || value === "active_inline" || value === "blocked" ||
    value === "none";
}

function isBoundedStringArray(
  value: unknown,
  maximumCount: number,
  maximumLength: number
): value is string[] {
  return Array.isArray(value) && value.length <= maximumCount &&
    value.every((item) => typeof item === "string" && item.length <= maximumLength);
}

function isEnvironmentNameArray(value: unknown, maximumCount: number): value is string[] {
  return Array.isArray(value) && value.length <= maximumCount &&
    value.every((item) => typeof item === "string" && /^[A-Za-z_][A-Za-z0-9_]{0,127}$/.test(item));
}

function isCloudIdentifier(value: unknown): value is string {
  return typeof value === "string" && /^[A-Za-z0-9._-]{1,128}$/.test(value);
}

function isNullableCloudIdentifier(value: unknown): value is string | null {
  return value === null || isCloudIdentifier(value);
}

function isNullableBoundedString(value: unknown, maximumLength: number): value is string | null {
  return value === null || (typeof value === "string" && value.length <= maximumLength);
}

function isCloudEndpoint(value: unknown): value is string {
  if (typeof value !== "string" || value.length === 0 || value.length > 2048) {
    return false;
  }
  try {
    const endpoint = new URL(value);
    const loopback = endpoint.hostname === "localhost" || endpoint.hostname === "127.0.0.1" ||
      endpoint.hostname === "[::1]" || endpoint.hostname === "::1";
    return endpoint.username === "" && endpoint.password === "" && endpoint.search === "" &&
      endpoint.hash === "" && (endpoint.protocol === "https:" ||
        (endpoint.protocol === "http:" && loopback));
  } catch {
    return false;
  }
}

function isPositiveSafeInteger(value: unknown): value is number {
  return isNonNegativeSafeInteger(value) && value > 0;
}

function isUpdateStatus(value: unknown): value is UpdateStatus {
  return value === "checking" || value === "up-to-date" || value === "available" ||
    value === "launching" || value === "unsupported" || value === "error";
}

function isValidUpdateStatusForOperation(
  status: UpdateStatus,
  operation: MaintenanceOperation
): boolean {
  return operation === "update-check"
    ? status !== "launching"
    : operation === "update-apply"
      ? status === "launching" || status === "unsupported" || status === "error"
      : false;
}

function isOptionalSemanticVersion(value: Record<string, unknown>, key: string): boolean {
  return !Object.hasOwn(value, key) ||
    isSemanticVersion(value[key]);
}

function isSemanticVersion(value: unknown): value is string {
  return typeof value === "string" && value.length <= 256 &&
    /^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$/.test(value);
}

function isSafeFileName(value: unknown): value is string {
  return isBoundedNonEmptyString(value, 255) &&
    value !== "." && value !== ".." &&
    !/[\\/:*?"<>|\u0000-\u001f]/u.test(value);
}

function parseEngineCapabilitiesChanged(value: Record<string, unknown>): HostEvent | null {
  if (!isNonEmptyString(value.sessionId) ||
      typeof value.imagePrompts !== "boolean" ||
      !Array.isArray(value.sessionModes) ||
      !value.sessionModes.every(isSessionMode) ||
      new Set(value.sessionModes).size !== value.sessionModes.length) {
    return null;
  }
  return {
    type: "engine/capabilities",
    sessionId: value.sessionId,
    imagePrompts: value.imagePrompts,
    sessionModes: [...value.sessionModes]
  };
}

function parsePromptAttachmentReferences(values: unknown[]): PromptAttachment[] | null {
  const attachments: PromptAttachment[] = [];
  const names = new Set<string>();
  const tokens = new Set<string>();
  let totalBytes = 0;
  for (const candidate of values) {
    if (!isRecord(candidate) ||
        !hasOnlyKeys(candidate, ["token", "name", "mimeType", "size"]) ||
        typeof candidate.token !== "string" || !/^[0-9A-F]{64}$/u.test(candidate.token) ||
        !isSafeFileName(candidate.name) ||
        !isSupportedImageMimeType(candidate.mimeType) ||
        !isPositiveSafeInteger(candidate.size) || candidate.size > 10 * 1024 * 1024) {
      return null;
    }
    const normalizedName = candidate.name.toLocaleLowerCase();
    if (names.has(normalizedName) || tokens.has(candidate.token)) {
      return null;
    }
    totalBytes += candidate.size;
    if (totalBytes > 20 * 1024 * 1024) {
      return null;
    }
    names.add(normalizedName);
    tokens.add(candidate.token);
    attachments.push({
      token: candidate.token,
      name: candidate.name,
      mimeType: candidate.mimeType,
      size: candidate.size
    });
  }
  return attachments;
}

function parseRuntimeCommandsChanged(value: Record<string, unknown>): HostEvent | null {
  if (!isWorkspaceGeneration(value.workspaceGeneration) ||
      !Array.isArray(value.commands) ||
      value.commands.length > 4096) {
    return null;
  }
  const commands: RuntimeCommand[] = [];
  const names = new Set<string>();
  for (const candidate of value.commands) {
    const command = parseRuntimeCommand(candidate);
    if (!command || names.has(command.name)) {
      return null;
    }
    names.add(command.name);
    commands.push(command);
  }
  return {
    type: "runtime/commands/changed",
    workspaceGeneration: value.workspaceGeneration,
    commands
  };
}

const maximumWorktreeTextLength = 2 * 1024 * 1024;
const maximumWorktreeAggregateTextLength = 16 * 1024 * 1024;

type WorktreeTextBudget = { total: number };

function parseWorktreeCreated(value: Record<string, unknown>): HostEvent | null {
  const budget: WorktreeTextBudget = { total: 0 };
  return isWorkspaceGeneration(value.workspaceGeneration) &&
    isWorktreeCreateStatus(value.status) &&
    consumeWorktreeString(value.sessionId, 512, budget) &&
    consumeWorktreeString(value.worktreePath, 32767, budget) &&
    consumeOptionalWorktreeString(value, "sourceGitRoot", 32767, budget) &&
    consumeOptionalWorktreeString(value, "commit", maximumWorktreeTextLength, budget)
    ? {
        type: "worktree/created",
        workspaceGeneration: value.workspaceGeneration,
        status: value.status,
        sessionId: value.sessionId,
        worktreePath: value.worktreePath,
        ...optionalString(value, "sourceGitRoot"),
        ...optionalString(value, "commit")
      }
    : null;
}

function parseWorktreeListChanged(value: Record<string, unknown>): HostEvent | null {
  if (!isWorkspaceGeneration(value.workspaceGeneration) ||
      !Array.isArray(value.worktrees) ||
      value.worktrees.length > 4096) {
    return null;
  }
  const budget: WorktreeTextBudget = { total: 0 };
  const ids = new Set<string>();
  const worktrees: WorktreeRecord[] = [];
  for (const candidate of value.worktrees) {
    const worktree = parseWorktreeRecord(candidate, budget);
    if (!worktree || ids.has(worktree.id)) {
      return null;
    }
    ids.add(worktree.id);
    worktrees.push(worktree);
  }
  return {
    type: "worktree/list/changed",
    workspaceGeneration: value.workspaceGeneration,
    worktrees
  };
}

function parseWorktreeDetail(value: Record<string, unknown>): HostEvent | null {
  if (!isWorkspaceGeneration(value.workspaceGeneration)) {
    return null;
  }
  if (!Object.hasOwn(value, "worktree")) {
    return { type: "worktree/detail", workspaceGeneration: value.workspaceGeneration };
  }
  const worktree = parseWorktreeRecord(value.worktree, { total: 0 });
  return worktree
    ? { type: "worktree/detail", workspaceGeneration: value.workspaceGeneration, worktree }
    : null;
}

function parseWorktreeApplied(value: Record<string, unknown>): HostEvent | null {
  if (!isWorkspaceGeneration(value.workspaceGeneration) ||
      !isWorktreeApplyStatus(value.status) ||
      !Array.isArray(value.files) ||
      value.files.length > 10000 ||
      !Array.isArray(value.conflicts) ||
      value.conflicts.length > 10000) {
    return null;
  }
  const budget: WorktreeTextBudget = { total: 0 };
  if (!consumeOptionalWorktreeString(value, "gitRoot", 32767, budget)) {
    return null;
  }
  const files: WorktreeFileChange[] = [];
  for (const candidate of value.files) {
    const file = parseWorktreeFileChange(candidate, budget);
    if (!file) {
      return null;
    }
    files.push(file);
  }
  const conflicts: WorktreeConflict[] = [];
  for (const candidate of value.conflicts) {
    const conflict = parseWorktreeConflict(candidate, budget);
    if (!conflict) {
      return null;
    }
    conflicts.push(conflict);
  }
  return {
    type: "worktree/applied",
    workspaceGeneration: value.workspaceGeneration,
    status: value.status,
    files,
    conflicts,
    ...optionalString(value, "gitRoot")
  };
}

function parseWorktreeRemoved(value: Record<string, unknown>): HostEvent | null {
  const budget: WorktreeTextBudget = { total: 0 };
  return isWorkspaceGeneration(value.workspaceGeneration) &&
    consumeWorktreeString(value.idOrPath, 32767, budget) &&
    typeof value.removed === "boolean" &&
    consumeOptionalWorktreeString(value, "resolvedPath", 32767, budget)
    ? {
        type: "worktree/removed",
        workspaceGeneration: value.workspaceGeneration,
        idOrPath: value.idOrPath,
        removed: value.removed,
        ...optionalString(value, "resolvedPath")
      }
    : null;
}

function parseWorktreeGcCompleted(value: Record<string, unknown>): HostEvent | null {
  return isWorkspaceGeneration(value.workspaceGeneration) &&
    isNonNegativeSafeInteger(value.deadRemoved) &&
    isNonNegativeSafeInteger(value.expiredRemoved) &&
    isNonNegativeSafeInteger(value.skippedAlive) &&
    isNonNegativeSafeInteger(value.removeFailed)
    ? {
        type: "worktree/gc/completed",
        workspaceGeneration: value.workspaceGeneration,
        deadRemoved: value.deadRemoved,
        expiredRemoved: value.expiredRemoved,
        skippedAlive: value.skippedAlive,
        removeFailed: value.removeFailed
      }
    : null;
}

function parseWorktreeError(value: Record<string, unknown>): HostEvent | null {
  return isWorkspaceGeneration(value.workspaceGeneration) &&
    isBoundedNonEmptyString(value.message, 4096) &&
    isWorktreeOperation(value.operation) &&
    (!Object.hasOwn(value, "itemId") || isBoundedNonEmptyString(value.itemId, 32767))
    ? {
        type: "worktree/error",
        workspaceGeneration: value.workspaceGeneration,
        message: value.message,
        operation: value.operation,
        ...optionalString(value, "itemId")
      }
    : null;
}

function parseWorktreeRecord(
  value: unknown,
  budget: WorktreeTextBudget
): WorktreeRecord | null {
  if (!isRecord(value) ||
      !hasOnlyKeys(value, [
        "id", "path", "sourceRepository", "repositoryName", "kind", "creationType",
        "gitReference", "headCommit", "sessionId", "creatorProcessId", "createdAt",
        "lastAccessedAt", "status", "metadata"
      ]) ||
      !consumeWorktreeString(value.id, 32767, budget) ||
      !consumeWorktreeString(value.path, 32767, budget) ||
      !consumeWorktreeString(value.sourceRepository, 32767, budget) ||
      !consumeWorktreeString(value.repositoryName, maximumWorktreeTextLength, budget) ||
      !isWorktreeKind(value.kind) ||
      !isWorktreeCreationType(value.creationType) ||
      !consumeOptionalWorktreeString(value, "gitReference", 512, budget) ||
      !consumeOptionalWorktreeString(value, "headCommit", maximumWorktreeTextLength, budget) ||
      !consumeOptionalWorktreeString(value, "sessionId", 512, budget) ||
      !isOptionalNonNegativeSafeInteger(value, "creatorProcessId") ||
      !consumeWorktreeString(value.createdAt, 128, budget) ||
      !isIsoTimestamp(value.createdAt) ||
      !consumeOptionalWorktreeString(value, "lastAccessedAt", 128, budget) ||
      (typeof value.lastAccessedAt === "string" && !isIsoTimestamp(value.lastAccessedAt)) ||
      !isWorktreeRecordStatus(value.status)) {
    return null;
  }

  let metadata: WorktreeRecord["metadata"];
  if (Object.hasOwn(value, "metadata")) {
    if (!isRecord(value.metadata) ||
        !hasOnlyKeys(value.metadata, ["label", "userProvided"]) ||
        !consumeWorktreeString(value.metadata.label, 256, budget) ||
        typeof value.metadata.userProvided !== "boolean") {
      return null;
    }
    metadata = { label: value.metadata.label, userProvided: value.metadata.userProvided };
  }

  return {
    id: value.id,
    path: value.path,
    sourceRepository: value.sourceRepository,
    repositoryName: value.repositoryName,
    kind: value.kind,
    creationType: value.creationType,
    ...optionalString(value, "gitReference"),
    ...optionalString(value, "headCommit"),
    ...optionalString(value, "sessionId"),
    ...optionalNumber(value, "creatorProcessId"),
    createdAt: value.createdAt,
    ...optionalString(value, "lastAccessedAt"),
    status: value.status,
    ...(metadata ? { metadata } : {})
  };
}

function parseWorktreeFileChange(
  value: unknown,
  budget: WorktreeTextBudget
): WorktreeFileChange | null {
  if (!isRecord(value) ||
      !hasOnlyKeys(value, [
        "path", "oldPath", "changeType", "staged", "additions", "deletions", "patch",
        "patchBytes", "patchLines", "oldText", "newText"
      ]) ||
      !consumeWorktreeString(value.path, 32767, budget) ||
      !consumeOptionalWorktreeString(value, "oldPath", 32767, budget) ||
      !isWorktreeChangeType(value.changeType) ||
      (Object.hasOwn(value, "staged") && typeof value.staged !== "boolean") ||
      !isNonNegativeSafeInteger(value.additions) ||
      !isNonNegativeSafeInteger(value.deletions) ||
      !consumeOptionalWorktreeString(value, "patch", maximumWorktreeTextLength, budget, false) ||
      !isOptionalNonNegativeSafeInteger(value, "patchBytes") ||
      !isOptionalNonNegativeSafeInteger(value, "patchLines") ||
      !consumeOptionalWorktreeString(value, "oldText", maximumWorktreeTextLength, budget, false) ||
      !consumeOptionalWorktreeString(value, "newText", maximumWorktreeTextLength, budget, false)) {
    return null;
  }
  return {
    path: value.path,
    ...optionalString(value, "oldPath"),
    changeType: value.changeType,
    ...(typeof value.staged === "boolean" ? { staged: value.staged } : {}),
    additions: value.additions,
    deletions: value.deletions,
    ...optionalString(value, "patch"),
    ...optionalNumber(value, "patchBytes"),
    ...optionalNumber(value, "patchLines"),
    ...optionalString(value, "oldText"),
    ...optionalString(value, "newText")
  };
}

function parseWorktreeConflict(
  value: unknown,
  budget: WorktreeTextBudget
): WorktreeConflict | null {
  if (!isRecord(value) ||
      !hasOnlyKeys(value, ["path", "changeType", "base", "ours", "theirs"]) ||
      !consumeWorktreeString(value.path, 32767, budget) ||
      !isWorktreeChangeType(value.changeType) ||
      !consumeOptionalWorktreeString(value, "base", maximumWorktreeTextLength, budget, false) ||
      !consumeOptionalWorktreeString(value, "ours", maximumWorktreeTextLength, budget, false) ||
      !consumeOptionalWorktreeString(value, "theirs", maximumWorktreeTextLength, budget, false)) {
    return null;
  }
  return {
    path: value.path,
    changeType: value.changeType,
    ...optionalString(value, "base"),
    ...optionalString(value, "ours"),
    ...optionalString(value, "theirs")
  };
}

function consumeWorktreeString(
  value: unknown,
  maximumLength: number,
  budget: WorktreeTextBudget,
  requireNonEmpty = true
): value is string {
  if (typeof value !== "string" ||
      value.length > maximumLength ||
      value.length > maximumWorktreeTextLength ||
      (requireNonEmpty && value.trim().length === 0)) {
    return false;
  }
  budget.total += value.length;
  return budget.total <= maximumWorktreeAggregateTextLength;
}

function consumeOptionalWorktreeString(
  value: Record<string, unknown>,
  key: string,
  maximumLength: number,
  budget: WorktreeTextBudget,
  requireNonEmpty = true
): boolean {
  return !Object.hasOwn(value, key) ||
    consumeWorktreeString(value[key], maximumLength, budget, requireNonEmpty);
}

function parseRuntimeCommand(value: unknown): RuntimeCommand | null {
  if (!isRecord(value) ||
      !hasOnlyKeys(value, ["name", "description", "input", "skill"]) ||
      !isBoundedNonEmptyString(value.name, 256) ||
      !isBoundedString(value.description, 4096)) {
    return null;
  }
  let input: RuntimeCommand["input"];
  if (Object.hasOwn(value, "input")) {
    if (!isRecord(value.input) ||
        !hasOnlyKeys(value.input, ["hint"]) ||
        !isBoundedNonEmptyString(value.input.hint, 2048)) {
      return null;
    }
    input = { hint: value.input.hint };
  }
  let skill: RuntimeCommand["skill"];
  if (Object.hasOwn(value, "skill")) {
    if (!isRecord(value.skill) ||
        !hasOnlyKeys(value.skill, ["scope", "path"]) ||
        !isRuntimeSkillScope(value.skill.scope) ||
        !isBoundedNonEmptyString(value.skill.path, 32767)) {
      return null;
    }
    skill = { scope: value.skill.scope, path: value.skill.path };
  }
  return {
    name: value.name,
    description: value.description,
    ...(input ? { input } : {}),
    ...(skill ? { skill } : {})
  };
}

function parseUiPreferencesChanged(value: Record<string, unknown>): HostEvent | null {
  return isUiLanguage(value.language) &&
    isBoundedString(value.composerDraft, 64 * 1024) &&
    isSessionMode(value.sessionMode) &&
    isExecutionProfile(value.executionProfile) &&
    typeof value.notificationsEnabled === "boolean" &&
    typeof value.windowsAutomationEnabled === "boolean" &&
    typeof value.backgroundUpdateChecksEnabled === "boolean" &&
    typeof value.restartRequired === "boolean"
    ? {
        type: "ui/preferences/changed",
        language: value.language,
        composerDraft: value.composerDraft,
        sessionMode: value.sessionMode,
        executionProfile: value.executionProfile,
        notificationsEnabled: value.notificationsEnabled,
        windowsAutomationEnabled: value.windowsAutomationEnabled,
        backgroundUpdateChecksEnabled: value.backgroundUpdateChecksEnabled,
        restartRequired: value.restartRequired
      }
    : null;
}

function parseRuntimeDashboardChanged(value: Record<string, unknown>): HostEvent | null {
  if (!isNonEmptyString(value.sessionId) ||
      !Array.isArray(value.backgroundTasks) ||
      !Array.isArray(value.subagents)) {
    return null;
  }

  const backgroundTasks: BackgroundTaskSnapshot[] = [];
  const taskIds = new Set<string>();
  for (const candidate of value.backgroundTasks) {
    const task = parseBackgroundTask(candidate);
    if (!task || taskIds.has(task.taskId)) {
      return null;
    }
    taskIds.add(task.taskId);
    backgroundTasks.push(task);
  }

  const subagents: SubagentSnapshot[] = [];
  const subagentIds = new Set<string>();
  for (const candidate of value.subagents) {
    const subagent = parseSubagent(candidate);
    if (!subagent || subagentIds.has(subagent.subagentId)) {
      return null;
    }
    subagentIds.add(subagent.subagentId);
    subagents.push(subagent);
  }

  return {
    type: "runtime/dashboard/changed",
    sessionId: value.sessionId,
    backgroundTasks,
    subagents
  };
}

function parseBackgroundTask(value: unknown): BackgroundTaskSnapshot | null {
  if (!isRecord(value) ||
      !isNonEmptyString(value.taskId) ||
      !isNonEmptyString(value.command) ||
      !isNonEmptyString(value.workingDirectory) ||
      !isIsoTimestamp(value.startedAt) ||
      !isOptionalIsoTimestamp(value, "endedAt") ||
      typeof value.output !== "string" ||
      typeof value.truncated !== "boolean" ||
      !isOptionalSafeInteger(value, "exitCode") ||
      !isOptionalNonEmptyString(value, "signal") ||
      typeof value.completed !== "boolean" ||
      !isBackgroundTaskKind(value.kind) ||
      typeof value.explicitlyKilled !== "boolean" ||
      !isOptionalNonEmptyString(value, "ownerSessionId")) {
    return null;
  }

  return {
    taskId: value.taskId,
    command: value.command,
    workingDirectory: value.workingDirectory,
    startedAt: value.startedAt,
    ...optionalString(value, "endedAt"),
    output: value.output,
    truncated: value.truncated,
    ...(typeof value.exitCode === "number" ? { exitCode: value.exitCode } : {}),
    ...optionalString(value, "signal"),
    completed: value.completed,
    kind: value.kind,
    explicitlyKilled: value.explicitlyKilled,
    ...optionalString(value, "ownerSessionId")
  };
}

function parseRuntimeSubagentDetail(value: Record<string, unknown>): HostEvent | null {
  if (!isNonEmptyString(value.sessionId) || !isNonEmptyString(value.subagentId)) {
    return null;
  }
  if (!Object.hasOwn(value, "snapshot")) {
    return {
      type: "runtime/subagent/detail",
      sessionId: value.sessionId,
      subagentId: value.subagentId
    };
  }
  const snapshot = parseSubagent(value.snapshot);
  return snapshot && snapshot.subagentId === value.subagentId
    ? {
        type: "runtime/subagent/detail",
        sessionId: value.sessionId,
        subagentId: value.subagentId,
        snapshot
      }
    : null;
}

function parseSubagent(value: unknown): SubagentSnapshot | null {
  if (!isRecord(value) ||
      !isNonEmptyString(value.subagentId) ||
      typeof value.parentSessionId !== "string" ||
      typeof value.childSessionId !== "string" ||
      !isNonEmptyString(value.subagentType) ||
      !isNonEmptyString(value.description) ||
      !isIsoTimestamp(value.startedAt) ||
      !isNonNegativeSafeInteger(value.durationMs) ||
      !isSubagentStatus(value.status) ||
      !isOptionalNonNegativeSafeInteger(value, "turnCount") ||
      !isOptionalNonNegativeSafeInteger(value, "toolCallCount") ||
      !isOptionalNonNegativeSafeInteger(value, "tokensUsed") ||
      !isOptionalNonNegativeSafeInteger(value, "contextWindowTokens") ||
      !isOptionalPercentage(value, "contextUsagePercent") ||
      !isOptionalNonEmptyStringArray(value, "toolsUsed") ||
      !isOptionalNonNegativeSafeInteger(value, "errorCount") ||
      !isOptionalString(value, "output") ||
      !isOptionalNonEmptyString(value, "worktreePath") ||
      !isOptionalNonEmptyString(value, "failureError") ||
      !isOptionalNonEmptyString(value, "cancelReason") ||
      !isOptionalNonEmptyString(value, "forkContextSource") ||
      !isOptionalNonEmptyString(value, "forkParentPromptId") ||
      !isOptionalNonEmptyString(value, "resumedFrom")) {
    return null;
  }

  return {
    subagentId: value.subagentId,
    parentSessionId: value.parentSessionId,
    childSessionId: value.childSessionId,
    subagentType: value.subagentType,
    description: value.description,
    startedAt: value.startedAt,
    durationMs: value.durationMs,
    status: value.status,
    ...optionalNumber(value, "turnCount"),
    ...optionalNumber(value, "toolCallCount"),
    ...optionalNumber(value, "tokensUsed"),
    ...optionalNumber(value, "contextWindowTokens"),
    ...optionalNumber(value, "contextUsagePercent"),
    ...(Array.isArray(value.toolsUsed) ? { toolsUsed: [...value.toolsUsed] as string[] } : {}),
    ...optionalNumber(value, "errorCount"),
    ...optionalString(value, "output"),
    ...optionalString(value, "worktreePath"),
    ...optionalString(value, "failureError"),
    ...optionalString(value, "cancelReason"),
    ...optionalString(value, "forkContextSource"),
    ...optionalString(value, "forkParentPromptId"),
    ...optionalString(value, "resumedFrom")
  };
}

function parseSessionForked(value: Record<string, unknown>): HostEvent | null {
  if (!isNonEmptyString(value.sessionId) ||
      !isNonEmptyString(value.workspacePath) ||
      !isNonEmptyString(value.parentSessionId) ||
      !isNonNegativeSafeInteger(value.chatMessagesCopied) ||
      !isNonNegativeSafeInteger(value.updatesCopied) ||
      typeof value.planStateCopied !== "boolean" ||
      (Object.hasOwn(value, "modelId") && !isNonEmptyString(value.modelId))) {
    return null;
  }

  return {
    type: "session/forked",
    sessionId: value.sessionId,
    workspacePath: value.workspacePath,
    parentSessionId: value.parentSessionId,
    chatMessagesCopied: value.chatMessagesCopied,
    updatesCopied: value.updatesCopied,
    planStateCopied: value.planStateCopied,
    ...(isNonEmptyString(value.modelId) ? { modelId: value.modelId } : {})
  };
}

function parseSessionRewindPoints(value: Record<string, unknown>): HostEvent | null {
  if (!isNonEmptyString(value.sessionId) || !Array.isArray(value.points)) {
    return null;
  }

  const points: SessionRewindPoint[] = [];
  for (const candidate of value.points) {
    if (!isRecord(candidate) ||
        !isNonNegativeSafeInteger(candidate.promptIndex) ||
        !isIsoTimestamp(candidate.createdAt) ||
        !isNonNegativeSafeInteger(candidate.fileSnapshotCount) ||
        typeof candidate.hasFileChanges !== "boolean" ||
        !isOptionalString(candidate, "promptPreview")) {
      return null;
    }
    points.push({
      promptIndex: candidate.promptIndex,
      createdAt: candidate.createdAt,
      fileSnapshotCount: candidate.fileSnapshotCount,
      hasFileChanges: candidate.hasFileChanges,
      ...optionalString(candidate, "promptPreview")
    });
  }

  return { type: "session/rewind/points", sessionId: value.sessionId, points };
}

function parseSessionRewound(value: Record<string, unknown>): HostEvent | null {
  if (!isNonEmptyString(value.sessionId) ||
      typeof value.success !== "boolean" ||
      !isNonNegativeSafeInteger(value.targetPromptIndex) ||
      !isSessionRewindMode(value.mode) ||
      !isNonEmptyStringArray(value.revertedFiles) ||
      !isNonEmptyStringArray(value.cleanFiles) ||
      !Array.isArray(value.conflicts) ||
      !isOptionalString(value, "promptText") ||
      !isOptionalString(value, "error")) {
    return null;
  }

  const conflicts: SessionRewindConflict[] = [];
  for (const candidate of value.conflicts) {
    if (!isRecord(candidate) ||
        !isNonEmptyString(candidate.path) ||
        !isNonEmptyString(candidate.conflictType)) {
      return null;
    }
    conflicts.push({ path: candidate.path, conflictType: candidate.conflictType });
  }

  return {
    type: "session/rewound",
    sessionId: value.sessionId,
    success: value.success,
    targetPromptIndex: value.targetPromptIndex,
    mode: value.mode,
    revertedFiles: [...value.revertedFiles],
    cleanFiles: [...value.cleanFiles],
    conflicts,
    ...optionalString(value, "promptText"),
    ...optionalString(value, "error")
  };
}

function parseSessionListChanged(
  value: Record<string, unknown>,
  pendingSessionLists: Set<string>
): HostEvent | null {
  const requestId = Object.hasOwn(value, "requestId") && isMaintenanceRequestId(value.requestId)
    ? value.requestId
    : undefined;
  if (!Array.isArray(value.sessions) ||
      (Object.hasOwn(value, "requestId") && !requestId) ||
      (requestId && !pendingSessionLists.has(requestId)) ||
      (Object.hasOwn(value, "nextCursor") && !isNonEmptyString(value.nextCursor))) {
    return null;
  }

  const sessions: SessionSummary[] = [];
  for (const candidate of value.sessions) {
    if (!isRecord(candidate) ||
        !isNonEmptyString(candidate.sessionId) ||
        !isNonEmptyString(candidate.title) ||
        !isNonEmptyString(candidate.workspacePath) ||
        !isIsoTimestamp(candidate.createdAt) ||
        !isIsoTimestamp(candidate.updatedAt) ||
        !isNonNegativeSafeInteger(candidate.messageCount) ||
        !sessionOptionalStringsAreValid(candidate)) {
      return null;
    }

    sessions.push({
      sessionId: candidate.sessionId,
      title: candidate.title,
      workspacePath: candidate.workspacePath,
      createdAt: candidate.createdAt,
      updatedAt: candidate.updatedAt,
      messageCount: candidate.messageCount,
      ...optionalSessionString(candidate, "modelId"),
      ...optionalSessionString(candidate, "parentSessionId"),
      ...optionalSessionString(candidate, "branch"),
      ...optionalSessionString(candidate, "worktreeLabel"),
      ...optionalSessionString(candidate, "sourceWorkspacePath")
    });
  }

  if (requestId) {
    pendingSessionLists.delete(requestId);
  }
  return {
    type: "session/list/changed",
    ...(requestId ? { requestId } : {}),
    sessions,
    ...(isNonEmptyString(value.nextCursor) ? { nextCursor: value.nextCursor } : {})
  };
}

function parseSessionListError(
  value: Record<string, unknown>,
  pendingSessionLists: Set<string>
): HostEvent | null {
  if (!isMaintenanceRequestId(value.requestId) ||
      !pendingSessionLists.has(value.requestId) ||
      !isBoundedNonEmptyString(value.message, 4096)) {
    return null;
  }
  pendingSessionLists.delete(value.requestId);
  return { type: "session/list/error", requestId: value.requestId, message: value.message };
}

const sessionOptionalStringKeys = [
  "modelId",
  "parentSessionId",
  "branch",
  "worktreeLabel",
  "sourceWorkspacePath"
] as const;

function sessionOptionalStringsAreValid(value: Record<string, unknown>): boolean {
  return sessionOptionalStringKeys.every(
    (key) => !Object.hasOwn(value, key) || isNonEmptyString(value[key])
  );
}

function optionalSessionString<Key extends typeof sessionOptionalStringKeys[number]>(
  value: Record<string, unknown>,
  key: Key
): Partial<Record<Key, string>> {
  return isNonEmptyString(value[key]) ? { [key]: value[key] } as Record<Key, string> : {};
}

function parseProviderStatus(value: Record<string, unknown>): HostEvent | null {
  if (!isProviderStatus(value.status) ||
      typeof value.baseUrl !== "string" ||
      typeof value.model !== "string") {
    return null;
  }
  const providerIdentityIsValid = value.status === "error"
    ? true
    : isNonEmptyString(value.baseUrl) && isNonEmptyString(value.model);
  if (!providerIdentityIsValid ||
      !isProviderBackend(value.backend) ||
      typeof value.allowInsecureTransport !== "boolean" ||
      typeof value.hasCredential !== "boolean" ||
      !isOptionalString(value, "message")) {
    return null;
  }

  return {
    type: "provider/status",
    status: value.status,
    baseUrl: value.baseUrl,
    model: value.model,
    backend: value.backend,
    allowInsecureTransport: value.allowInsecureTransport,
    hasCredential: value.hasCredential,
    ...optionalString(value, "message")
  };
}

function parsePermissionRequest(value: Record<string, unknown>): HostEvent | null {
  if (!isNonEmptyString(value.requestId) ||
      !isNonEmptyString(value.sessionId) ||
      !isNonEmptyString(value.toolCallId) ||
      !isNonEmptyString(value.title) ||
      !Array.isArray(value.options) ||
      value.options.length === 0 ||
      !Array.isArray(value.locations) ||
      !value.locations.every((location) => typeof location === "string")) {
    return null;
  }

  const options: PermissionOption[] = [];
  for (const candidate of value.options) {
    if (!isRecord(candidate) ||
        !isNonEmptyString(candidate.optionId) ||
        !isNonEmptyString(candidate.name) ||
        !isPermissionOptionKind(candidate.kind)) {
      return null;
    }
    options.push({
      optionId: candidate.optionId,
      name: candidate.name,
      kind: candidate.kind
    });
  }

  return {
    type: "permission/requested",
    requestId: value.requestId,
    sessionId: value.sessionId,
    toolCallId: value.toolCallId,
    title: value.title,
    ...(typeof value.toolKind === "string" ? { toolKind: value.toolKind } : {}),
    ...(Object.hasOwn(value, "rawInput") ? { rawInput: value.rawInput } : {}),
    options,
    locations: [...value.locations]
  };
}

function optionalString(value: Record<string, unknown>, key: string): Record<string, string> {
  return typeof value[key] === "string" ? { [key]: value[key] } : {};
}

function optionalNumber(value: Record<string, unknown>, key: string): Record<string, number> {
  return typeof value[key] === "number" ? { [key]: value[key] } : {};
}

function optionalEngineCapabilities(value: unknown): { capabilities: EngineCapabilities } | {} {
  if (!isRecord(value) ||
      !hasOnlyKeys(value, ["executionProfiles", "wslStrictReason", "imagePrompts", "sessionModes"]) ||
      !Array.isArray(value.executionProfiles) ||
      !value.executionProfiles.every(isExecutionProfile) ||
      (Object.hasOwn(value, "imagePrompts") && typeof value.imagePrompts !== "boolean") ||
      (Object.hasOwn(value, "sessionModes") &&
        (!Array.isArray(value.sessionModes) || !value.sessionModes.every(isSessionMode)))) {
    return {};
  }
  return {
    capabilities: {
      executionProfiles: [...new Set(value.executionProfiles)],
      ...(typeof value.wslStrictReason === "string" && value.wslStrictReason.trim()
        ? { wslStrictReason: value.wslStrictReason }
        : {}),
      ...(typeof value.imagePrompts === "boolean" ? { imagePrompts: value.imagePrompts } : {}),
      ...(Array.isArray(value.sessionModes)
        ? { sessionModes: [...new Set(value.sessionModes)] as SessionMode[] }
        : {})
    }
  };
}

function isEngineStatus(value: unknown): value is EngineStatus {
  return value === "idle" || value === "starting" || value === "ready" || value === "running" ||
    value === "stopped" || value === "error";
}

function isSupportedImageMimeType(value: unknown): value is PromptAttachment["mimeType"] {
  return value === "image/png" || value === "image/jpeg" ||
    value === "image/gif" || value === "image/webp";
}

function isImageAttachmentError(value: unknown): value is ImageAttachmentError {
  return value === "unsupported_type" || value === "too_many" || value === "too_large" ||
    value === "total_too_large" || value === "duplicate_name" ||
    value === "content_mismatch" || value === "read_failed";
}

function isProviderStatus(value: unknown): value is "loaded" | "saved" | "error" {
  return value === "loaded" || value === "saved" || value === "error";
}

function isProviderBackend(value: unknown): value is ProviderBackend {
  return value === "chat_completions" || value === "responses";
}

function isOptionalString(value: Record<string, unknown>, key: string): boolean {
  return !Object.hasOwn(value, key) || typeof value[key] === "string";
}

function isOptionalNonEmptyString(value: Record<string, unknown>, key: string): boolean {
  return !Object.hasOwn(value, key) || isNonEmptyString(value[key]);
}

function isOptionalIsoTimestamp(value: Record<string, unknown>, key: string): boolean {
  return !Object.hasOwn(value, key) || isIsoTimestamp(value[key]);
}

function isOptionalSafeInteger(value: Record<string, unknown>, key: string): boolean {
  return !Object.hasOwn(value, key) ||
    (typeof value[key] === "number" && Number.isSafeInteger(value[key]));
}

function isOptionalNonNegativeSafeInteger(value: Record<string, unknown>, key: string): boolean {
  return !Object.hasOwn(value, key) || isNonNegativeSafeInteger(value[key]);
}

function isOptionalPercentage(value: Record<string, unknown>, key: string): boolean {
  return !Object.hasOwn(value, key) ||
    (isNonNegativeSafeInteger(value[key]) && (value[key] as number) <= 100);
}

function isOptionalNonEmptyStringArray(value: Record<string, unknown>, key: string): boolean {
  return !Object.hasOwn(value, key) || isNonEmptyStringArray(value[key]);
}

function isExecutionProfile(value: unknown): value is ExecutionProfile {
  return value === "NativeProtected" || value === "WslStrict";
}

function isSessionMode(value: unknown): value is SessionMode {
  return value === "default" || value === "plan";
}

function isSessionRewindMode(value: unknown): value is SessionRewindMode {
  return value === "all" || value === "conversation_only" || value === "files_only";
}

function isBackgroundTaskKind(value: unknown): value is BackgroundTaskKind {
  return value === "bash" || value === "monitor";
}

function isBackgroundTaskKillOutcome(value: unknown): value is BackgroundTaskKillOutcome {
  return value === "killed" || value === "already_exited" || value === "not_found";
}

function isSubagentStatus(value: unknown): value is SubagentStatus {
  return value === "initializing" || value === "running" || value === "completed" ||
    value === "failed" || value === "cancelled";
}

function isSubagentCancelOutcome(value: unknown): value is SubagentCancelOutcome {
  return value === "cancelled" || value === "already_finished" || value === "not_found";
}

function isRuntimeDashboardOperation(value: unknown): value is RuntimeDashboardOperation {
  return value === "refresh" || value === "task_kill" ||
    value === "subagent_get" || value === "subagent_cancel";
}

function isRuntimeSkillScope(value: unknown): value is RuntimeSkillScope {
  return value === "local" || value === "repo" || value === "user" || value === "plugin";
}

function isWorktreeCreateStatus(value: unknown): value is WorktreeCreateStatus {
  return value === "creating" || value === "exists";
}

function isWorktreeKind(value: unknown): value is WorktreeKind {
  return value === "session" || value === "ab" || value === "pool" ||
    value === "fork" || value === "manual" || value === "subagent";
}

function isWorktreeCreationType(value: unknown): value is WorktreeCreationType {
  return value === "linked" || value === "standalone" || value === "git";
}

function isWorktreeRecordStatus(value: unknown): value is WorktreeRecordStatus {
  return value === "alive" || value === "dead";
}

function isWorktreeApplyStatus(value: unknown): value is WorktreeApplyStatus {
  return value === "success" || value === "conflicts";
}

function isWorktreeChangeType(value: unknown): value is WorktreeChangeType {
  return value === "create" || value === "edit" || value === "delete" ||
    value === "rename" || value === "copy" || value === "type_change" ||
    value === "untracked";
}

function isWorktreeOperation(value: unknown): value is WorktreeOperation {
  return value === "create" || value === "list" || value === "show" ||
    value === "apply" || value === "remove" || value === "gc";
}

function isMemoryFlushStatus(value: unknown): value is "running" | "succeeded" | "error" {
  return value === "running" || value === "succeeded" || value === "error";
}

function isUiLanguage(value: unknown): value is UiLanguage {
  return value === "zh-CN" || value === "en-US";
}

function isPermissionOptionKind(value: unknown): value is PermissionOptionKind {
  return value === "allow_once" || value === "allow_always" ||
    value === "reject_once" || value === "reject_always";
}

function isNonEmptyString(value: unknown): value is string {
  return typeof value === "string" && value.trim().length > 0;
}

function isBoundedString(value: unknown, maximumLength: number): value is string {
  return typeof value === "string" && value.length <= maximumLength;
}

function isBoundedNonEmptyString(value: unknown, maximumLength: number): value is string {
  return isNonEmptyString(value) && value.length <= maximumLength;
}

function isWindowsAutomationAction(value: unknown): value is WindowsAutomationAction {
  return value === "focus-window" || value === "invoke" || value === "set-value";
}

function isWindowsAutomationErrorReason(
  value: unknown
): value is WindowsAutomationErrorReason {
  return value === "disabled" || value === "busy" || value === "failed";
}

function isWindowsProcessId(value: unknown): value is number {
  return typeof value === "number" &&
    Number.isSafeInteger(value) &&
    value > 0 &&
    value <= 2_147_483_647;
}

function isOptionalCommandTarget(value: unknown): boolean {
  return value === undefined || isBoundedNonEmptyString(value, 256);
}

function isOptionalAutomationValue(value: unknown): boolean {
  return value === undefined || (typeof value === "string" && value.length <= 8192);
}

function isOptionalBoundedString(
  value: Record<string, unknown>,
  key: string,
  maximumLength: number
): boolean {
  return !Object.hasOwn(value, key) || isBoundedString(value[key], maximumLength);
}

function isWorkspaceGeneration(value: unknown): value is number {
  return typeof value === "number"
    && Number.isInteger(value)
    && value >= 0
    && value <= 2_147_483_647;
}

function isMemorySessionId(value: unknown): value is string {
  return typeof value === "string" && value.length > 0 && value.length <= 512 &&
    value.trim().length > 0 && !/[\p{Cc}\p{Cf}\p{Zl}\p{Zp}]/u.test(value);
}

function isMemoryFileId(value: unknown): value is string {
  if (value === "global" || value === "workspace") {
    return true;
  }
  if (typeof value !== "string" || !value.startsWith("session/")) {
    return false;
  }
  const fileName = value.slice("session/".length);
  return fileName.length > 0 && fileName.length <= 255 && fileName.endsWith(".md") &&
    /^[A-Za-z0-9._-]+$/u.test(fileName);
}

function isMemoryContent(value: unknown): value is string {
  return typeof value === "string" &&
    new TextEncoder().encode(value).byteLength <= maximumMemoryContentBytes;
}

function isMemoryMessage(value: unknown): value is string {
  return isBoundedNonEmptyString(value, maximumMemoryMessageCharacters) &&
    !/[\p{Cc}\p{Cf}\p{Zl}\p{Zp}]/u.test(value);
}

function isMemoryConfirmationToken(value: unknown): value is string {
  return typeof value === "string" && memoryConfirmationTokenPattern.test(value);
}

function isMemoryOperation(value: unknown): value is MemoryOperation {
  return value === "list" || value === "read" || value === "write" || value === "delete";
}

function isMemoryFileScope(value: unknown): value is MemoryFileScope {
  return value === "global" || value === "workspace" || value === "session";
}

function isMemoryMutationStatus(value: unknown): value is MemoryMutationStatus {
  return value === "confirmation_required" || value === "success" || value === "not_found";
}

function isNonNegativeSafeInteger(value: unknown): value is number {
  return typeof value === "number" && Number.isSafeInteger(value) && value >= 0;
}

function isNonEmptyStringArray(value: unknown): value is string[] {
  return Array.isArray(value) && value.every(isNonEmptyString);
}

function isIsoTimestamp(value: unknown): value is string {
  return typeof value === "string" &&
    /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})$/.test(value) &&
    Number.isFinite(Date.parse(value));
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

const hostEventKeys: Readonly<Record<string, readonly string[]>> = {
  "engine/status": [
    "schemaVersion", "type", "status", "message", "sessionId", "engineEpoch", "capabilities"
  ],
  "engine/capabilities": ["schemaVersion", "type", "sessionId", "imagePrompts", "sessionModes"],
  "attachment/changed": [
    "schemaVersion", "type", "requestId", "attachments", "error", "cancelled"
  ],
  "memory/capabilities": ["schemaVersion", "type", "sessionId", "memory"],
  "memory/listed": [
    "schemaVersion", "type", "requestId", "workspaceGeneration", "sessionId", "files", "truncated"
  ],
  "memory/document": [
    "schemaVersion", "type", "requestId", "workspaceGeneration", "sessionId", "file", "content"
  ],
  "memory/mutation": [
    "schemaVersion", "type", "requestId", "workspaceGeneration", "sessionId", "operation",
    "fileId", "status", "message", "file", "confirmationToken"
  ],
  "memory/error": [
    "schemaVersion", "type", "requestId", "workspaceGeneration", "sessionId", "operation",
    "fileId", "message"
  ],
  "workspace/selected": ["schemaVersion", "type", "path", "workspaceGeneration"],
  "workspace/context/instructions/list": [
    "schemaVersion", "type", "requestId", "workspaceGeneration", "files"
  ],
  "workspace/context/file/read": [
    "schemaVersion", "type", "requestId", "workspaceGeneration", "relativePath", "content"
  ],
  "workspace/context/instructions/write": [
    "schemaVersion", "type", "requestId", "workspaceGeneration", "relativePath"
  ],
  "workspace/context/file/search": [
    "schemaVersion", "type", "requestId", "workspaceGeneration", "query", "files"
  ],
  "workspace/context/error": [
    "schemaVersion", "type", "requestId", "workspaceGeneration", "operation"
  ],
  "credential/status": ["schemaVersion", "type", "status", "message"],
  "provider/status": [
    "schemaVersion", "type", "status", "baseUrl", "model", "backend",
    "allowInsecureTransport", "hasCredential", "message"
  ],
  "session/update": [
    "schemaVersion", "type", "sessionId", "updateKind", "text", "update", "engineEpoch"
  ],
  "prompt/completed": ["schemaVersion", "type", "sessionId", "stopReason"],
  "session/list/changed": ["schemaVersion", "type", "requestId", "sessions", "nextCursor"],
  "session/list/error": ["schemaVersion", "type", "requestId", "message"],
  "session/active/changed": [
    "schemaVersion", "type", "sessionId", "workspacePath", "engineEpoch"
  ],
  "session/renamed": ["schemaVersion", "type", "requestId", "sessionId", "title"],
  "session/archive/changed": [
    "schemaVersion", "type", "requestId", "sessionId", "archived"
  ],
  "session/operation/error": [
    "schemaVersion", "type", "requestId", "operation", "sessionId", "message"
  ],
  "session/forked": [
    "schemaVersion", "type", "sessionId", "workspacePath", "parentSessionId",
    "chatMessagesCopied", "updatesCopied", "planStateCopied", "modelId"
  ],
  "session/compacted": ["schemaVersion", "type", "sessionId"],
  "session/rewind/points": ["schemaVersion", "type", "sessionId", "points"],
  "session/rewind/points/error": ["schemaVersion", "type", "sessionId", "message"],
  "session/rewound": [
    "schemaVersion", "type", "sessionId", "success", "targetPromptIndex", "mode",
    "revertedFiles", "cleanFiles", "conflicts", "promptText", "error"
  ],
  "session/mode/changed": ["schemaVersion", "type", "sessionId", "mode", "planAvailable"],
  "runtime/dashboard/changed": ["schemaVersion", "type", "sessionId", "backgroundTasks", "subagents"],
  "runtime/task/killed": ["schemaVersion", "type", "sessionId", "taskId", "outcome"],
  "runtime/subagent/detail": ["schemaVersion", "type", "sessionId", "subagentId", "snapshot"],
  "runtime/subagent/cancelled": [
    "schemaVersion", "type", "sessionId", "subagentId", "outcome", "terminalStatus"
  ],
  "runtime/dashboard/error": ["schemaVersion", "type", "sessionId", "message", "operation", "itemId"],
  "runtime/commands/changed": ["schemaVersion", "type", "workspaceGeneration", "commands"],
  "runtime/commands/error": ["schemaVersion", "type", "workspaceGeneration", "message"],
  "worktree/created": [
    "schemaVersion", "type", "workspaceGeneration", "status", "sessionId",
    "worktreePath", "sourceGitRoot", "commit"
  ],
  "worktree/list/changed": ["schemaVersion", "type", "workspaceGeneration", "worktrees"],
  "worktree/detail": ["schemaVersion", "type", "workspaceGeneration", "worktree"],
  "worktree/applied": [
    "schemaVersion", "type", "workspaceGeneration", "status", "files", "conflicts", "gitRoot"
  ],
  "worktree/removed": [
    "schemaVersion", "type", "workspaceGeneration", "idOrPath", "removed", "resolvedPath"
  ],
  "worktree/gc/completed": [
    "schemaVersion", "type", "workspaceGeneration", "deadRemoved", "expiredRemoved",
    "skippedAlive", "removeFailed"
  ],
  "worktree/error": [
    "schemaVersion", "type", "workspaceGeneration", "message", "operation", "itemId"
  ],
  "runtime/memory/status": ["schemaVersion", "type", "sessionId", "status", "message"],
  "ui/preferences/changed": [
    "schemaVersion", "type", "language", "composerDraft", "sessionMode",
    "executionProfile", "notificationsEnabled", "windowsAutomationEnabled",
    "backgroundUpdateChecksEnabled", "restartRequired"
  ],
  "session/exported": ["schemaVersion", "type", "requestId", "sessionId", "fileName"],
  "session/imported": [
    "schemaVersion", "type", "requestId", "sessionId", "workspacePath"
  ],
  "backup/completed": [
    "schemaVersion", "type", "requestId", "operation", "fileCount", "totalBytes",
    "restartRequired"
  ],
  "update/status": ["schemaVersion", "type", "requestId", "status", "version"],
  "update/background-available": ["schemaVersion", "type", "version"],
  "maintenance/error": ["schemaVersion", "type", "requestId", "operation"],
  "maintenance/cancelled": ["schemaVersion", "type", "requestId", "operation"],
  "cloud/profile": [
    "schemaVersion", "type", "requestId", "localOnly", "baseUri", "teamId",
    "deviceId", "hasAccessToken"
  ],
  "cloud/pairing/completed": ["schemaVersion", "type", "requestId", "operation"],
  "cloud/session/uploaded": [
    "schemaVersion", "type", "requestId", "sessionId", "revision"
  ],
  "cloud/session/imported": [
    "schemaVersion", "type", "requestId", "remoteSessionId", "found", "revision",
    "importedSessionId"
  ],
  "cloud/session/deleted": [
    "schemaVersion", "type", "requestId", "remoteSessionId", "found", "revision"
  ],
  "cloud/session/exported": [
    "schemaVersion", "type", "requestId", "sessionId", "fileName"
  ],
  "cloud/notification": [
    "schemaVersion", "type", "kind", "resourceId", "policyVersion"
  ],
  "cloud/handoff/created": [
    "schemaVersion", "type", "requestId", "handoffId", "sessionId", "targetDeviceId"
  ],
  "cloud/handoffs/received": ["schemaVersion", "type", "requestId", "imports"],
  "cloud/policy": [
    "schemaVersion", "type", "requestId", "version", "allowedExecutionProfiles",
    "remoteRunnerEnabled", "uiAutomationEnabled", "maximumConcurrentJobs",
    "allowedPluginPublishers"
  ],
  "cloud/runner/registered": [
    "schemaVersion", "type", "requestId", "runnerId", "capabilities"
  ],
  "cloud/runner/queued": ["schemaVersion", "type", "requestId", "jobId"],
  "cloud/runner/claimed": [
    "schemaVersion", "type", "requestId", "found", "claimHandle", "jobId",
    "requiredCapability", "task", "leaseExpiresAt"
  ],
  "cloud/runner/completed": [
    "schemaVersion", "type", "requestId", "claimHandle", "jobId"
  ],
  "cloud/automations": ["schemaVersion", "type", "requestId", "automations"],
  "cloud/automation/created": ["schemaVersion", "type", "requestId", "automation"],
  "cloud/automation/disabled": [
    "schemaVersion", "type", "requestId", "automationId", "disabled"
  ],
  "cloud/error": ["schemaVersion", "type", "requestId", "operation"],
  "cloud/cancelled": ["schemaVersion", "type", "requestId", "operation"],
  "extensions/catalog": [
    "schemaVersion", "type", "requestId", "sessionId", "mcp", "skills", "hooks",
    "plugins", "marketplace"
  ],
  "extensions/action/completed": [
    "schemaVersion", "type", "requestId", "sessionId", "scope", "action", "status",
    "message", "requiresReload", "requiresRestart"
  ],
  "extensions/error": [
    "schemaVersion", "type", "requestId", "sessionId", "scope", "action", "message"
  ],
  "windows/automation/completed": [
    "schemaVersion", "type", "requestId", "action", "processId", "target"
  ],
  "windows/automation/cancelled": ["schemaVersion", "type", "requestId"],
  "windows/automation/error": ["schemaVersion", "type", "requestId", "reason"],
  "permission/requested": [
    "schemaVersion", "type", "requestId", "sessionId", "toolCallId", "title",
    "toolKind", "rawInput", "options", "locations"
  ]
};

function hasOnlyKnownHostEventKeys(value: Record<string, unknown>): boolean {
  const keys = typeof value.type === "string" ? hostEventKeys[value.type] : undefined;
  return keys !== undefined && hasOnlyKeys(value, keys);
}

function hasOnlyKeys(value: Record<string, unknown>, keys: readonly string[]): boolean {
  const allowed = new Set(keys);
  return Object.keys(value).every((key) => allowed.has(key));
}

declare global {
  interface Window {
    chrome?: { webview?: WebViewTransport };
  }
}

export const defaultHostBridge = createHostBridge(
  typeof window === "undefined" ? undefined : window.chrome?.webview
);
