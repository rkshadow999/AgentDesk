#!/usr/bin/env python3
"""Verify the built Linux sidecar reports the current strict health boundary."""

from __future__ import annotations

import json
import os
import selectors
import subprocess
import sys
import tempfile
import time
from pathlib import Path


def fail(message: str, stderr_path: Path) -> None:
    stderr = stderr_path.read_text(encoding="utf-8", errors="replace")
    if len(stderr) > 4000:
        stderr = stderr[-4000:]
    raise RuntimeError(f"{message}\nsidecar stderr:\n{stderr}")


def to_wire_method(method: str) -> str:
    if method.startswith(("agentdesk/", "x.ai/")):
        return f"_{method}"
    return method


def send_request(
    process: subprocess.Popen[str],
    selector: selectors.BaseSelector,
    request_id: str,
    method: str,
    params: dict[str, object],
    stderr_path: Path,
) -> dict[str, object]:
    assert process.stdin is not None
    assert process.stdout is not None
    request = {
        "jsonrpc": "2.0",
        "id": request_id,
        "method": to_wire_method(method),
        "params": params,
    }
    process.stdin.write(json.dumps(request, separators=(",", ":")) + "\n")
    process.stdin.flush()

    deadline = time.monotonic() + 15
    while time.monotonic() < deadline:
        if process.poll() is not None:
            fail(
                f"strict sidecar exited before {method} responded with code {process.returncode}",
                stderr_path,
            )
        events = selector.select(timeout=0.25)
        if not events:
            continue
        line = process.stdout.readline()
        if not line:
            continue
        try:
            message = json.loads(line)
        except json.JSONDecodeError:
            continue
        if message.get("id") == request_id:
            if "error" in message:
                fail(
                    f"strict sidecar {method} returned an error: {message['error']}",
                    stderr_path,
                )
            return message
        if "id" in message and "method" in message:
            refusal = {
                "jsonrpc": "2.0",
                "id": message["id"],
                "error": {"code": -32601, "message": "unsupported by health probe"},
            }
            process.stdin.write(json.dumps(refusal, separators=(",", ":")) + "\n")
            process.stdin.flush()

    fail(f"strict sidecar {method} response timed out", stderr_path)


def main() -> int:
    if len(sys.argv) != 2:
        raise SystemExit(
            "usage: Verify-AgentDeskLinuxStrictHealth.py <agentdesk-engine>"
        )

    binary = Path(sys.argv[1]).resolve()
    if not binary.is_file():
        raise FileNotFoundError(f"Linux sidecar not found: {binary}")

    with tempfile.TemporaryDirectory(prefix="agentdesk-strict-health-") as temp_root:
        root = Path(temp_root)
        workspace = root / "workspace"
        home = root / "home"
        engine_home = root / "engine-home"
        workspace.mkdir()
        home.mkdir()
        engine_home.mkdir()
        stderr_path = root / "stderr.log"

        environment = os.environ.copy()
        environment.update(
            {
                "HOME": str(home),
                "GROK_HOME": str(engine_home),
                "GROK_SANDBOX": "strict",
                "GROK_SANDBOX_REQUIRE_ENFORCEMENT": "1",
                "GROK_DISABLE_API_KEY_PERSIST": "1",
                "GROK_DISABLE_AUTOUPDATER": "1",
                "DISABLE_TELEMETRY": "1",
                "GROK_TELEMETRY_ENABLED": "false",
                "GROK_TELEMETRY_TRACE_UPLOAD": "false",
                "GROK_FEEDBACK_ENABLED": "false",
                "DISABLE_ERROR_REPORTING": "1",
                "AGENTDESK_SUBAGENT_WORKTREE_MODE": "strict",
            }
        )
        environment.pop("XAI_API_KEY", None)
        environment.pop("GROK_CODE_XAI_API_KEY", None)

        with stderr_path.open("wb") as stderr_file:
            process = subprocess.Popen(
                [
                    str(binary),
                    "--no-auto-update",
                    "agent",
                    "--no-leader",
                    "stdio",
                ],
                cwd=workspace,
                env=environment,
                stdin=subprocess.PIPE,
                stdout=subprocess.PIPE,
                stderr=stderr_file,
                text=True,
                encoding="utf-8",
                bufsize=1,
            )
            assert process.stdin is not None
            assert process.stdout is not None

            selector = selectors.DefaultSelector()
            selector.register(process.stdout, selectors.EVENT_READ)
            initialize = send_request(
                process,
                selector,
                "acp-initialize",
                "initialize",
                {
                    "protocolVersion": 1,
                    "clientCapabilities": {
                        "fs": {"readTextFile": False, "writeTextFile": False},
                        "terminal": False,
                    },
                    "clientInfo": {
                        "name": "agentdesk-health-probe",
                        "title": "AgentDesk Health Probe",
                        "version": "0.1.0",
                    },
                },
                stderr_path,
            )
            initialize_result = initialize.get("result")
            if not isinstance(initialize_result, dict) or initialize_result.get(
                "protocolVersion"
            ) != 1:
                fail("strict sidecar ACP initialize response was incompatible", stderr_path)

            extension = send_request(
                process,
                selector,
                "agentdesk-initialize",
                "agentdesk/v1/initialize",
                {
                    "protocolVersion": 1,
                    "client": {"name": "agentdesk", "version": "0.1.0"},
                },
                stderr_path,
            )
            extension_result = extension.get("result")
            if not isinstance(extension_result, dict) or extension_result.get(
                "protocolVersion"
            ) != 1:
                fail("strict sidecar AgentDesk extension was incompatible", stderr_path)

            response = send_request(
                process,
                selector,
                "agentdesk-health",
                "agentdesk/v1/health",
                {},
                stderr_path,
            )

            result = response.get("result")
            if not isinstance(result, dict) or result.get("status") != "ok":
                fail("strict sidecar health status was not ok", stderr_path)
            sandbox = result.get("sandbox")
            expected = {
                "configuredProfile": "strict",
                "active": True,
                "activeProfile": "strict",
                "childNetworkRestricted": False,
                "enforcementRequired": True,
            }
            if not isinstance(sandbox, dict):
                fail("strict sidecar health omitted sandbox attestation", stderr_path)
            for key, value in expected.items():
                if sandbox.get(key) != value:
                    fail(
                        f"strict sidecar health field {key} was {sandbox.get(key)!r}, expected {value!r}",
                        stderr_path,
                    )

            process.stdin.close()
            try:
                return_code = process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=5)
                fail("strict sidecar did not exit after stdin closed", stderr_path)
            if return_code != 0:
                fail(
                    f"strict sidecar exited with code {return_code} after health probe",
                    stderr_path,
                )

    print("AgentDesk Linux strict health fail-closed verification passed.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
