// Modified by the AgentDesk project for Windows desktop integration and safety support.
//! Shared environment helpers: binary resolution, git workdirs, env var setup.

use std::ffi::{OsStr, OsString};
use std::path::{Path, PathBuf};
use std::process::Command;
use std::sync::OnceLock;

use tempfile::TempDir;

/// RAII guard for a single environment variable in `#[serial]` tests: snapshots
/// the prior value on construction, applies the change, then restores the prior
/// value (or unsets it) on drop — even if an assertion panics. Restoring rather
/// than always unsetting avoids clobbering vars a parent process/harness set
/// (e.g. `RUST_LOG`).
///
/// Callers MUST be `#[serial_test::serial]`: the `unsafe` `set_var`/`remove_var`
/// are sound only when no other thread accesses the environment concurrently.
pub struct EnvGuard {
    key: &'static str,
    prior: Option<OsString>,
}

impl EnvGuard {
    /// Set `key` to `value` for the guard's lifetime. Accepts `&str`, `&Path`,
    /// `String`, etc. via `AsRef<OsStr>`.
    pub fn set(key: &'static str, value: impl AsRef<OsStr>) -> Self {
        let prior = std::env::var_os(key);
        // SAFETY: callers are `#[serial]`, so no other thread touches the env.
        unsafe { std::env::set_var(key, value) };
        Self { key, prior }
    }

    /// Unset `key` for the guard's lifetime.
    pub fn unset(key: &'static str) -> Self {
        let prior = std::env::var_os(key);
        // SAFETY: see [`EnvGuard::set`].
        unsafe { std::env::remove_var(key) };
        Self { key, prior }
    }
}

impl Drop for EnvGuard {
    fn drop(&mut self) {
        // SAFETY: see [`EnvGuard::set`].
        match self.prior.take() {
            Some(v) => unsafe { std::env::set_var(self.key, v) },
            None => unsafe { std::env::remove_var(self.key) },
        }
    }
}

fn workspace_root() -> PathBuf {
    // nth(3): crate is nested three levels below the cargo workspace root.
    PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .ancestors()
        .nth(3)
        .expect("workspace root")
        .to_path_buf()
}

fn target_dir() -> PathBuf {
    std::env::var_os("CARGO_TARGET_DIR")
        .map(PathBuf::from)
        .unwrap_or_else(|| workspace_root().join("target"))
}

fn local_grok_binary_path() -> PathBuf {
    target_dir()
        .join("debug")
        .join(format!("xai-grok-pager{}", std::env::consts::EXE_SUFFIX))
}

fn grok_binary_build_args() -> Vec<&'static str> {
    let mut args = vec![
        "rustc",
        "--locked",
        "-p",
        "xai-grok-pager-bin",
        "--bin",
        "xai-grok-pager",
    ];
    #[cfg(all(windows, target_env = "msvc"))]
    args.extend(["--", "-C", "link-arg=/DEBUG:NONE"]);
    args
}

fn ensure_local_grok_binary(binary: &Path) {
    let cargo = std::env::var("CARGO").unwrap_or_else(|_| "cargo".to_string());
    let output = Command::new(&cargo)
        .current_dir(workspace_root())
        .args(grok_binary_build_args())
        .output()
        .unwrap_or_else(|e| panic!("failed to spawn {cargo} to build xai-grok-pager: {e}"));

    assert!(
        output.status.success(),
        "failed to build xai-grok-pager for lifecycle tests (exit {:?})\nstdout:\n{}\nstderr:\n{}",
        output.status.code(),
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr),
    );
    assert!(
        binary.exists(),
        "xai-grok-pager build completed but binary missing at {}",
        binary.display()
    );
}

/// Resolve grok binary: `GROK_BINARY` env (CI) or a locally built `xai-grok-pager` binary.
pub fn grok_binary() -> PathBuf {
    if let Ok(path) = std::env::var("GROK_BINARY") {
        let p = PathBuf::from(path);
        assert!(p.exists(), "GROK_BINARY does not exist: {}", p.display());
        return p;
    }

    if let Ok(path) = std::env::var("CARGO_BIN_EXE_xai-grok-pager") {
        let p = PathBuf::from(path);
        if p.exists() {
            return p;
        }
    }

    static LOCAL_BINARY: OnceLock<PathBuf> = OnceLock::new();
    LOCAL_BINARY
        .get_or_init(|| {
            let binary = local_grok_binary_path();
            // Cargo owns freshness checks instead of trusting a stale pager.
            // On Windows the final linker override also avoids MSVC's PDB size
            // limit while preserving the build-script stack reservation.
            ensure_local_grok_binary(&binary);
            binary
        })
        .clone()
}

/// Temp dir with a git repo + one committed file.
/// Forces libgit2 to fully init (the codepath that breaks with bad OpenSSL linking).
pub fn git_workdir() -> TempDir {
    let dir = TempDir::new().expect("create temp dir");
    let path = dir.path();

    fn run_git(args: &[&str], dir: &Path) {
        let output = Command::new("git")
            .args(args)
            .current_dir(dir)
            .output()
            .unwrap_or_else(|e| panic!("failed to spawn git {}: {e}", args.join(" ")));
        assert!(
            output.status.success(),
            "git {} failed (exit {:?}):\n{}",
            args.join(" "),
            output.status.code(),
            String::from_utf8_lossy(&output.stderr),
        );
    }

    run_git(&["init"], path);
    // Configure git user for commits (required in CI where no global config exists)
    run_git(&["config", "user.email", "test@test.com"], path);
    run_git(&["config", "user.name", "Test"], path);

    std::fs::write(path.join("README.md"), "test file\n").expect("write test file");

    run_git(&["add", "-A"], path);
    run_git(&["commit", "-m", "init", "--no-gpg-sign"], path);

    dir
}

