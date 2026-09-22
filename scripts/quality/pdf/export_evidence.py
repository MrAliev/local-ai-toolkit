"""Validate and publish PDF probe results without machine-specific paths or MCP origins."""
import argparse
import hashlib
import json
from pathlib import Path


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("run", type=Path)
    parser.add_argument("destination", type=Path)
    parser.add_argument("--live-name", default="live-ocr")
    parser.add_argument("--label", required=True, help="unique measurement label, e.g. 2026-09-22")
    args = parser.parse_args()
    if Path(args.label).name != args.label or Path(args.live_name).name != args.live_name:
        raise ValueError("Use single-component names")
    native = json.loads((args.run / "evidence.json").read_text(encoding="utf-8"))
    fixtures = {item["file"]: item for item in native["fixtures"]}
    expected = {(name, mode, repeat) for name in fixtures for mode in ("text", "raster")
                for repeat in range(1, native["repeats"] + 1)}
    observed = {(run["file"], run["mode"], run["repeat"]) for run in native["runs"]}
    if observed != expected or len(native["runs"]) != len(expected):
        raise ValueError("Incomplete or duplicate native measurements")
    for fixture in fixtures.values():
        if digest(args.run / "fixtures" / fixture["file"]) != fixture["sha256"]:
            raise ValueError("Fixture changed after measurement")
    for run in native["runs"]:
        if run["processOutcome"] != "completed":
            raise ValueError("Review worker failure before publishing")
        result = run["data"]["result"]
        if result["status"] not in ("read", "rejected"):
            raise ValueError("Unknown parser outcome")
        if run["mode"] == "raster" and result["status"] == "read":
            if not result["pages"] or not result["withinProductRasterLimits"]:
                raise ValueError("Missing raster or product limit exceeded")
    live_dir = args.run / args.live_name
    provenance = json.loads((live_dir / "provenance.json").read_text(encoding="utf-8"))
    if not provenance["hostUnchanged"]:
        raise ValueError("Broker identity changed")
    live = json.loads((live_dir / "results.json").read_text(encoding="utf-8"))
    if {item["file"] for item in live} != {"text-en.pdf", "text-unicode.pdf", "scan-en.pdf"} or len(live) != 3:
        raise ValueError("Incomplete or duplicate live corpus")
    sanitized = []
    for item in live:
        if item["sha256"] != fixtures[item["file"]]["sha256"]:
            raise ValueError("Live and native fixtures differ")
        call = item["call"]
        usage = call["result"]["_meta"]["localai/usage"]
        if call["result"].get("isError") or usage["outcome"]["technical"] != "succeeded":
            raise ValueError("Live OCR failed")
        sanitized.append({**{key: value for key, value in item.items() if key != "call"},
                          "tool": call["tool"], "clientElapsedMs": call["elapsedMs"], "usage": usage})
    published = {"schemaVersion": 1, "provenance": provenance,
                 "rawResultsSha256": digest(live_dir / "results.json"),
                 "probeSourceSha256": digest(Path(__file__).with_name("live_ocr.py")),
                 "boundaries": ["Three synthetic cases, one run each; not general OCR accuracy.",
                                "Answers are untrusted model output, retained only as measurement data.",
                                "AlreadyPresent token comparisons are conditional estimates, not account usage.",
                                "Raw MCP envelopes stay in the external run directory; published answers omit only the PDF page heading."],
                 "results": sanitized}
    args.destination.mkdir(parents=True, exist_ok=True)
    for suffix, value in (("native", native), ("ocr", published)):
        path = args.destination / f"{args.label}-{suffix}.json"
        with path.open("x", encoding="utf-8", newline="\n") as stream:
            json.dump(value, stream, ensure_ascii=False, indent=2)
            stream.write("\n")
        print(path)


if __name__ == "__main__":
    main()
