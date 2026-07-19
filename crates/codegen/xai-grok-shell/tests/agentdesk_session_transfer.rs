use base64::Engine as _;
use xai_grok_shell::extensions::agentdesk_session::{
    SessionTransferDocument, SessionTransferFile, export_session_document_from_directory,
    import_session_document_to_root, validate_session_id,
};

fn write_source_session(root: &std::path::Path, session_id: &str, cwd: &str) -> std::path::PathBuf {
    let session_dir = root.join("source-session");
    std::fs::create_dir_all(session_dir.join("assets")).unwrap();
    let summary = serde_json::json!({
        "info": { "id": session_id, "cwd": cwd },
        "session_summary": "Portable session",
        "created_at": "2026-07-17T00:00:00Z",
        "updated_at": "2026-07-17T00:00:00Z",
        "num_messages": 0,
        "num_chat_messages": 0,
        "current_model_id": "grok-4.5",
        "next_trace_turn": 0,
        "chat_format_version": 1,
        "git_remotes": []
    });
    std::fs::write(
        session_dir.join("summary.json"),
        serde_json::to_vec_pretty(&summary).unwrap(),
    )
    .unwrap();
    std::fs::write(session_dir.join("chat_history.jsonl"), b"").unwrap();
    std::fs::write(session_dir.join("updates.jsonl"), b"").unwrap();
    std::fs::write(session_dir.join("plan.json"), b"{\"todos\":{}}").unwrap();
    std::fs::write(session_dir.join("rewind_points.jsonl"), b"").unwrap();
    std::fs::write(
        session_dir.join("assets").join("image.bin"),
        b"session-asset",
    )
    .unwrap();
    std::fs::write(session_dir.join("summary.json.lock"), b"ephemeral").unwrap();
    session_dir
}

#[test]
fn export_import_round_trip_preserves_state_and_rewrites_identity() {
    let temp = tempfile::tempdir().unwrap();
    let source_id = "019f8a11-1111-7111-8111-111111111111";
    let source_cwd = temp.path().join("source-workspace");
    let target_cwd = temp.path().join("target-workspace");
    std::fs::create_dir_all(&source_cwd).unwrap();
    std::fs::create_dir_all(&target_cwd).unwrap();
    let session_dir = write_source_session(temp.path(), source_id, source_cwd.to_str().unwrap());

    let document = export_session_document_from_directory(source_id, &session_dir).unwrap();

    assert_eq!(document.source_session_id, source_id);
    assert_eq!(document.source_cwd, source_cwd.to_str().unwrap());
    assert!(document.files.iter().any(|file| file.path == "plan.json"));
    assert!(
        document
            .files
            .iter()
            .any(|file| file.path == "rewind_points.jsonl")
    );
    assert!(
        document
            .files
            .iter()
            .any(|file| file.path == "assets/image.bin")
    );
    assert!(
        !document
            .files
            .iter()
            .any(|file| file.path.ends_with(".lock"))
    );

    let result = import_session_document_to_root(
        &document,
        target_cwd.to_str().unwrap(),
        temp.path().join("grok-home"),
    )
    .unwrap();
    assert_ne!(result.session_id, source_id);

    let target_dir = temp
        .path()
        .join("grok-home")
        .join("sessions")
        .join(xai_grok_shell::util::grok_home::encode_cwd_dirname(
            target_cwd.to_str().unwrap(),
        ))
        .join(&result.session_id);
    let summary: serde_json::Value =
        serde_json::from_slice(&std::fs::read(target_dir.join("summary.json")).unwrap()).unwrap();
    assert_eq!(summary["info"]["id"], result.session_id);
    assert_eq!(summary["info"]["cwd"], target_cwd.to_str().unwrap());
    assert_eq!(summary["parent_session_id"], source_id);
    assert!(target_dir.join("plan.json").is_file());
    assert!(target_dir.join("rewind_points.jsonl").is_file());
    assert_eq!(
        std::fs::read(target_dir.join("assets").join("image.bin")).unwrap(),
        b"session-asset"
    );
}

