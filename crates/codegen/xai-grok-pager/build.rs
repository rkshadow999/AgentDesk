// Modified by the AgentDesk project for Windows desktop integration and safety support.
use std::path::PathBuf;
use std::process::Command;

fn git_output(args: &[&str]) -> Option<String> {
    Command::new("git")
        .current_dir(env!("CARGO_MANIFEST_DIR"))
        .args(args)
        .output()
        .ok()
        .filter(|output| output.status.success())
        .and_then(|output| String::from_utf8(output.stdout).ok())
        .map(|value| value.trim().to_string())
        .filter(|value| !value.is_empty())
}

fn git_metadata_path(name: &str) -> Option<PathBuf> {
    git_output(&["rev-parse", "--path-format=absolute", "--git-path", name])
        .map(PathBuf::from)
        .filter(|path| path.is_file())
}

fn watch_git_metadata() {
    for path in [
        git_metadata_path("HEAD"),
        git_metadata_path("logs/HEAD"),
        git_output(&["symbolic-ref", "--quiet", "HEAD"])
            .and_then(|reference| git_metadata_path(&reference)),
        git_metadata_path("packed-refs"),
    ]
    .into_iter()
    .flatten()
    {
        println!("cargo:rerun-if-changed={}", path.display());
    }
}

fn main() {
    watch_git_metadata();
    println!("cargo:rerun-if-env-changed=GROK_VERSION");

    let commit = Command::new("git")
        .args(["rev-parse", "--short", "HEAD"])
        .output()
        .ok()
        .filter(|o| o.status.success())
        .and_then(|o| String::from_utf8(o.stdout).ok())
        .map(|s| s.trim().to_string())
        .unwrap_or_else(|| "unknown".to_string());

    let version = std::env::var("GROK_VERSION")
        .or_else(|_| std::env::var("CARGO_PKG_VERSION"))
        .unwrap_or_else(|_| "0.0.0".to_string());

    println!(
        "cargo:rustc-env=VERSION_WITH_COMMIT={} ({})",
        version, commit
    );
}
