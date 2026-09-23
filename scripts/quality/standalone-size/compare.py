"""Compare self-contained publish outputs without installing either build or invoking a model."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import queue
import statistics
import subprocess
import threading
import time
import zipfile


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest().upper()


def inventory(root):
    artifacts = root / "artifacts"
    files = {path.name: {"bytes": path.stat().st_size, "sha256": digest(path)}
             for path in sorted(artifacts.iterdir()) if path.is_file()}
    installer = root / "LocalAi.Installer" / "LocalAi.Installer.exe"
    package = root / "release" / "localai-package.zip"
    with zipfile.ZipFile(package) as archive:
        names = archive.namelist()
        # Resolve the metadata name from the published archive; require exactly one extra JSON
        # member and byte-identical artifacts. The production verifier is run separately.
        metadata = set(names) - set(files)
        if len(names) != len(files) + 1 or len(metadata) != 1 or not next(iter(metadata)).endswith(".json"):
            raise ValueError("Unexpected archive layout")
        for name, info in files.items():
            with archive.open(name) as entry:
                if hashlib.file_digest(entry, "sha256").hexdigest().upper() != info["sha256"]:
                    raise ValueError("Archive artifact differs from publish output: " + name)
        metadata = json.loads(archive.read(next(iter(metadata))))
    return {"artifacts": files, "artifactBytes": sum(item["bytes"] for item in files.values()),
            "installer": {"bytes": installer.stat().st_size, "sha256": digest(installer)},
            "package": {"bytes": package.stat().st_size, "sha256": digest(package)},
            "folderInstallBytes": package.stat().st_size + installer.stat().st_size,
            "packageMetadata": metadata}


def environment_for(cache):
    environment = os.environ.copy()
    environment["PATH"] = str(Path(os.environ["SystemRoot"]) / "System32")
    environment["DOTNET_ROOT"] = str(cache / "no-global-runtime")
    environment["DOTNET_ROOT_X64"] = environment["DOTNET_ROOT"]
    environment["DOTNET_MULTILEVEL_LOOKUP"] = "0"
    environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = str(cache)
    environment["LOCALAI_LANGUAGE"] = "en"
    return environment


def launch(root, output, name, args, expected_exit, iteration):
    cache = output / (name + "-" + str(iteration))
    cache.mkdir(parents=True, exist_ok=False)
    environment = environment_for(cache)
    started = time.perf_counter()
    run = subprocess.run([str(root / "artifacts" / name), *args], cwd=cache, env=environment,
                         capture_output=True, timeout=60,
                         creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)
    elapsed = (time.perf_counter() - started) * 1000
    (cache / "stdout.log").write_bytes(run.stdout)
    (cache / "stderr.log").write_bytes(run.stderr)
    if run.returncode != expected_exit:
        raise ValueError(f"Unexpected {name} exit: {run.returncode}")
    payload = run.stdout if run.stdout else run.stderr
    if b"Usage:" not in payload:
        raise ValueError("No usage output from " + name)
    return {"elapsedMs": round(elapsed, 3), "exit": run.returncode,
            "usageSha256": hashlib.sha256(payload).hexdigest(),
            "extractedBytes": sum(path.stat().st_size for path in cache.rglob("*")
                                  if path.is_file() and path.suffix != ".log")}


def mcp_start(root, output, iteration):
    cache = output / ("codesearch-mcp-" + str(iteration))
    cache.mkdir(parents=True, exist_ok=False)
    incoming = queue.Queue()
    with (cache / "stderr.log").open("w", encoding="utf-8") as errors:
        started = time.perf_counter()
        process = subprocess.Popen([str(root / "artifacts" / "codesearch-mcp.exe")], cwd=cache,
                                   env=environment_for(cache), stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                   stderr=errors, text=True, encoding="utf-8", creationflags=subprocess.CREATE_NO_WINDOW)
        def read():
            for line in process.stdout:
                incoming.put(line)
            incoming.put(None)
        threading.Thread(target=read, daemon=True).start()
        def send(value):
            process.stdin.write(json.dumps(value) + "\n")
            process.stdin.flush()
        def response(identifier):
            deadline = time.monotonic() + 60
            while True:
                line = incoming.get(timeout=max(.01, deadline - time.monotonic()))
                if line is None:
                    raise ValueError("MCP closed before replying")
                value = json.loads(line)
                if value.get("id") == identifier:
                    if "error" in value:
                        raise ValueError("MCP refused request")
                    return value["result"]
        try:
            send({"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {
                "protocolVersion": "2024-11-05", "capabilities": {}, "clientInfo": {"name": "standalone-size", "version": "1"}}})
            info = response(1)
            elapsed = (time.perf_counter() - started) * 1000
            send({"jsonrpc": "2.0", "method": "notifications/initialized"})
            send({"jsonrpc": "2.0", "id": 2, "method": "tools/list", "params": {}})
            tools = response(2)
            payload = json.dumps({"initialize": info, "tools": tools}, sort_keys=True).encode("utf-8")
            (cache / "responses.json").write_bytes(payload)
            process.stdin.close()
            process.wait(timeout=15)
            if process.returncode != 0:
                raise ValueError("MCP did not exit cleanly")
            return {"elapsedMs": round(elapsed, 3), "exit": process.returncode,
                    "tools": len(tools["tools"]), "usageSha256": hashlib.sha256(payload).hexdigest()}
        finally:
            if process.poll() is None:
                process.kill()
                process.wait(timeout=15)


def compare(baseline, candidate, output, repetitions, require_reduction):
    output.mkdir(parents=True, exist_ok=False)
    before, after = inventory(baseline), inventory(candidate)
    if set(before["artifacts"]) != set(after["artifacts"]) or before["packageMetadata"] != after["packageMetadata"]:
        raise ValueError("Publish layouts or package metadata differ")
    report = {"schemaVersion": 1, "baseline": before, "candidate": after,
              "artifactReductionBytes": before["artifactBytes"] - after["artifactBytes"],
              "folderInstallReductionBytes": before["folderInstallBytes"] - after["folderInstallBytes"],
              "startupScope": "new_process_and_empty_bundle_cache; OS_page_cache_not_cleared",
              "sdkExposure": "PATH_system32_only; DOTNET_ROOT_points_to_missing_directory",
              "deferred": ["installer_UI_startup", "locallm_MCP_background_model_sync", "clean_machine_offline_install"],
              "cloudTokenSavings": "not_applicable_administrative_measurement", "startups": {}}
    report_path = output / "report.json"
    report_path.write_text(json.dumps(report, indent=2), encoding="utf-8")
    if require_reduction and report["artifactReductionBytes"] <= 0:
        raise ValueError("Runtime artifact bytes did not decrease")
    operations = [("localai.exe", ["--help"], 0), ("codesearch.exe", ["--help"], 0),
                  ("localai-launcher.exe", ["--help"], 2), ("LocalAi.Broker.exe", ["--help"], 2),
                  ("codesearch-mcp.exe", [], 0)]
    for name, args, expected_exit in operations:
        records = {"baseline": [], "candidate": []}
        for iteration in range(repetitions):
            order = [("baseline", baseline), ("candidate", candidate)]
            if iteration % 2:
                order.reverse()
            for label, root in order:
                records[label].append(mcp_start(root, output / label, iteration) if name == "codesearch-mcp.exe"
                                      else launch(root, output / label, name, args, expected_exit, iteration))
        if len({row["usageSha256"] for rows in records.values() for row in rows}) != 1:
            raise ValueError("Usage output differs across builds: " + name)
        report["startups"][name] = {label: {"samples": rows, "medianMs": statistics.median(row["elapsedMs"] for row in rows)}
                                    for label, rows in records.items()}
        report_path.write_text(json.dumps(report, indent=2), encoding="utf-8")
    return report


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("baseline", type=Path)
    parser.add_argument("candidate", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--repetitions", type=int, default=5, choices=range(1, 11))
    parser.add_argument("--require-reduction", action="store_true")
    args = parser.parse_args()
    result = compare(args.baseline.resolve(), args.candidate.resolve(), args.output.resolve(),
                     args.repetitions, args.require_reduction)
    print(json.dumps({"artifactReductionBytes": result["artifactReductionBytes"],
                      "folderInstallReductionBytes": result["folderInstallReductionBytes"],
                      "startups": {name: {label: entry["medianMs"] for label, entry in rows.items()}
                                   for name, rows in result["startups"].items()}}, indent=2))