#[test]
fn import_rejects_unsafe_or_ambiguous_archive_paths_without_writing_a_session() {
    let temp = tempfile::tempdir().unwrap();
    let target_cwd = temp.path().join("target-workspace");
    std::fs::create_dir_all(&target_cwd).unwrap();
    let encoded = base64::engine::general_purpose::STANDARD.encode(b"{}");

    for session_id in [
        "../escaped",
        "..\\escaped",
        "C:escaped",
        "session/name",
        "CON",
        "NUL.txt",
        "COM1",
        "LPT9.log",
    ] {
        assert!(validate_session_id(session_id).is_err());
    }

    for paths in [
        vec!["../summary.json"],
        vec!["C:/summary.json"],
        vec!["aux.txt"],
        vec!["summary.json", "SUMMARY.JSON"],
        vec!["folder\\summary.json"],
        vec!["folder./summary.json"],
    ] {
        let document = SessionTransferDocument {
            format: "agentdesk.session.v1".to_string(),
            source_session_id: "019f8a11-1111-7111-8111-111111111111".to_string(),
            source_cwd: "C:\\source".to_string(),
            files: paths
                .into_iter()
                .map(|path| SessionTransferFile {
                    path: path.to_string(),
                    data: encoded.clone(),
                })
                .collect(),
        };

        let result = import_session_document_to_root(
            &document,
            target_cwd.to_str().unwrap(),
            temp.path().join("grok-home"),
        );
        assert!(result.is_err(), "unsafe archive path was accepted");
    }

    let unsafe_session_id = SessionTransferDocument {
        format: "agentdesk.session.v1".to_string(),
        source_session_id: "../../escaped".to_string(),
        source_cwd: "C:\\source".to_string(),
        files: vec![SessionTransferFile {
            path: "summary.json".to_string(),
            data: encoded,
        }],
    };
    assert!(
        import_session_document_to_root(
            &unsafe_session_id,
            target_cwd.to_str().unwrap(),
            temp.path().join("grok-home"),
        )
        .is_err(),
        "unsafe source session id was accepted"
    );

    let sessions = temp.path().join("grok-home").join("sessions");
    assert!(
        !sessions.exists()
            || std::fs::read_dir(sessions)
                .unwrap()
                .all(|entry| std::fs::read_dir(entry.unwrap().path())
                    .unwrap()
                    .next()
                    .is_none())
    );
}

#[cfg(windows)]
#[test]
fn import_rejects_a_root_beneath_a_junction_ancestor() {
    let temp = tempfile::tempdir().unwrap();
    let source_id = "019f8a11-1111-7111-8111-111111111111";
    let source_cwd = temp.path().join("source-workspace");
    let target_cwd = temp.path().join("target-workspace");
    std::fs::create_dir_all(&source_cwd).unwrap();
    std::fs::create_dir_all(&target_cwd).unwrap();
    let session_dir = write_source_session(temp.path(), source_id, source_cwd.to_str().unwrap());
    let document = export_session_document_from_directory(source_id, &session_dir).unwrap();

    let outside = temp.path().join("outside");
    let junction = temp.path().join("junction");
    std::fs::create_dir(&outside).unwrap();
    let status = std::process::Command::new("cmd")
        .args([
            "/C",
            "mklink",
            "/J",
            junction.to_str().unwrap(),
            outside.to_str().unwrap(),
        ])
        .status()
        .unwrap();
    assert!(status.success(), "failed to create the test junction");

    let result = import_session_document_to_root(
        &document,
        target_cwd.to_str().unwrap(),
        junction.join("grok-home"),
    );
    let wrote_outside = outside.join("grok-home").exists();
    std::fs::remove_dir(&junction).unwrap();

    assert!(result.is_err(), "a junction ancestor was accepted");
    assert!(
        !wrote_outside,
        "the import wrote through a junction ancestor"
    );
}
