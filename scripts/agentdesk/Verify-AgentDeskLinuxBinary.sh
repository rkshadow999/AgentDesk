#!/usr/bin/env bash
set -euo pipefail

if [[ $# -lt 2 || $# -gt 4 ]]; then
  echo "Usage: $0 <binary> <x64|arm64> [maximum-glibc-version] [report-path]" >&2
  exit 2
fi

binary_path="$1"
architecture="$2"
maximum_glibc="${3:-2.35}"
report_path="${4:-}"

if [[ ! -f "$binary_path" ]]; then
  echo "Linux sidecar does not exist: $binary_path" >&2
  exit 1
fi
if [[ "$architecture" != "x64" && "$architecture" != "arm64" ]]; then
  echo "Unsupported Linux sidecar architecture: $architecture" >&2
  exit 2
fi

# shellcheck source=/dev/null
source /etc/os-release
if [[ "${VERSION_ID:-}" != "22.04" ]]; then
  echo "Linux sidecars must be built and verified on Ubuntu 22.04; found ${PRETTY_NAME:-unknown}." >&2
  exit 1
fi

file_description="$(file --brief --dereference -- "$binary_path")"
if [[ "$architecture" == "x64" ]]; then
  expected_machine='x86-64'
else
  expected_machine='ARM aarch64|AArch64'
fi
if ! grep -Eq "$expected_machine" <<<"$file_description"; then
  echo "Linux sidecar architecture mismatch for $architecture: $file_description" >&2
  exit 1
fi

version_info="$(readelf --version-info --wide "$binary_path")"
required_glibc="$(grep -oE 'GLIBC_[0-9]+(\.[0-9]+)+' <<<"$version_info" | sed 's/^GLIBC_//' | sort -Vu | tail -n 1 || true)"
if [[ -z "$required_glibc" ]]; then
  echo "Linux sidecar does not declare a GLIBC symbol requirement: $binary_path" >&2
  exit 1
fi

newest_version="$(printf '%s\n%s\n' "$required_glibc" "$maximum_glibc" | sort -V | tail -n 1)"
if [[ "$newest_version" != "$maximum_glibc" ]]; then
  echo "Linux sidecar requires GLIBC_$required_glibc, above the supported GLIBC_$maximum_glibc baseline." >&2
  exit 1
fi

report="$(cat <<EOF
AgentDesk WSL sidecar compatibility
Build distribution: ${PRETTY_NAME}
Package architecture: ${architecture}
Maximum supported GLIBC: ${maximum_glibc}
Maximum required GLIBC: ${required_glibc}
ELF description: ${file_description}
EOF
)"

if [[ -n "$report_path" ]]; then
  mkdir -p "$(dirname "$report_path")"
  printf '%s\n' "$report" > "$report_path"
fi
printf '%s\n' "$report"