/// Point grok at the mock server with a fake API key and telemetry disabled.
pub fn test_env_cmd_tokio(
    cmd: &mut tokio::process::Command,
    mock_url: &str,
    home: &std::path::Path,
) {
    // Grok compatibility imports and plugins also consult Windows profile and
    // AppData roots, so GROK_HOME alone is not a hermetic test boundary.
    #[cfg(windows)]
    cmd.env("USERPROFILE", home)
        .env("APPDATA", home.join("AppData").join("Roaming"))
        .env("LOCALAPPDATA", home.join("AppData").join("Local"));

    cmd.env("HOME", home)
        // Keep Grok's own caches and session state under the same disposable
        // profile instead of the developer's real configuration directory.
        .env("GROK_HOME", home.join(".grok"))
        .env("GROK_CLI_CHAT_PROXY_BASE_URL", mock_url)
        .env("GROK_XAI_API_BASE_URL", mock_url)
        .env("XAI_API_KEY", "test-key-for-ci")
        .env("GROK_TELEMETRY_ENABLED", "false")
        .env("GROK_FEEDBACK_ENABLED", "false")
        .env("GROK_TRACE_UPLOAD", "false")
        .env("GROK_INSTRUMENTATION", "disabled")
        // Release binaries (CI lifecycle tests) otherwise spawn a background
        // update check that hits the network and can add latency under Rosetta.
        .env("GROK_DISABLE_AUTOUPDATER", "1");

    // Windows known-folder APIs can ignore USERPROFILE and expose the real
    // Claude/Cursor configuration to an otherwise isolated child process.
    // Disable every vendor-compat discovery surface in the test harness so
    // local MCPs, hooks, rules, and sessions cannot affect release gates.
    for variable in [
        "GROK_CLAUDE_SKILLS_ENABLED",
        "GROK_CLAUDE_RULES_ENABLED",
        "GROK_CLAUDE_AGENTS_ENABLED",
        "GROK_CLAUDE_MCPS_ENABLED",
        "GROK_CLAUDE_HOOKS_ENABLED",
        "GROK_CLAUDE_SESSIONS_ENABLED",
        "GROK_CURSOR_SKILLS_ENABLED",
        "GROK_CURSOR_RULES_ENABLED",
        "GROK_CURSOR_AGENTS_ENABLED",
        "GROK_CURSOR_MCPS_ENABLED",
        "GROK_CURSOR_HOOKS_ENABLED",
        "GROK_CURSOR_SESSIONS_ENABLED",
    ] {
        cmd.env(variable, "false");
    }
}

#[cfg(test)]
mod tests {
    use std::collections::HashMap;
    use std::ffi::{OsStr, OsString};
    use std::path::PathBuf;

    use super::{grok_binary_build_args, local_grok_binary_path, test_env_cmd_tokio};

    #[test]
    fn local_grok_binary_refreshes_the_pager_bin_with_locked_cargo() {
        let mut expected = vec![
            "rustc",
            "--locked",
            "-p",
            "xai-grok-pager-bin",
            "--bin",
            "xai-grok-pager",
        ];
        #[cfg(all(windows, target_env = "msvc"))]
        expected.extend(["--", "-C", "link-arg=/DEBUG:NONE"]);
        assert_eq!(grok_binary_build_args().as_slice(), expected.as_slice());
        assert_eq!(
            local_grok_binary_path()
                .parent()
                .and_then(std::path::Path::file_name),
            Some(OsStr::new("debug"))
        );
    }

    #[cfg(windows)]
    #[test]
    fn windows_test_env_redirects_user_config_roots() {
        let home = tempfile::tempdir().expect("create isolated Windows test profile");
        let mut cmd = tokio::process::Command::new("cmd.exe");
        test_env_cmd_tokio(&mut cmd, "http://127.0.0.1:1", home.path());

        let env: HashMap<OsString, Option<OsString>> = cmd
            .as_std()
            .get_envs()
            .map(|(key, value)| (key.to_os_string(), value.map(OsStr::to_os_string)))
            .collect();
        let env_path = |key: &str| {
            env.get(OsStr::new(key))
                .and_then(|value| value.as_ref())
                .map(PathBuf::from)
        };

        assert_eq!(env_path("USERPROFILE"), Some(home.path().to_path_buf()));
        assert_eq!(
            env_path("APPDATA"),
            Some(home.path().join("AppData").join("Roaming"))
        );
        assert_eq!(
            env_path("LOCALAPPDATA"),
            Some(home.path().join("AppData").join("Local"))
        );
        assert_eq!(
            env.get(OsStr::new("GROK_CLAUDE_MCPS_ENABLED"))
                .and_then(|value| value.as_deref()),
            Some(OsStr::new("false"))
        );
        assert_eq!(
            env.get(OsStr::new("GROK_CURSOR_MCPS_ENABLED"))
                .and_then(|value| value.as_deref()),
            Some(OsStr::new("false"))
        );
    }
}
