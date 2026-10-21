"""Bounded MCP retention experiment. All inference uses the shared LocalAi broker."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import queue
import re
import subprocess
import threading
import time

from localai_installation import installed_runtime_root


def digest(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def git(root, *args):
    return subprocess.check_output(["git", "-C", str(root), *args], text=True).strip()


class Mcp:
    def __init__(self, launcher, server, output):
        self.errors = (output / (server + ".stderr.log")).open("w", encoding="utf-8")
        self.process = subprocess.Popen(
            [str(launcher), "run", server], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
            stderr=self.errors, text=True, encoding="utf-8",
            creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)
        self.incoming = queue.Queue()
        self.identifier = 0
        threading.Thread(target=self.read, daemon=True).start()
        self.info = self.request("initialize", {"protocolVersion": "2024-11-05",
            "capabilities": {}, "clientInfo": {"name": "retention-probe", "version": "1"}})
        self.send({"jsonrpc": "2.0", "method": "notifications/initialized"})
        self.tools = self.request("tools/list", {})

    def read(self):
        for line in self.process.stdout:
            self.incoming.put(line)
        self.incoming.put(None)

    def send(self, value):
        self.process.stdin.write(json.dumps(value) + "\n")
        self.process.stdin.flush()

    def request(self, method, params):
        self.identifier += 1
        self.send({"jsonrpc": "2.0", "id": self.identifier, "method": method, "params": params})
        deadline = time.monotonic() + 300
        while True:
            line = self.incoming.get(timeout=max(.01, deadline - time.monotonic()))
            if line is None:
                raise RuntimeError("MCP closed stdout")
            value = json.loads(line)
            if value.get("id") == self.identifier:
                if "error" in value:
                    raise RuntimeError(value["error"])
                return value["result"]

    def call(self, name, args):
        start = time.perf_counter()
        result = self.request("tools/call", {"name": name, "arguments": args})
        return {"tool": name, "elapsedMs": round((time.perf_counter() - start) * 1000, 3),
                "result": result}

    def close(self):
        self.process.stdin.close()
        try:
            self.process.wait(timeout=15)
        except subprocess.TimeoutExpired:
            self.process.terminate()
            self.process.wait(timeout=15)
        self.errors.close()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("corpus", type=Path)
    parser.add_argument("fixture", type=Path, help="short-repeated-en.png and adjacent .txt")
    parser.add_argument("output", type=Path, help="new directory outside corpus")
    parser.add_argument("--inspect", action="store_true")
    args = parser.parse_args()
    corpus = args.corpus.resolve()
    output = args.output.resolve()
    if output.is_relative_to(corpus):
        raise ValueError("Output must be outside corpus")
    if git(corpus, "status", "--porcelain"):
        raise ValueError("Corpus must be clean")
    output.mkdir(parents=True, exist_ok=False)
    runtime = installed_runtime_root()
    launcher = runtime / "bin/launcher/localai-launcher.exe"
    policy = runtime / "settings/policy.json"
    host = runtime / "host.json"
    policy_before = policy.read_text(encoding="utf-8-sig")
    if json.loads(policy_before)["IdleModelKeepAliveSeconds"] != 10:
        raise ValueError("This experiment requires the unchanged 10-second idle policy")
    host_before = json.loads(host.read_text(encoding="utf-8-sig"))
    binary_dir = Path(host_before["BrokerAssemblyPath"]).parent
    binaries = ["LocalAi.Broker.exe", "codesearch-mcp.exe", "locallm-mcp.exe"]
    hashes = {name: digest(binary_dir / name) for name in binaries}
    commit = git(corpus, "rev-parse", "HEAD")
    image_hash = digest(args.fixture)
    reference = args.fixture.with_suffix(".txt")
    reference_hash = digest(reference)
    servers = []
    try:
        search = Mcp(launcher, "codesearch-mcp", output)
        servers.append(search)
        local = Mcp(launcher, "locallm-mcp", output)
        servers.append(local)
        (output / "tools.json").write_text(json.dumps([search.tools, local.tools], indent=2), encoding="utf-8")
        if args.inspect:
            print(json.dumps([t for s in servers for t in s.tools["tools"]
                              if t["name"] in ("search_code", "read_image", "ask_local")], indent=2))
            return
        report = {"schemaVersion": 1, "corpusCommit": commit, "policy": json.loads(policy_before),
                  "hostBefore": {k: host_before[k] for k in ("ProcessId", "StartedAtUtc", "BrokerAssemblyPath")},
                  "binaryHashes": hashes, "probeSha256": digest(__file__),
                  "imageSha256": image_hash, "referenceSha256": reference_hash,
                  "servers": [s.info for s in servers], "observations": [], "complete": False}
        def call(client, label, name, arguments):
            observation = client.call(name, arguments)
            observation["label"] = label
            report["observations"].append(observation)
            (output / "raw.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
            meta = observation["result"].get("_meta", {})
            notice = meta.get("localai/usageNotice")
            if not notice:
                notice = next((c["text"] for c in observation["result"].get("content", [])
                               if c.get("type") == "text" and "INPUT" in c["text"]), "")
            print(json.dumps({"label": label, "elapsedMs": observation["elapsedMs"], "notice": notice}, ensure_ascii=False), flush=True)
            if observation["result"].get("isError"):
                raise RuntimeError(observation["result"])
            return observation
        call(search, "index-before", "index_status", {"root": str(corpus)})
        query = "Calculate the parcel delivery charge using kilograms and an extra fee for large parcels"
        search_args = {"root": str(corpus), "query": query, "topK": 5, "maxPerFile": 2,
                       "sourceContext": "AlreadyPresent"}
        call(search, "prime", "search_code", search_args)
        for cycle in range(3):
            prefix = str(cycle) + ":"
            call(search, prefix + "immediate", "search_code", search_args)
            call(local, prefix + "file", "ask_local", {"prompt": "Return the file contents exactly, preserving both repeated lines. No explanation.",
                 "files": [str(reference.resolve())], "taskProfile": "Extraction", "sourceContext": "AlreadyPresent"})
            call(local, prefix + "resident-after-file", "local_models_status", {})
            call(search, prefix + "after-file", "search_code", search_args)
            call(local, prefix + "ocr", "read_image", {"paths": [str(args.fixture.resolve())],
                 "question": "Transcribe all text exactly, preserving repeated lines. Return only the transcription.",
                 "mode": "Ocr", "sourceContext": "AlreadyPresent"})
            call(local, prefix + "resident-after-ocr", "local_models_status", {})
            call(search, prefix + "after-ocr", "search_code", search_args)
            print(prefix + "idle 15 seconds, no broker requests", flush=True)
            time.sleep(15)
            call(local, prefix + "resident-after-idle", "local_models_status", {})
            call(search, prefix + "after-idle", "search_code", search_args)
        call(search, "index-after", "index_status", {"root": str(corpus)})
        if git(corpus, "rev-parse", "HEAD") != commit or git(corpus, "status", "--porcelain"):
            raise ValueError("Corpus changed")
        if policy.read_text(encoding="utf-8-sig") != policy_before:
            raise ValueError("Policy changed")
        host_after = json.loads(host.read_text(encoding="utf-8-sig"))
        if any(host_after[k] != v for k, v in report["hostBefore"].items()):
            raise ValueError("Broker process changed")
        if hashes != {name: digest(binary_dir / name) for name in binaries}:
            raise ValueError("Broker binaries changed")
        if image_hash != digest(args.fixture) or reference_hash != digest(reference):
            raise ValueError("Fixture changed")
        report["complete"] = True
        (output / "raw.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    finally:
        for server in reversed(servers):
            server.close()


if __name__ == "__main__":
    main()
