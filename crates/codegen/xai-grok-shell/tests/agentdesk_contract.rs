use xai_grok_shell::agent::auth_method::{
    LEGACY_XAI_API_KEY_ENV_VAR, XAI_API_KEY_ENV_VAR, capture_desktop_api_key_before_runtime,
    read_xai_api_key_env,
};
use xai_grok_shell::agent::config::{Config, ConfigModelOverride};
use xai_grok_shell::extensions::agentdesk::OpenAiCompatibleProfile;
use xai_grok_shell::sampling::ApiBackend;
use xai_grok_shell::tools::incremental_bash::output_delta;
use xai_grok_shell::util::config::{
    disable_remote_fetch_for_process, resolve_remote_fetch_enabled,
};
use xai_grok_shell::{auth, extensions};
use xai_grok_test_support::{GrokStdioClient, MockInferenceServer, git_workdir};

const READ_ONLY_MARKETPLACE_NAME: &str = "AgentDesk Read-Only Fixture";

fn write_read_only_marketplace_fixture(home: &std::path::Path) {
    let marketplace = home.join("marketplace");
    let skill = marketplace.join("default-skills").join("fixture-skill");
    std::fs::create_dir_all(&skill).unwrap();
    std::fs::write(
        skill.join("SKILL.md"),
        "---\nname: fixture-skill\ndescription: read-only marketplace fixture\n---\n",
    )
    .unwrap();

    let grok_home = home.join(".grok");
    std::fs::create_dir_all(&grok_home).unwrap();
    let path = serde_json::to_string(marketplace.to_str().unwrap()).unwrap();
    std::fs::write(
        grok_home.join("config.toml"),
        format!(
            "[marketplace]\nofficial_marketplace_auto_installed = true\n\n\
             [[marketplace.sources]]\nname = {READ_ONLY_MARKETPLACE_NAME:?}\npath = {path}\n"
        ),
    )
    .unwrap();
}

fn assert_marketplace_install_registry_empty(home: &std::path::Path, operation: &str) {
    let registry = home
        .join(".grok")
        .join("installed-plugins")
        .join("registry.json");
    if !registry.exists() {
        return;
    }

    let payload: serde_json::Value =
        serde_json::from_slice(&std::fs::read(&registry).unwrap()).unwrap();
    let installed = payload["repos"].as_object().map_or(0, serde_json::Map::len);
    assert_eq!(
        installed, 0,
        "marketplace {operation} installed code through a read-only endpoint: {payload}"
    );
}

fn assert_plugins_do_not_include_marketplace_fixture(
    response: &agent_client_protocol::ExtResponse,
    operation: &str,
) {
    let payload: serde_json::Value = serde_json::from_str(response.0.get()).unwrap();
    let plugins = payload["result"]["plugins"]
        .as_array()
        .unwrap_or_else(|| panic!("plugins/list returned an invalid payload: {payload}"));
    assert!(
        plugins.iter().all(|plugin| {
            plugin["name"] != "default-skills"
                && plugin["marketplaceSource"] != READ_ONLY_MARKETPLACE_NAME
        }),
        "marketplace {operation} reloaded auto-installed code into the session: {payload}"
    );
}

fn assert_marketplace_default_skills_installed(home: &std::path::Path) {
    let registry = home
        .join(".grok")
        .join("installed-plugins")
        .join("registry.json");
    let payload: serde_json::Value =
        serde_json::from_slice(&std::fs::read(&registry).expect("read install registry"))
            .expect("parse install registry");
    assert_eq!(
        payload["repos"].as_object().map_or(0, serde_json::Map::len),
        1,
        "non-AgentDesk clients must preserve the upstream default-skills install behavior: {payload}"
    );
}

fn assert_plugins_include_marketplace_fixture(response: &agent_client_protocol::ExtResponse) {
    let payload: serde_json::Value = serde_json::from_str(response.0.get()).unwrap();
    let plugins = payload["result"]["plugins"]
        .as_array()
        .unwrap_or_else(|| panic!("plugins/list returned an invalid payload: {payload}"));
    assert!(
        plugins.iter().any(|plugin| {
            plugin["name"] == "default-skills"
                || plugin["marketplaceSource"] == READ_ONLY_MARKETPLACE_NAME
        }),
        "the upstream marketplace list behavior did not reload default-skills: {payload}"
    );
}

