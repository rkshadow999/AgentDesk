#!/usr/bin/env python3
"""Focused regression tests for the Linux strict health probe."""

from __future__ import annotations

import importlib.util
import unittest
from pathlib import Path


def load_probe_module():
    probe_path = Path(__file__).with_name("Verify-AgentDeskLinuxStrictHealth.py")
    spec = importlib.util.spec_from_file_location(
        "agentdesk_linux_strict_health_probe", probe_path
    )
    if spec is None or spec.loader is None:
        raise RuntimeError(f"Unable to load strict health probe: {probe_path}")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class WireMethodTests(unittest.TestCase):
    def test_extension_methods_use_the_acp_wire_prefix(self) -> None:
        probe = load_probe_module()

        self.assertEqual(
            "_agentdesk/v1/initialize",
            probe.to_wire_method("agentdesk/v1/initialize"),
        )
        self.assertEqual(
            "_agentdesk/v1/health",
            probe.to_wire_method("agentdesk/v1/health"),
        )
        self.assertEqual("initialize", probe.to_wire_method("initialize"))
        self.assertEqual(
            "_agentdesk/v1/health",
            probe.to_wire_method("_agentdesk/v1/health"),
        )


if __name__ == "__main__":
    unittest.main()
