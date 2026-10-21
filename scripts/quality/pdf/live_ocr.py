"""Read three synthetic PDFs through the installed MCP server and shared broker."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import sys

sys.dont_write_bytecode = True
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "latency"))
from retention_probe import Mcp
from localai_installation import installed_runtime_root


def normal(text):
    return " ".join(text.split())


def distance(left, right):
    previous = list(range(len(right) + 1))
    for index, value in enumerate(left, 1):
        current = [index]
        for other, target in enumerate(right, 1):
            current.append(min(current[-1] + 1, previous[other] + 1, previous[other - 1] + (value != target)))
        previous = current
    return previous[-1]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("run", type=Path, help="existing run_probe output directory")
    parser.add_argument("--output-name", default="live-ocr", help="new result subdirectory name")
    args = parser.parse_args()
    root = args.run.resolve()
    if Path(args.output_name).name != args.output_name:
        raise ValueError("Use a single result-directory name")
    output = root / args.output_name
    output.mkdir(exist_ok=False)
    manifest = json.loads((root / "manifest.json").read_text(encoding="utf-8"))["result"]["fixtures"]
    runtime = installed_runtime_root()
    host_path = runtime / "host.json"
    before = json.loads(host_path.read_text(encoding="utf-8-sig"))
    identity_keys = ("ProcessId", "StartedAtUtc", "BrokerAssemblyPath", "SchemaVersion")
    identity = {key: before[key] for key in identity_keys}
    binary_hash = hashlib.sha256(Path(before["BrokerAssemblyPath"]).read_bytes()).hexdigest()
    (output / "host-before.json").write_text(json.dumps(identity, indent=2), encoding="utf-8")
    launcher = runtime / "bin/launcher/localai-launcher.exe"
    server = Mcp(launcher, "locallm-mcp", output)
    results = []
    try:
        for name in ["text-en.pdf", "text-unicode.pdf", "scan-en.pdf"]:
            fixture = next(item for item in manifest if item["file"] == name)
            path = root / "fixtures" / name
            if hashlib.sha256(path.read_bytes()).hexdigest() != fixture["sha256"]:
                raise ValueError("Fixture changed")
            print("Reading " + name, flush=True)
            call = server.call("read_image", {"paths": [str(path)], "question":
                "Transcribe every visible line verbatim, preserving repeated lines and the original language. Return only the transcription.",
                "mode": "Ocr", "sourceContext": "AlreadyPresent"})
            text = "\n".join(block.get("text", "") for block in call["result"].get("content", []) if block.get("type") == "text")
            notice = call["result"]["content"][0].get("text", "")
            print(notice, flush=True)
            regions = re.findall(r'<untrusted-content id="([^"]+)"[^>]*>\n(.*?)\n</untrusted-content id="\1">', text, re.S)
            answer = regions[-1][1] if regions else None
            if answer is not None:
                answer = re.sub(r"^(?:PDF page 1 of 1|Страница PDF 1 из 1)\s*\n\s*\n", "", answer)
            reference = fixture["referencePages"][0]
            observed = normal(answer) if answer is not None else None
            expected = normal(reference)
            results.append({"file": name, "sha256": fixture["sha256"], "reference": reference,
                            "answer": answer, "exactAfterWhitespaceNormalization": observed == expected,
                            "characterErrorRate": distance(expected, observed) / len(expected) if observed is not None else None,
                            "call": call})
            (output / "results.json").write_text(json.dumps(results, ensure_ascii=False, indent=2), encoding="utf-8")
    finally:
        server.close()
    after = json.loads(host_path.read_text(encoding="utf-8-sig"))
    if identity != {key: after[key] for key in identity_keys} or binary_hash != hashlib.sha256(Path(after["BrokerAssemblyPath"]).read_bytes()).hexdigest():
        raise RuntimeError("Broker host changed during measurement")
    (output / "provenance.json").write_text(json.dumps({"hostUnchanged": True,
        "brokerAssemblySha256": binary_hash, "checkedIdentityFields": identity_keys,
        "heartbeatExcluded": "HeartbeatAtUtc normally advances during work; it is not process identity.",
        "sourceContext": "AlreadyPresent", "scope": "Installed RC22 OCR baseline, separate from source-tree native-parser measurements"}, indent=2), encoding="utf-8")


if __name__ == "__main__":
    main()