async fn initialize_agentdesk_extensions(client: &GrokStdioClient) {
    client
        .ext_method(
            "agentdesk/v1/initialize",
            serde_json::json!({
                "protocolVersion": extensions::agentdesk::PROTOCOL_VERSION,
                "client": { "name": "agentdesk", "version": "0.0.0-test" }
            }),
        )
        .await
        .unwrap_or_else(|error| {
            panic!(
                "agentdesk/v1/initialize failed: {error:?}\nstderr:\n{}",
                client.stderr()
            )
        });
}

#[test]
fn openai_compatible_profile_normalizes_model_and_base_url() {
    let profile = OpenAiCompatibleProfile::new("  grok-4.5  ", "HTTPS://Example.COM:443/proxy/v1/")
        .expect("valid profile");

    assert_eq!(profile.model(), "grok-4.5");
    assert_eq!(profile.base_url(), "https://example.com/proxy/v1");
}

#[test]
fn openai_compatible_profile_rejects_unsafe_or_ambiguous_inputs() {
    for (model, base_url) in [
        ("", "https://example.com/v1"),
        ("bad\nmodel", "https://example.com/v1"),
        ("grok-4.5", "ftp://example.com/v1"),
        ("grok-4.5", "https://user:secret@example.com/v1"),
        ("grok-4.5", "https://example.com/v1?tenant=secret"),
        ("grok-4.5", "https://example.com/v1#fragment"),
        ("grok-4.5", "not-a-url"),
    ] {
        assert!(
            OpenAiCompatibleProfile::new(model, base_url).is_err(),
            "profile unexpectedly accepted model={model:?}, base_url={base_url:?}"
        );
    }
}

#[test]
fn openai_compatible_profile_overrides_model_without_persisted_credentials() {
    let profile =
        OpenAiCompatibleProfile::new("grok-4.5", "https://example.com/v1/").expect("valid profile");
    let mut config = Config::default();
    config.config_models.insert(
        "grok-4.5".to_owned(),
        ConfigModelOverride {
            api_key: Some("must-be-cleared".to_owned()),
            api_base_url: Some("https://stale.example/v1".to_owned()),
            ..ConfigModelOverride::default()
        },
    );

    profile.apply(&mut config);

    let model = config
        .config_models
        .get("grok-4.5")
        .expect("profile model override");
    assert_eq!(config.default_model_override.as_deref(), Some("grok-4.5"));
    assert_eq!(config.endpoints.xai_api_base_url, "https://example.com/v1");
    assert_eq!(model.model.as_deref(), Some("grok-4.5"));
    assert_eq!(model.base_url.as_deref(), Some("https://example.com/v1"));
    assert_eq!(model.api_backend, Some(ApiBackend::ChatCompletions));
    assert_eq!(model.supported_in_api, Some(true));
    assert!(model.api_key.is_none());
    assert!(model.env_key.is_none());
    assert!(model.api_base_url.is_none());
}

#[test]
fn openai_compatible_profile_applies_responses_backend() {
    let profile = OpenAiCompatibleProfile::new_with_backend(
        "grok-4.5",
        "https://example.com/v1/",
        "responses",
    )
    .expect("valid Responses profile");
    let mut config = Config::default();

    profile.apply(&mut config);

    let model = config
        .config_models
        .get("grok-4.5")
        .expect("profile model override");
    assert_eq!(profile.api_backend(), &ApiBackend::Responses);
    assert_eq!(model.api_backend, Some(ApiBackend::Responses));
    assert!(model.api_key.is_none());
    assert!(model.env_key.is_none());
    assert!(model.api_base_url.is_none());
}

#[test]
fn openai_compatible_profile_rejects_non_openai_backends() {
    let error =
        OpenAiCompatibleProfile::new_with_backend("grok-4.5", "https://example.com/v1", "messages")
            .expect_err("AgentDesk must reject unsupported provider backends");

    assert!(!error.to_string().contains("https://example.com"));
}

