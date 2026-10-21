"""Installation selection regressions. These tests never start a broker or model."""
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

import localai_installation


class RuntimePlatformTests(unittest.TestCase):
    def test_unsupported_platform_does_not_use_environment(self):
        with patch.object(sys, "platform", "linux"), patch.dict(os.environ, {"LOCALAPPDATA": "ignored"}):
            with self.assertRaisesRegex(OSError, "require Windows"):
                localai_installation.installed_runtime_root()


@unittest.skipUnless(sys.platform == "win32", "Windows known-folder API required")
class InstalledRuntimeTests(unittest.TestCase):
    def test_environment_cannot_select_another_installation(self):
        expected = localai_installation.installed_runtime_root()
        with tempfile.TemporaryDirectory() as directory:
            for override in (directory, None):
                with self.subTest(local_app_data=override):
                    environment = os.environ.copy()
                    if override is None:
                        environment.pop("LOCALAPPDATA", None)
                    else:
                        environment["LOCALAPPDATA"] = override
                    result = subprocess.run([
                        sys.executable, "-B", "-c",
                        "from localai_installation import installed_runtime_root; "
                        "print(installed_runtime_root())"],
                        cwd=Path(__file__).resolve().parent, env=environment,
                        check=True, capture_output=True, text=True, timeout=15)
                    self.assertEqual(expected, Path(result.stdout.strip()))

    def test_failed_lookup_does_not_fall_back_to_environment(self):
        with patch.object(localai_installation, "_windows_local_app_data", side_effect=OSError("lookup failed")):
            with patch.dict(os.environ, {"LOCALAPPDATA": tempfile.gettempdir()}):
                with self.assertRaisesRegex(OSError, "lookup failed"):
                    localai_installation.installed_runtime_root()

    def test_non_absolute_native_result_is_rejected(self):
        with patch.object(localai_installation, "_windows_local_app_data", return_value=Path("relative")):
            with self.assertRaisesRegex(OSError, "non-absolute"):
                localai_installation.installed_runtime_root()


class ProbeStopped(Exception):
    pass


@unittest.skipUnless(sys.platform == "win32", "Windows diagnostic entry points required")
class DiagnosticEntryPointTests(unittest.TestCase):
    def test_both_entry_points_use_the_known_installation(self):
        import retention_probe
        spec = importlib.util.spec_from_file_location(
            "live_ocr_under_test", Path(__file__).resolve().parents[1] / "pdf" / "live_ocr.py")
        live_ocr = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(live_ocr)
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            runtime = root / "known-installation" / "LocalAi"
            (runtime / "settings").mkdir(parents=True)
            binary_dir = runtime / "bin" / "test-build"
            binary_dir.mkdir(parents=True)
            for name in ("LocalAi.Broker.exe", "codesearch-mcp.exe", "locallm-mcp.exe"):
                (binary_dir / name).write_bytes(b"test-only, never executed")
            (runtime / "host.json").write_text(json.dumps({
                "BrokerAssemblyPath": str(binary_dir / "LocalAi.Broker.exe"),
                "ProcessId": 1, "StartedAtUtc": "test", "SchemaVersion": 1}), encoding="utf-8")
            (runtime / "settings" / "policy.json").write_text(
                json.dumps({"IdleModelKeepAliveSeconds": 10}), encoding="utf-8")
            corpus = root / "corpus"
            corpus.mkdir()
            (corpus / "manifest.json").write_text(json.dumps({"result": {"fixtures": []}}), encoding="utf-8")
            fixture = root / "fixture.png"
            fixture.write_bytes(b"test-only")
            fixture.with_suffix(".txt").write_text("test-only", encoding="utf-8")
            for module, arguments, server in (
                    (retention_probe, [str(corpus), str(fixture), str(root / "retention-output")], "codesearch-mcp"),
                    (live_ocr, [str(corpus)], "locallm-mcp")):
                with self.subTest(entry_point=module.__name__):
                    with patch.object(module, "installed_runtime_root", return_value=runtime):
                        with patch.object(module, "Mcp", side_effect=ProbeStopped) as start:
                            with patch.dict(os.environ, {"LOCALAPPDATA": str(root / "wrong-installation")}):
                                with patch.object(sys, "argv", [module.__file__, *arguments]):
                                    with patch.object(retention_probe, "git", return_value=""):
                                        with self.assertRaises(ProbeStopped):
                                            module.main()
                            self.assertEqual((runtime / "bin/launcher/localai-launcher.exe", server),
                                             start.call_args.args[:2])


if __name__ == "__main__":
    unittest.main()
