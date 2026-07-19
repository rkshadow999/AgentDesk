use super::ExtResult;
use crate::agent::config::{Config, ConfigModelOverride};
use crate::sampling::ApiBackend;

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct OpenAiCompatibleProfile {
    model: String,
    base_url: String,
    api_backend: ApiBackend,
}

impl OpenAiCompatibleProfile {
    pub fn new(model: impl AsRef<str>, base_url: impl AsRef<str>) -> anyhow::Result<Self> {
        Self::new_with_backend(model, base_url, "chat_completions")
    }

    pub fn new_with_backend(
        model: impl AsRef<str>,
        base_url: impl AsRef<str>,
        backend: impl AsRef<str>,
    ) -> anyhow::Result<Self> {
        let api_backend = match backend.as_ref() {
            "chat_completions" => ApiBackend::ChatCompletions,
            "responses" => ApiBackend::Responses,
            _ => anyhow::bail!("the AgentDesk provider backend is unsupported"),
        };
        let model = model.as_ref().trim();
        anyhow::ensure!(
            !model.is_empty() && !model.chars().any(char::is_control),
            "the AgentDesk model is invalid"
        );

        let candidate = base_url.as_ref().trim();
        let url = url::Url::parse(candidate)
            .map_err(|_| anyhow::anyhow!("the AgentDesk Base URL is invalid"))?;
        anyhow::ensure!(
            matches!(url.scheme(), "http" | "https")
                && url.host_str().is_some()
                && url.username().is_empty()
                && url.password().is_none()
                && url.query().is_none()
                && url.fragment().is_none(),
            "the AgentDesk Base URL is invalid"
        );
        let base_url = url.as_str().trim_end_matches('/').to_owned();

        Ok(Self {
            model: model.to_owned(),
            base_url,
            api_backend,
        })
    }

    pub fn model(&self) -> &str {
        &self.model
    }

    pub fn base_url(&self) -> &str {
        &self.base_url
    }

    pub fn api_backend(&self) -> &ApiBackend {
        &self.api_backend
    }

    pub fn apply(&self, config: &mut Config) {
        config.config_models.insert(
            self.model.clone(),
            ConfigModelOverride {
                model: Some(self.model.clone()),
                base_url: Some(self.base_url.clone()),
                api_backend: Some(self.api_backend.clone()),
                supported_in_api: Some(true),
                ..ConfigModelOverride::default()
            },
        );
        config.default_model_override = Some(self.model.clone());
        config.endpoints.xai_api_base_url = self.base_url.clone();
    }
}

pub const PROTOCOL_VERSION: u64 = 1;
pub const CREDENTIAL_AUTH_METHOD_ID: &str = "xai.api_key";
pub const MAX_CREDENTIAL_LENGTH: usize = 16 * 1024;

pub fn initialize_payload() -> serde_json::Value {
    serde_json::json!({
        "protocolVersion": PROTOCOL_VERSION,
        "engine": {
            "name": "grok-build",
            "version": env!("CARGO_PKG_VERSION"),
        },
        "sessionModes": ["default", "plan"],
        "sessionTransfer": {
            "schemaVersion": crate::extensions::agentdesk_session::SESSION_TRANSFER_SCHEMA_VERSION,
            "export": true,
            "import": true,
        },
        "memory": {
            "schemaVersion": crate::extensions::agentdesk_memory::MEMORY_SCHEMA_VERSION,
            "list": true,
            "read": true,
            "write": true,
            "delete": true,
            "mutationConfirmationRequired": true,
        },
    })
}

pub fn credential_response(api_key: &str) -> ExtResult {
    if !crate::auth::api_key_persistence_disabled() {
        return Err(agent_client_protocol::Error::invalid_params()
            .data("the AgentDesk credential bridge requires memory-only credential mode"));
    }
    if api_key.is_empty() || api_key.len() > MAX_CREDENTIAL_LENGTH {
        return Err(agent_client_protocol::Error::invalid_params()
            .data("the AgentDesk credential is empty or exceeds the size limit"));
    }

    crate::agent::auth_method::set_captured_desktop_api_key(Some(api_key.to_owned()));
    super::to_raw_response(&serde_json::json!({
        "credentialAccepted": true,
        "authMethodId": CREDENTIAL_AUTH_METHOD_ID,
    }))
}

pub fn initialize_response() -> ExtResult {
    super::to_raw_response(&initialize_payload())
}

pub fn health_payload() -> serde_json::Value {
    serde_json::json!({
        "status": "ok",
        "sandbox": {
            "configuredProfile": xai_grok_sandbox::configured_profile_name().unwrap_or("off"),
            "active": xai_grok_sandbox::is_active(),
            "activeProfile": xai_grok_sandbox::profile_name(),
            "childNetworkRestricted": xai_grok_sandbox::child_network_fully_enforced(),
            "enforcementRequired": xai_grok_sandbox::enforcement_required(),
        }
    })
}

pub fn health_response() -> ExtResult {
    super::to_raw_response(&health_payload())
}