#[test]
fn agentdesk_process_switch_disables_remote_fetch() {
    disable_remote_fetch_for_process();
    assert!(!resolve_remote_fetch_enabled());
}

#[test]
fn incremental_bash_delta_tracks_monotonic_bytes_across_retained_tail_truncation() {
    let (first, cursor) = output_delta(0, b"abcdefghij", 10);
    assert_eq!(first, b"abcdefghij");

    let (shrunk, cursor) = output_delta(cursor, b"fghijk", 11);
    assert_eq!(shrunk, b"k");

    let (gap, cursor) = output_delta(cursor, b"yz", 20);
    assert_eq!(
        gap,
        b"\n\n... (output truncated; 7 bytes unavailable) ...\n\nyz"
    );

    let (duplicate, duplicate_cursor) = output_delta(cursor, b"different", 20);
    assert!(duplicate.is_empty());
    assert_eq!(duplicate_cursor, cursor);

    let (regressed, regressed_cursor) = output_delta(cursor, b"older", 19);
    assert!(regressed.is_empty());
    assert_eq!(regressed_cursor, cursor);

    let (next, next_cursor) = output_delta(regressed_cursor, b"z", 21);
    assert_eq!(next, b"z");
    assert_eq!(next_cursor, 21);
}

#[test]
fn incremental_bash_delta_slices_multibyte_output_by_raw_bytes() {
    let (first, cursor) = output_delta(0, b"a\xE4\xBD", 3);
    assert_eq!(first, b"a\xE4\xBD");

    let (second, cursor) = output_delta(cursor, b"\xE4\xBD\xA0", 4);
    assert_eq!(second, b"\xA0");
    assert_eq!(cursor, 4);

    let mut combined = first;
    combined.extend_from_slice(&second);
    assert_eq!(String::from_utf8(combined).unwrap(), "a你");
}

#[tokio::test(flavor = "current_thread")]
async fn negotiated_agentdesk_identity_keeps_marketplace_list_read_only() {
    tokio::task::LocalSet::new()
        .run_until(async {
            let server = MockInferenceServer::start()
                .await
                .expect("start mock inference server");
            let workdir = git_workdir();
            let home = tempfile::tempdir().expect("create isolated profile");
            write_read_only_marketplace_fixture(home.path());

            let client = GrokStdioClient::spawn_with_home(&server, workdir.path(), home).await;
            client.initialize_with_timeout().await;
            initialize_agentdesk_extensions(&client).await;
            let session_id = client.create_session_with_timeout(workdir.path()).await;
            let session_id = session_id.0.as_ref();

            for (label, client_identifier) in [
                ("missing identifier", None),
                ("wrong casing", Some(serde_json::json!("AgentDesk"))),
                ("wrong type", Some(serde_json::json!(7))),
                ("forged other client", Some(serde_json::json!("grok-tui"))),
            ] {
                let mut params = serde_json::json!({ "sessionId": session_id });
                if let Some(client_identifier) = client_identifier {
                    params["clientIdentifier"] = client_identifier;
                }
                let list = client
                    .ext_method("x.ai/marketplace/list", params)
                    .await
                    .unwrap_or_else(|error| {
                        panic!(
                            "marketplace/list ({label}) failed: {error:?}\nstderr:\n{}",
                            client.stderr()
                        )
                    });
                let list_payload: serde_json::Value =
                    serde_json::from_str(list.0.get()).expect("parse marketplace/list response");
                let discovered_default_skills = list_payload["result"]["sources"]
                    .as_array()
                    .into_iter()
                    .flatten()
                    .flat_map(|source| source["plugins"].as_array().into_iter().flatten())
                    .any(|plugin| plugin["relativePath"] == "default-skills");
                assert!(
                    discovered_default_skills,
                    "the fixture did not exercise the default-skills path: {list_payload}"
                );
                assert_marketplace_install_registry_empty(client.home_path(), label);

                let plugins = client
                    .ext_method(
                        "x.ai/plugins/list",
                        serde_json::json!({ "sessionId": session_id }),
                    )
                    .await
                    .expect("plugins/list after marketplace/list");
                assert_plugins_do_not_include_marketplace_fixture(&plugins, label);
            }

            let exit_status = client.shutdown_with_timeout().await;
            assert!(exit_status.success(), "sidecar did not exit cleanly");
        })
        .await;
}

