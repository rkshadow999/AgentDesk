// Modified by the AgentDesk project for Windows desktop integration and safety support.
//! Versioned AgentDesk Memory browsing and mutation contracts.

use std::path::Path;

use agent_client_protocol as acp;
use serde::{Deserialize, Serialize};
use xai_grok_memory::{
    BrowsableMemoryDocument, BrowsableMemoryFile, BrowsableMemoryScope, BrowsableMemoryTarget,
    MAX_BROWSABLE_MEMORY_BYTES, MemoryStorage,
};

use super::{ExtResult, parse_params, to_raw_response};
use crate::agent::MvpAgent;

pub const MEMORY_SCHEMA_VERSION: u64 = 1;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum MemoryFileScope {
    Global,
    Workspace,
    Session,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct MemoryFileResponse {
    pub id: String,
    pub scope: MemoryFileScope,
    pub name: String,
    pub byte_len: u64,
    pub modified_at: Option<String>,
    pub writable: bool,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct MemoryListResponse {
    pub schema_version: u64,
    pub files: Vec<MemoryFileResponse>,
    pub truncated: bool,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct MemoryReadResponse {
    pub schema_version: u64,
    pub file: MemoryFileResponse,
    pub content: String,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum MemoryMutationStatus {
    ConfirmationRequired,
    Success,
    NotFound,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct MemoryMutationResponse {
    pub schema_version: u64,
    pub status: MemoryMutationStatus,
    pub message: String,
    pub file: Option<MemoryFileResponse>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct SessionRequest {
    session_id: String,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct FileRequest {
    session_id: String,
    file_id: String,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct WriteRequest {
    session_id: String,
    file_id: String,
    content: String,
    confirmed: bool,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct DeleteRequest {
    session_id: String,
    file_id: String,
    confirmed: bool,
}

pub async fn handle(agent: &MvpAgent, args: &acp::ExtRequest) -> ExtResult {
    match args.method.as_ref() {
        "agentdesk/v1/memory/list" => {
            let request: SessionRequest = parse_params(args)?;
            let storage = storage_for_session(agent, &request.session_id)?;
            let response = tokio::task::spawn_blocking(move || list_for_storage(&storage))
                .await
                .map_err(|_| memory_worker_error())?
                .map_err(map_memory_error)?;
            to_raw_response(&response)
        }
        "agentdesk/v1/memory/read" => {
            let request: FileRequest = parse_params(args)?;
            let storage = storage_for_session(agent, &request.session_id)?;
            let response =
                tokio::task::spawn_blocking(move || read_for_storage(&storage, &request.file_id))
                    .await
                    .map_err(|_| memory_worker_error())?
                    .map_err(map_memory_error)?;
            to_raw_response(&response)
        }
        "agentdesk/v1/memory/write" => {
            let request: WriteRequest = parse_params(args)?;
            let storage = storage_for_session(agent, &request.session_id)?;
            let response = tokio::task::spawn_blocking(move || {
                write_for_storage(
                    &storage,
                    &request.file_id,
                    &request.content,
                    request.confirmed,
                )
            })
            .await
            .map_err(|_| memory_worker_error())?
            .map_err(map_memory_error)?;
            to_raw_response(&response)
        }
        "agentdesk/v1/memory/delete" => {
            let request: DeleteRequest = parse_params(args)?;
            let storage = storage_for_session(agent, &request.session_id)?;
            let response = tokio::task::spawn_blocking(move || {
                delete_for_storage(&storage, &request.file_id, request.confirmed)
            })
            .await
            .map_err(|_| memory_worker_error())?
            .map_err(map_memory_error)?;
            to_raw_response(&response)
        }
        _ => Err(acp::Error::method_not_found()),
    }
}

fn storage_for_session(agent: &MvpAgent, session_id: &str) -> Result<MemoryStorage, acp::Error> {
    crate::extensions::agentdesk_session::validate_session_id(session_id)
        .map_err(|error| acp::Error::invalid_params().data(error.to_string()))?;
    let session_id = acp::SessionId::new(session_id.to_string());
    let cwd = {
        let sessions = agent.sessions.borrow();
        sessions
            .get(&session_id)
            .map(|session| session.info.cwd.clone())
    }
    .ok_or_else(|| acp::Error::invalid_params().data("the session was not found"))?;
    Ok(MemoryStorage::new(Path::new(&cwd), None))
}

fn list_for_storage(storage: &MemoryStorage) -> std::io::Result<MemoryListResponse> {
    let listing = storage.list_browsable_files()?;
    Ok(MemoryListResponse {
        schema_version: MEMORY_SCHEMA_VERSION,
        files: listing.files.into_iter().map(map_file).collect(),
        truncated: listing.truncated,
    })
}

fn read_for_storage(storage: &MemoryStorage, file_id: &str) -> std::io::Result<MemoryReadResponse> {
    let target = BrowsableMemoryTarget::parse_id(file_id)?;
    let BrowsableMemoryDocument { file, content } = storage.read_browsable_file(&target)?;
    Ok(MemoryReadResponse {
        schema_version: MEMORY_SCHEMA_VERSION,
        file: map_file(file),
        content,
    })
}

fn write_for_storage(
    storage: &MemoryStorage,
    file_id: &str,
    content: &str,
    confirmed: bool,
) -> std::io::Result<MemoryMutationResponse> {
    let target = BrowsableMemoryTarget::parse_id(file_id)?;
    if content.len() > MAX_BROWSABLE_MEMORY_BYTES {
        return Err(std::io::Error::new(
            std::io::ErrorKind::InvalidInput,
            "memory content exceeds the write limit",
        ));
    }
    if !confirmed {
        return Ok(confirmation_required(
            "Writing memory requires explicit confirmation.",
        ));
    }
    match storage.write_browsable_file(&target, content) {
        Ok(file) => Ok(MemoryMutationResponse {
            schema_version: MEMORY_SCHEMA_VERSION,
            status: MemoryMutationStatus::Success,
            message: "Memory file updated.".to_string(),
            file: Some(map_file(file)),
        }),
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => Ok(not_found()),
        Err(error) => Err(error),
    }
}

fn delete_for_storage(
    storage: &MemoryStorage,
    file_id: &str,
    confirmed: bool,
) -> std::io::Result<MemoryMutationResponse> {
    let target = BrowsableMemoryTarget::parse_id(file_id)?;
    if !confirmed {
        return Ok(confirmation_required(
            "Deleting memory requires explicit confirmation.",
        ));
    }
    if storage.delete_browsable_file(&target)? {
        Ok(MemoryMutationResponse {
            schema_version: MEMORY_SCHEMA_VERSION,
            status: MemoryMutationStatus::Success,
            message: "Memory file deleted.".to_string(),
            file: None,
        })
    } else {
        Ok(not_found())
    }
}

fn confirmation_required(message: &str) -> MemoryMutationResponse {
    MemoryMutationResponse {
        schema_version: MEMORY_SCHEMA_VERSION,
        status: MemoryMutationStatus::ConfirmationRequired,
        message: message.to_string(),
        file: None,
    }
}

fn not_found() -> MemoryMutationResponse {
    MemoryMutationResponse {
        schema_version: MEMORY_SCHEMA_VERSION,
        status: MemoryMutationStatus::NotFound,
        message: "Memory file not found.".to_string(),
        file: None,
    }
}

fn map_file(file: BrowsableMemoryFile) -> MemoryFileResponse {
    let scope = match file.scope {
        BrowsableMemoryScope::Global => MemoryFileScope::Global,
        BrowsableMemoryScope::Workspace => MemoryFileScope::Workspace,
        BrowsableMemoryScope::Session => MemoryFileScope::Session,
    };
    MemoryFileResponse {
        id: file.id,
        scope,
        name: file.name,
        byte_len: file.byte_len,
        modified_at: file.modified.map(|modified| {
            let timestamp: chrono::DateTime<chrono::Utc> = modified.into();
            timestamp.to_rfc3339_opts(chrono::SecondsFormat::Millis, true)
        }),
        writable: file.writable,
    }
}

fn memory_worker_error() -> acp::Error {
    acp::Error::internal_error().data("the memory worker failed")
}

fn map_memory_error(error: std::io::Error) -> acp::Error {
    match error.kind() {
        std::io::ErrorKind::InvalidInput => {
            acp::Error::invalid_params().data("the memory request was invalid")
        }
        std::io::ErrorKind::NotFound => {
            acp::Error::invalid_params().data("the memory file was not found")
        }
        std::io::ErrorKind::PermissionDenied => {
            acp::Error::invalid_params().data("the memory target was not accessible")
        }
        std::io::ErrorKind::InvalidData => {
            acp::Error::invalid_params().data("the memory file exceeded its content boundary")
        }
        _ => acp::Error::internal_error().data("the memory operation failed"),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use xai_grok_memory::{MemoryScope, MemoryStorage};

    fn storage() -> (tempfile::TempDir, MemoryStorage) {
        let root = tempfile::tempdir().unwrap();
        let global = root.path().join("memory");
        let workspace = global.join("workspace");
        let storage = MemoryStorage::with_paths(global, workspace);
        (root, storage)
    }

    #[test]
    fn list_response_exposes_bounded_metadata_without_paths() {
        let (_root, storage) = storage();
        storage
            .write_long_term(MemoryScope::Global, "# Global")
            .unwrap();
        storage
            .write_long_term(MemoryScope::Workspace, "# Workspace")
            .unwrap();

        let response = list_for_storage(&storage).unwrap();
        let json = serde_json::to_value(&response).unwrap();

        assert_eq!(json["schemaVersion"], MEMORY_SCHEMA_VERSION);
        assert_eq!(json["files"].as_array().unwrap().len(), 2);
        assert_eq!(json["files"][0]["id"], "global");
        assert_eq!(json["files"][0]["scope"], "global");
        assert!(json["files"][0].get("path").is_none());
        assert!(json.to_string().len() < 16 * 1024);
    }

    #[test]
    fn read_response_returns_utf8_content_and_strict_file_metadata() {
        let (_root, storage) = storage();
        storage
            .write_long_term(MemoryScope::Workspace, "# 项目记忆\n\nAgentDesk")
            .unwrap();

        let response = read_for_storage(&storage, "workspace").unwrap();
        let json = serde_json::to_value(&response).unwrap();

        assert_eq!(json["schemaVersion"], MEMORY_SCHEMA_VERSION);
        assert_eq!(json["file"]["id"], "workspace");
        assert_eq!(json["content"], "# 项目记忆\n\nAgentDesk");
        assert!(json["file"].get("path").is_none());
    }

    #[test]
    fn write_requires_confirmation_before_mutating() {
        let (_root, storage) = storage();
        storage
            .write_long_term(MemoryScope::Workspace, "old")
            .unwrap();

        let pending = write_for_storage(&storage, "workspace", "new", false).unwrap();

        assert_eq!(pending.status, MemoryMutationStatus::ConfirmationRequired);
        assert_eq!(
            std::fs::read_to_string(storage.workspace_memory_file()).unwrap(),
            "old"
        );

        let applied = write_for_storage(&storage, "workspace", "new", true).unwrap();
        assert_eq!(applied.status, MemoryMutationStatus::Success);
        assert_eq!(
            std::fs::read_to_string(storage.workspace_memory_file()).unwrap(),
            "new"
        );
    }

    #[test]
    fn delete_requires_confirmation_and_reports_missing_files() {
        let (_root, storage) = storage();
        storage.write_long_term(MemoryScope::Global, "old").unwrap();

        let pending = delete_for_storage(&storage, "global", false).unwrap();
        assert_eq!(pending.status, MemoryMutationStatus::ConfirmationRequired);
        assert!(storage.global_memory_file().exists());

        let deleted = delete_for_storage(&storage, "global", true).unwrap();
        assert_eq!(deleted.status, MemoryMutationStatus::Success);
        assert!(!storage.global_memory_file().exists());

        let missing = delete_for_storage(&storage, "global", true).unwrap();
        assert_eq!(missing.status, MemoryMutationStatus::NotFound);
    }
}
