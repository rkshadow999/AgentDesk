//! Portable AgentDesk session transfer documents.
//!
//! The engine remains the owner of session data. This module serializes the
//! persisted session directory into a bounded JSON document and restores it
//! through a staging directory before an atomic move into the live store.

use crate::session::info::Info;
use crate::session::persistence::{CopiedSessionFile, Summary};
use crate::session::storage::{CopySessionOptions, JsonlStorageAdapter};
use crate::util::grok_home::encode_cwd_dirname;
use agent_client_protocol as acp;
use base64::Engine as _;
use serde::{Deserialize, Serialize};
use std::collections::HashSet;
use std::fmt;
use std::io;
use std::path::{Component, Path, PathBuf};

pub const SESSION_TRANSFER_SCHEMA_VERSION: u32 = 1;
pub const SESSION_TRANSFER_FORMAT: &str = "agentdesk.session.v1";
pub const MAX_SESSION_DOCUMENT_BYTES: usize = 16 * 1024 * 1024;
const MAX_DECODED_BYTES: usize = 12 * 1024 * 1024;
const MAX_FILE_BYTES: usize = 10 * 1024 * 1024;
const MAX_FILES: usize = 512;
const MAX_PATH_BYTES: usize = 512;
const MAX_COMPONENT_CHARS: usize = 255;
const MAX_SESSION_ID_CHARS: usize = 128;
const MAX_CWD_CHARS: usize = 32 * 1024;
const MAX_DIRECTORY_DEPTH: usize = 16;

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct SessionTransferDocument {
    pub format: String,
    pub source_session_id: String,
    pub source_cwd: String,
    pub files: Vec<SessionTransferFile>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct SessionTransferFile {
    pub path: String,
    pub data: String,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SessionTransferImportResult {
    pub session_id: String,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct SessionTransferExportRequest {
    pub session_id: String,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct SessionTransferExportResponse {
    pub schema_version: u32,
    pub session: SessionTransferDocument,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct SessionTransferImportRequest {
    pub cwd: String,
    pub session: SessionTransferDocument,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct SessionTransferImportResponse {
    pub schema_version: u32,
    pub session_id: String,
}

#[derive(Debug)]
pub enum SessionTransferError {
    Invalid(String),
    Io(io::Error),
    Json(serde_json::Error),
}

impl SessionTransferError {
    pub fn is_invalid(&self) -> bool {
        matches!(self, Self::Invalid(_) | Self::Json(_))
            || matches!(self, Self::Io(error) if error.kind() == io::ErrorKind::InvalidData)
    }
}

impl fmt::Display for SessionTransferError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Invalid(message) => formatter.write_str(message),
            Self::Io(error) => write!(formatter, "session transfer I/O failed: {error}"),
            Self::Json(_) => formatter.write_str("session transfer JSON is invalid"),
        }
    }
}

impl std::error::Error for SessionTransferError {}

impl From<io::Error> for SessionTransferError {
    fn from(error: io::Error) -> Self {
        Self::Io(error)
    }
}

impl From<serde_json::Error> for SessionTransferError {
    fn from(error: serde_json::Error) -> Self {
        Self::Json(error)
    }
}

pub fn export_session_document_from_directory(
    session_id: &str,
    session_dir: &Path,
) -> Result<SessionTransferDocument, SessionTransferError> {
    validate_session_id(session_id)?;
    ensure_regular_directory(session_dir)?;
    let summary_bytes = std::fs::read(session_dir.join("summary.json"))?;
    let summary: Summary = serde_json::from_slice(&summary_bytes)?;
    if summary.info.id.0.as_ref() != session_id {
        return Err(SessionTransferError::Invalid(
            "the session summary id does not match the export request".to_string(),
        ));
    }

    let mut files = Vec::new();
    collect_directory_files(session_dir, session_dir, 0, &mut files)?;
    export_session_document_from_files(session_id, &summary.info.cwd, files)
}

pub fn export_session_document_from_files(
    session_id: &str,
    source_cwd: &str,
    files: Vec<CopiedSessionFile>,
) -> Result<SessionTransferDocument, SessionTransferError> {
    validate_session_id(session_id)?;
    validate_cwd(source_cwd)?;

    let mut seen = HashSet::new();
    let mut total_bytes = 0usize;
    let mut exported_files = Vec::with_capacity(files.len().min(MAX_FILES));
    for file in files {
        let path = file.name.replace('\\', "/");
        if should_skip_file(&path) {
            continue;
        }
        validate_archive_path(&path)?;
        let duplicate_key = path.to_lowercase();
        if !seen.insert(duplicate_key) {
            return Err(SessionTransferError::Invalid(
                "the session archive contains duplicate paths".to_string(),
            ));
        }
        if exported_files.len() >= MAX_FILES {
            return Err(SessionTransferError::Invalid(
                "the session archive contains too many files".to_string(),
            ));
        }
        if file.data.len() > MAX_FILE_BYTES {
            return Err(SessionTransferError::Invalid(
                "a session archive file exceeds the size limit".to_string(),
            ));
        }
        total_bytes = total_bytes.checked_add(file.data.len()).ok_or_else(|| {
            SessionTransferError::Invalid("the session archive size overflowed".to_string())
        })?;
        if total_bytes > MAX_DECODED_BYTES {
            return Err(SessionTransferError::Invalid(
                "the session archive exceeds the decoded size limit".to_string(),
            ));
        }
        exported_files.push(SessionTransferFile {
            path,
            data: base64::engine::general_purpose::STANDARD.encode(file.data),
        });
    }
    exported_files.sort_by(|left, right| left.path.cmp(&right.path));
    if !exported_files
        .iter()
        .any(|file| file.path == "summary.json")
    {
        return Err(SessionTransferError::Invalid(
            "the session archive is missing summary.json".to_string(),
        ));
    }

    let document = SessionTransferDocument {
        format: SESSION_TRANSFER_FORMAT.to_string(),
        source_session_id: session_id.to_string(),
        source_cwd: source_cwd.to_string(),
        files: exported_files,
    };
    ensure_document_size(&document)?;
    Ok(document)
}

pub fn import_session_document_to_root(
    document: &SessionTransferDocument,
    target_cwd: &str,
    root: PathBuf,
) -> Result<SessionTransferImportResult, SessionTransferError> {
    validate_cwd(target_cwd)?;
    let target_workspace = Path::new(target_cwd);
    if !target_workspace.is_absolute() || !target_workspace.is_dir() {
        return Err(SessionTransferError::Invalid(
            "the import working directory must be an existing absolute directory".to_string(),
        ));
    }
    let decoded_files = validate_and_decode_document(document)?;

    ensure_not_reparse_if_present(&root)?;
    std::fs::create_dir_all(&root)?;
    ensure_regular_directory(&root)?;
    let staging_parent = root.join("agentdesk-import-staging");
    ensure_not_reparse_if_present(&staging_parent)?;
    std::fs::create_dir_all(&staging_parent)?;
    ensure_regular_directory(&staging_parent)?;

    let operation_id = uuid::Uuid::now_v7().to_string();
    let operation_root = staging_parent.join(&operation_id);
    std::fs::create_dir(&operation_root)?;
    let result = import_in_staging(document, decoded_files, target_cwd, &root, &operation_root);
    let _ = std::fs::remove_dir_all(&operation_root);
    result
}

fn import_in_staging(
    document: &SessionTransferDocument,
    decoded_files: Vec<(String, Vec<u8>)>,
    target_cwd: &str,
    root: &Path,
    operation_root: &Path,
) -> Result<SessionTransferImportResult, SessionTransferError> {
    let source_info = Info {
        id: acp::SessionId::new(document.source_session_id.clone()),
        cwd: document.source_cwd.clone(),
    };
    let source_dir = operation_root
        .join("sessions")
        .join(encode_cwd_dirname(&source_info.cwd))
        .join(source_info.id.to_string());
    write_archive_files(&source_dir, decoded_files)?;

    let source_summary: Summary =
        serde_json::from_slice(&std::fs::read(source_dir.join("summary.json"))?)?;
    if source_summary.info.id != source_info.id || source_summary.info.cwd != source_info.cwd {
        return Err(SessionTransferError::Invalid(
            "the session summary identity does not match the transfer document".to_string(),
        ));
    }

    let new_session_id = choose_session_id(root, target_cwd)?;
    let target_info = Info {
        id: acp::SessionId::new(new_session_id.clone()),
        cwd: target_cwd.to_string(),
    };
    let staging_adapter = JsonlStorageAdapter::with_root(operation_root.to_path_buf());
    staging_adapter
        .copy_session_data_sync(
            &source_info,
            &target_info,
            CopySessionOptions {
                parent_session_id: Some(document.source_session_id.clone()),
                session_kind: Some("import".to_string()),
                copy_compaction_segments: true,
                ..CopySessionOptions::default()
            },
        )
        .map_err(map_storage_error)?;

    let staging_target = operation_root
        .join("sessions")
        .join(encode_cwd_dirname(target_cwd))
        .join(&new_session_id);
    copy_missing_files(&source_dir, &staging_target)?;
    validate_staged_target(&staging_target, &target_info, &document.source_session_id)?;

    let sessions_root = root.join("sessions");
    ensure_not_reparse_if_present(&sessions_root)?;
    std::fs::create_dir_all(&sessions_root)?;
    ensure_regular_directory(&sessions_root)?;
    let target_parent = sessions_root.join(encode_cwd_dirname(target_cwd));
    ensure_not_reparse_if_present(&target_parent)?;
    std::fs::create_dir_all(&target_parent)?;
    ensure_regular_directory(&target_parent)?;
    let final_target = target_parent.join(&new_session_id);
    if final_target.exists() {
        return Err(SessionTransferError::Invalid(
            "the generated session id already exists".to_string(),
        ));
    }
    std::fs::rename(&staging_target, &final_target)?;

    Ok(SessionTransferImportResult {
        session_id: new_session_id,
    })
}

fn validate_and_decode_document(
    document: &SessionTransferDocument,
) -> Result<Vec<(String, Vec<u8>)>, SessionTransferError> {
    ensure_document_size(document)?;
    if document.format != SESSION_TRANSFER_FORMAT {
        return Err(SessionTransferError::Invalid(
            "the session transfer format is unsupported".to_string(),
        ));
    }
    validate_session_id(&document.source_session_id)?;
    validate_cwd(&document.source_cwd)?;
    if document.files.is_empty() || document.files.len() > MAX_FILES {
        return Err(SessionTransferError::Invalid(
            "the session archive file count is invalid".to_string(),
        ));
    }

    let mut seen = HashSet::new();
    let mut total_bytes = 0usize;
    let mut decoded = Vec::with_capacity(document.files.len());
    for file in &document.files {
        validate_archive_path(&file.path)?;
        if !seen.insert(file.path.to_lowercase()) {
            return Err(SessionTransferError::Invalid(
                "the session archive contains duplicate paths".to_string(),
            ));
        }
        if file.data.len() > ((MAX_FILE_BYTES + 2) / 3) * 4 {
            return Err(SessionTransferError::Invalid(
                "a session archive file exceeds the encoded size limit".to_string(),
            ));
        }
        let bytes = base64::engine::general_purpose::STANDARD
            .decode(file.data.as_bytes())
            .map_err(|_| {
                SessionTransferError::Invalid(
                    "the session archive contains invalid base64 data".to_string(),
                )
            })?;
        if bytes.len() > MAX_FILE_BYTES {
            return Err(SessionTransferError::Invalid(
                "a session archive file exceeds the decoded size limit".to_string(),
            ));
        }
        total_bytes = total_bytes.checked_add(bytes.len()).ok_or_else(|| {
            SessionTransferError::Invalid("the session archive size overflowed".to_string())
        })?;
        if total_bytes > MAX_DECODED_BYTES {
            return Err(SessionTransferError::Invalid(
                "the session archive exceeds the decoded size limit".to_string(),
            ));
        }
        decoded.push((file.path.clone(), bytes));
    }
    if !seen.contains("summary.json") {
        return Err(SessionTransferError::Invalid(
            "the session archive is missing summary.json".to_string(),
        ));
    }
    Ok(decoded)
}

fn write_archive_files(
    session_dir: &Path,
    files: Vec<(String, Vec<u8>)>,
) -> Result<(), SessionTransferError> {
    std::fs::create_dir_all(session_dir)?;
    for (relative, bytes) in files {
        let destination = session_dir.join(relative.replace('/', std::path::MAIN_SEPARATOR_STR));
        if let Some(parent) = destination.parent() {
            std::fs::create_dir_all(parent)?;
        }
        let mut options = std::fs::OpenOptions::new();
        options.write(true).create_new(true);
        use std::io::Write as _;
        options.open(destination)?.write_all(&bytes)?;
    }
    Ok(())
}

fn copy_missing_files(source: &Path, target: &Path) -> Result<(), SessionTransferError> {
    let mut files = Vec::new();
    collect_directory_files(source, source, 0, &mut files)?;
    for file in files {
        let destination = target.join(file.name.replace('/', std::path::MAIN_SEPARATOR_STR));
        if destination.exists() {
            continue;
        }
        if let Some(parent) = destination.parent() {
            std::fs::create_dir_all(parent)?;
        }
        std::fs::write(destination, file.data)?;
    }
    Ok(())
}

fn validate_staged_target(
    target_dir: &Path,
    target_info: &Info,
    source_session_id: &str,
) -> Result<(), SessionTransferError> {
    let summary: Summary =
        serde_json::from_slice(&std::fs::read(target_dir.join("summary.json"))?)?;
    if summary.info.id.to_string() != target_info.id.to_string()
        || summary.info.cwd != target_info.cwd
    {
        return Err(SessionTransferError::Invalid(
            "the imported session summary identity is invalid".to_string(),
        ));
    }
    if summary.parent_session_id.as_deref() != Some(source_session_id) {
        return Err(SessionTransferError::Invalid(
            "the imported session lost its source lineage".to_string(),
        ));
    }

    validate_json_file_if_present::<crate::tools::todo::TodoState>(&target_dir.join("plan.json"))?;
    validate_json_file_if_present::<crate::session::plan_mode::PlanModeSnapshot>(
        &target_dir.join("plan_mode.json"),
    )?;
    validate_json_file_if_present::<crate::session::signals::SessionSignals>(
        &target_dir.join("signals.json"),
    )?;
    validate_json_file_if_present::<crate::session::announcement_state::AnnouncementState>(
        &target_dir.join("announcement_state.json"),
    )?;
    validate_json_file_if_present::<crate::session::goal_tracker::GoalOrchestration>(
        &target_dir.join("goal").join("state.json"),
    )?;
    validate_json_lines_if_present::<xai_grok_workspace::session::file_state::RewindPoint>(
        &target_dir.join("rewind_points.jsonl"),
    )?;
    Ok(())
}

fn validate_json_file_if_present<T: serde::de::DeserializeOwned>(
    path: &Path,
) -> Result<(), SessionTransferError> {
    if path.is_file() {
        let _: T = serde_json::from_slice(&std::fs::read(path)?)?;
    }
    Ok(())
}

fn validate_json_lines_if_present<T: serde::de::DeserializeOwned>(
    path: &Path,
) -> Result<(), SessionTransferError> {
    if !path.is_file() {
        return Ok(());
    }
    let bytes = std::fs::read(path)?;
    let text = std::str::from_utf8(&bytes).map_err(|_| {
        SessionTransferError::Invalid("a session JSONL file is not UTF-8".to_string())
    })?;
    for line in text.lines().filter(|line| !line.trim().is_empty()) {
        let _: T = serde_json::from_str(line)?;
    }
    Ok(())
}

fn choose_session_id(root: &Path, target_cwd: &str) -> Result<String, SessionTransferError> {
    let parent = root.join("sessions").join(encode_cwd_dirname(target_cwd));
    for _ in 0..16 {
        let candidate = uuid::Uuid::now_v7().to_string();
        if !parent.join(&candidate).exists() {
            return Ok(candidate);
        }
    }
    Err(SessionTransferError::Invalid(
        "could not allocate a unique session id".to_string(),
    ))
}

fn collect_directory_files(
    base: &Path,
    directory: &Path,
    depth: usize,
    files: &mut Vec<CopiedSessionFile>,
) -> Result<(), SessionTransferError> {
    if depth > MAX_DIRECTORY_DEPTH {
        return Err(SessionTransferError::Invalid(
            "the session directory nesting is too deep".to_string(),
        ));
    }
    ensure_regular_directory(directory)?;
    let mut entries = std::fs::read_dir(directory)?.collect::<Result<Vec<_>, _>>()?;
    entries.sort_by_key(|entry| entry.file_name());
    for entry in entries {
        let path = entry.path();
        let metadata = std::fs::symlink_metadata(&path)?;
        if is_reparse_or_symlink(&metadata) {
            return Err(SessionTransferError::Invalid(
                "the session directory contains a link or reparse point".to_string(),
            ));
        }
        if metadata.is_dir() {
            collect_directory_files(base, &path, depth + 1, files)?;
            continue;
        }
        if !metadata.is_file() {
            return Err(SessionTransferError::Invalid(
                "the session directory contains an unsupported file type".to_string(),
            ));
        }
        let relative = path.strip_prefix(base).map_err(|_| {
            SessionTransferError::Invalid("the session path escaped its base".to_string())
        })?;
        let name = relative_path_to_archive(relative)?;
        if should_skip_file(&name) {
            continue;
        }
        if files.len() >= MAX_FILES {
            return Err(SessionTransferError::Invalid(
                "the session directory contains too many files".to_string(),
            ));
        }
        if metadata.len() > MAX_FILE_BYTES as u64 {
            return Err(SessionTransferError::Invalid(
                "a session file exceeds the size limit".to_string(),
            ));
        }
        files.push(CopiedSessionFile {
            name,
            data: std::fs::read(path)?,
        });
    }
    Ok(())
}

fn relative_path_to_archive(path: &Path) -> Result<String, SessionTransferError> {
    let mut components = Vec::new();
    for component in path.components() {
        let Component::Normal(value) = component else {
            return Err(SessionTransferError::Invalid(
                "the session path is not relative".to_string(),
            ));
        };
        let value = value.to_str().ok_or_else(|| {
            SessionTransferError::Invalid("the session path is not UTF-8".to_string())
        })?;
        components.push(value);
    }
    let result = components.join("/");
    validate_archive_path(&result)?;
    Ok(result)
}

fn validate_archive_path(path: &str) -> Result<(), SessionTransferError> {
    if path.is_empty()
        || path.len() > MAX_PATH_BYTES
        || path.starts_with('/')
        || path.ends_with('/')
        || path.contains('\\')
        || path.contains("//")
    {
        return Err(SessionTransferError::Invalid(
            "the session archive contains an invalid path".to_string(),
        ));
    }
    let parsed = Path::new(path);
    if parsed.is_absolute()
        || parsed
            .components()
            .any(|component| !matches!(component, Component::Normal(_)))
    {
        return Err(SessionTransferError::Invalid(
            "the session archive path is not relative".to_string(),
        ));
    }
    for component in path.split('/') {
        validate_windows_component(component)?;
    }
    Ok(())
}

fn validate_windows_component(component: &str) -> Result<(), SessionTransferError> {
    if component.is_empty()
        || component == "."
        || component == ".."
        || component.chars().count() > MAX_COMPONENT_CHARS
        || component.ends_with([' ', '.'])
        || component.chars().any(|value| {
            value.is_control() || matches!(value, '<' | '>' | ':' | '"' | '|' | '?' | '*')
        })
    {
        return Err(SessionTransferError::Invalid(
            "the session archive contains an unsafe Windows path component".to_string(),
        ));
    }
    let stem = component
        .split('.')
        .next()
        .unwrap_or(component)
        .to_ascii_uppercase();
    let reserved = matches!(stem.as_str(), "CON" | "PRN" | "AUX" | "NUL")
        || stem.strip_prefix("COM").is_some_and(|suffix| {
            matches!(suffix, "1" | "2" | "3" | "4" | "5" | "6" | "7" | "8" | "9")
        })
        || stem.strip_prefix("LPT").is_some_and(|suffix| {
            matches!(suffix, "1" | "2" | "3" | "4" | "5" | "6" | "7" | "8" | "9")
        });
    if reserved {
        return Err(SessionTransferError::Invalid(
            "the session archive contains a reserved Windows path".to_string(),
        ));
    }
    Ok(())
}

fn ensure_document_size(document: &SessionTransferDocument) -> Result<(), SessionTransferError> {
    let mut estimated = 256usize
        .checked_add(document.format.len())
        .and_then(|value| value.checked_add(document.source_session_id.len()))
        .and_then(|value| value.checked_add(document.source_cwd.len()))
        .ok_or_else(|| {
            SessionTransferError::Invalid("the session document size overflowed".to_string())
        })?;
    for file in &document.files {
        estimated = estimated
            .checked_add(32)
            .and_then(|value| value.checked_add(file.path.len()))
            .and_then(|value| value.checked_add(file.data.len()))
            .ok_or_else(|| {
                SessionTransferError::Invalid("the session document size overflowed".to_string())
            })?;
        if estimated > MAX_SESSION_DOCUMENT_BYTES {
            return Err(SessionTransferError::Invalid(
                "the session transfer document exceeds the size limit".to_string(),
            ));
        }
    }
    let size = serde_json::to_vec(document)?.len();
    if size == 0 || size > MAX_SESSION_DOCUMENT_BYTES {
        return Err(SessionTransferError::Invalid(
            "the session transfer document exceeds the size limit".to_string(),
        ));
    }
    Ok(())
}

fn validate_identifier(
    value: &str,
    maximum_chars: usize,
    label: &str,
) -> Result<(), SessionTransferError> {
    if value.is_empty()
        || value.chars().count() > maximum_chars
        || value.chars().any(char::is_control)
    {
        return Err(SessionTransferError::Invalid(format!(
            "the {label} is invalid"
        )));
    }
    Ok(())
}

pub fn validate_session_id(value: &str) -> Result<(), SessionTransferError> {
    validate_identifier(value, MAX_SESSION_ID_CHARS, "session id")?;
    if !value
        .chars()
        .all(|character| character.is_ascii_alphanumeric() || matches!(character, '-' | '_'))
    {
        return Err(SessionTransferError::Invalid(
            "the session id contains unsafe characters".to_string(),
        ));
    }
    validate_windows_component(value)?;
    Ok(())
}

fn validate_cwd(value: &str) -> Result<(), SessionTransferError> {
    validate_identifier(value, MAX_CWD_CHARS, "session working directory")
}

fn should_skip_file(path: &str) -> bool {
    path == "summary.json.lock"
        || path.ends_with(".lock")
        || path == "mcp_stderr"
        || path.starts_with("mcp_stderr/")
}

fn ensure_regular_directory(path: &Path) -> Result<(), SessionTransferError> {
    ensure_no_reparse_ancestors(path)?;
    let metadata = std::fs::symlink_metadata(path)?;
    if !metadata.is_dir() || is_reparse_or_symlink(&metadata) {
        return Err(SessionTransferError::Invalid(
            "a session transfer directory is not a regular directory".to_string(),
        ));
    }
    Ok(())
}

fn ensure_not_reparse_if_present(path: &Path) -> Result<(), SessionTransferError> {
    ensure_no_reparse_ancestors(path)?;
    match std::fs::symlink_metadata(path) {
        Ok(metadata) if is_reparse_or_symlink(&metadata) => Err(SessionTransferError::Invalid(
            "a session transfer path is a link or reparse point".to_string(),
        )),
        Ok(metadata) if !metadata.is_dir() => Err(SessionTransferError::Invalid(
            "a session transfer path is not a directory".to_string(),
        )),
        Ok(_) => Ok(()),
        Err(error) if error.kind() == io::ErrorKind::NotFound => Ok(()),
        Err(error) => Err(error.into()),
    }
}

fn ensure_no_reparse_ancestors(path: &Path) -> Result<(), SessionTransferError> {
    for ancestor in path.ancestors() {
        match std::fs::symlink_metadata(ancestor) {
            Ok(metadata) if is_reparse_or_symlink(&metadata) => {
                return Err(SessionTransferError::Invalid(
                    "a session transfer path has a link or reparse point ancestor".to_string(),
                ));
            }
            Ok(_) => {}
            Err(error) if error.kind() == io::ErrorKind::NotFound => {}
            Err(error) => return Err(error.into()),
        }
    }
    Ok(())
}

#[cfg(windows)]
fn is_reparse_or_symlink(metadata: &std::fs::Metadata) -> bool {
    use std::os::windows::fs::MetadataExt as _;
    const FILE_ATTRIBUTE_REPARSE_POINT: u32 = 0x400;
    metadata.file_type().is_symlink()
        || metadata.file_attributes() & FILE_ATTRIBUTE_REPARSE_POINT != 0
}

#[cfg(not(windows))]
fn is_reparse_or_symlink(metadata: &std::fs::Metadata) -> bool {
    metadata.file_type().is_symlink()
}

fn map_storage_error(error: io::Error) -> SessionTransferError {
    if error.kind() == io::ErrorKind::InvalidData {
        SessionTransferError::Invalid("the persisted session data is invalid".to_string())
    } else {
        SessionTransferError::Io(error)
    }
}