#[tokio::test(flavor = "current_thread")]
async fn negotiated_agentdesk_identity_keeps_marketplace_refresh_read_only() {
    tokio::task::LocalSet::new()
        .run_until(async {
            let server = MockInferenceServer::start()
                .await
                .expect("start mock inference server");
            let workdir = git_workdir();
            let home = tempfile::tempdir().expect("create isolated profile");
            write_read_only_marketplace_fixture(home.path());

            let client = GrokStdioClient::spawn_with_home(&server, workdir.path(), home).await;
            client.initialize_with_timeout().await;
            initialize_agentdesk_extensions(&client).await;
            let session_id = client.create_session_with_timeout(workdir.path()).await;
            let session_id = session_id.0.as_ref();

            for (label, client_identifier) in [
                ("missing identifier", None),
                ("wrong casing", Some(serde_json::json!("AgentDesk"))),
                ("wrong type", Some(serde_json::json!(7))),
                ("forged other client", Some(serde_json::json!("grok-tui"))),
            ] {
                let mut params = serde_json::json!({
                    "sessionId": session_id,
                    "action": { "type": "refresh" }
                });
                if let Some(client_identifier) = client_identifier {
                    params["clientIdentifier"] = client_identifier;
                }
                let refresh = client
                    .ext_method("x.ai/marketplace/action", params)
                    .await
                    .unwrap_or_else(|error| {
                        panic!(
                            "marketplace refresh ({label}) failed: {error:?}\nstderr:\n{}",
                            client.stderr()
                        )
                    });
                let refresh_payload: serde_json::Value = serde_json::from_str(refresh.0.get())
                    .expect("parse marketplace refresh response");
                assert_eq!(refresh_payload["result"]["status"], "success");
                assert_eq!(refresh_payload["result"]["requiresReload"], false);
                assert_marketplace_install_registry_empty(client.home_path(), label);

                let plugins = client
                    .ext_method(
                        "x.ai/plugins/list",
                        serde_json::json!({ "sessionId": session_id }),
                    )
                    .await
                    .expect("plugins/list after marketplace refresh");
                assert_plugins_do_not_include_marketplace_fixture(&plugins, label);
            }

            let exit_status = client.shutdown_with_timeout().await;
            assert!(exit_status.success(), "sidecar did not exit cleanly");
        })
        .await;
}

#[tokio::test(flavor = "current_thread")]
async fn marketplace_list_for_other_clients_preserves_default_skill_auto_install() {
    tokio::task::LocalSet::new()
        .run_until(async {
            let server = MockInferenceServer::start()
                .await
                .expect("start mock inference server");
            let workdir = git_workdir();
            let home = tempfile::tempdir().expect("create isolated profile");
            write_read_only_marketplace_fixture(home.path());

            let client = GrokStdioClient::spawn_with_home(&server, workdir.path(), home).await;
            client.initialize_with_timeout().await;
            let session_id = client.create_session_with_timeout(workdir.path()).await;
            let session_id = session_id.0.as_ref();

            client
                .ext_method(
                    "x.ai/marketplace/list",
                    serde_json::json!({ "sessionId": session_id }),
                )
                .await
                .unwrap_or_else(|error| {
                    panic!(
                        "marketplace/list failed: {error:?}\nstderr:\n{}",
                        client.stderr()
                    )
                });

            assert_marketplace_default_skills_installed(client.home_path());
            let plugins = client
                .ext_method(
                    "x.ai/plugins/list",
                    serde_json::json!({ "sessionId": session_id }),
                )
                .await
                .expect("plugins/list after marketplace/list");
            assert_plugins_include_marketplace_fixture(&plugins);

            let exit_status = client.shutdown_with_timeout().await;
            assert!(exit_status.success(), "sidecar did not exit cleanly");
        })
        .await;
}

#[test]
fn agentdesk_health_and_desktop_credentials_fail_closed() {
    let initialize_response = extensions::agentdesk::initialize_response().unwrap();
    let initialize: serde_json::Value = serde_json::from_str(initialize_response.0.get()).unwrap();
    assert_eq!(initialize["protocolVersion"], 1);
    assert_eq!(
        initialize["sessionModes"],
        serde_json::json!(["default", "plan"])
    );
    assert_eq!(initialize["sessionTransfer"]["schemaVersion"], 1);
    assert_eq!(initialize["sessionTransfer"]["export"], true);
    assert_eq!(initialize["sessionTransfer"]["import"], true);
    assert!(initialize.get("result").is_none());

    let health_response = extensions::agentdesk::health_response().unwrap();
    let health: serde_json::Value = serde_json::from_str(health_response.0.get()).unwrap();
    assert_eq!(health["status"], "ok");
    assert!(health.get("result").is_none());
    assert!(health["sandbox"]["configuredProfile"].is_string());
    assert!(health["sandbox"]["active"].is_boolean());
    assert_eq!(health["sandbox"]["childNetworkRestricted"], false);
    assert!(health["sandbox"]["enforcementRequired"].is_boolean());

    unsafe {
        std::env::set_var("GROK_DISABLE_API_KEY_PERSIST", "1");
    }
    let credential_response =
        extensions::agentdesk::credential_response("desktop-pipe-secret").unwrap();
    let credential: serde_json::Value = serde_json::from_str(credential_response.0.get()).unwrap();
    assert_eq!(credential["credentialAccepted"], true);
    assert_eq!(credential["authMethodId"], "xai.api_key");
    assert_eq!(read_xai_api_key_env().as_deref(), Ok("desktop-pipe-secret"));

    let grok_home = tempfile::tempdir().unwrap();
    auth::store_api_key(grok_home.path(), "legacy-disk-secret").unwrap();
    // SAFETY: this integration test is the only test in its process and is
    // invoked with one test thread, before starting any AgentDesk runtime.
    unsafe {
        std::env::set_var("GROK_HOME", grok_home.path());
        std::env::set_var("GROK_DISABLE_API_KEY_PERSIST", "1");
        std::env::set_var(XAI_API_KEY_ENV_VAR, "desktop-memory-secret");
        std::env::set_var(LEGACY_XAI_API_KEY_ENV_VAR, "legacy-memory-secret");
        capture_desktop_api_key_before_runtime().unwrap();
    }

    assert_eq!(auth::read_api_key(grok_home.path()), None);
    assert_eq!(
        read_xai_api_key_env().as_deref(),
        Ok("desktop-memory-secret")
    );
    assert!(std::env::var_os(XAI_API_KEY_ENV_VAR).is_none());
    assert!(std::env::var_os(LEGACY_XAI_API_KEY_ENV_VAR).is_none());

    // SAFETY: same single-threaded process contract as above. Disabling the
    // desktop switch must leave the inherited key in place.
    unsafe {
        std::env::remove_var("GROK_DISABLE_API_KEY_PERSIST");
        std::env::set_var(XAI_API_KEY_ENV_VAR, "default-environment-secret");
        capture_desktop_api_key_before_runtime().unwrap();
    }
    assert_eq!(
        std::env::var(XAI_API_KEY_ENV_VAR).as_deref(),
        Ok("default-environment-secret")
    );

    std::fs::write(grok_home.path().join("auth.json"), "{not-json").unwrap();
    // SAFETY: same single-threaded process contract as above. A cleanup
    // failure must abort startup before inherited credentials are scrubbed.
    let cleanup_error = unsafe {
        std::env::set_var("GROK_DISABLE_API_KEY_PERSIST", "1");
        std::env::set_var(XAI_API_KEY_ENV_VAR, "must-remain-on-cleanup-error");
        capture_desktop_api_key_before_runtime().unwrap_err()
    };
    assert_eq!(cleanup_error.kind(), std::io::ErrorKind::InvalidData);
    assert_eq!(
        std::env::var(XAI_API_KEY_ENV_VAR).as_deref(),
        Ok("must-remain-on-cleanup-error")
    );

    // SAFETY: no other test shares this process.
    unsafe {
        std::env::remove_var("GROK_HOME");
        std::env::remove_var("GROK_DISABLE_API_KEY_PERSIST");
        std::env::remove_var(XAI_API_KEY_ENV_VAR);
    }
}
