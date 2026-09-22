"""Measure synthetic PDFs in isolated workers. No model inference occurs in this probe."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import subprocess
import time


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def normal(text):
    return " ".join(text.split())


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output", type=Path, help="new directory outside the repository")
    parser.add_argument("--repeats", type=int, default=3)
    parser.add_argument("--timeout", type=float, default=10)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[3]
    output = args.output.resolve()
    if output.is_relative_to(root) or args.repeats < 1 or args.timeout <= 0:
        raise ValueError("Use an external directory, positive repeats and timeout")
    output.mkdir(parents=True, exist_ok=False)
    binary = root / "scripts/quality/pdf/bin/Release/net10.0/PdfProbe.dll"
    flags = subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0
    fixtures = output / "fixtures"
    generated = subprocess.run(["dotnet", str(binary), "generate", str(fixtures)], capture_output=True,
                               text=True, encoding="utf-8", timeout=30, check=True, creationflags=flags)
    manifest = json.loads(generated.stdout)
    if manifest["result"]["status"] != "generated":
        raise RuntimeError(manifest)
    (output / "manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8")
    reports = []
    for fixture in manifest["result"]["fixtures"]:
        source = fixtures / fixture["file"]
        if digest(source) != fixture["sha256"]:
            raise ValueError("Fixture hash changed")
        for mode in ("text", "raster"):
            for repeat in range(args.repeats):
                start = time.perf_counter()
                stem = f"{source.stem}-{mode}-{repeat + 1}"
                with (output / f"{stem}.stdout.json").open("w", encoding="utf-8") as stdout, \
                        (output / f"{stem}.stderr.log").open("w", encoding="utf-8") as stderr:
                    try:
                        process = subprocess.run(["dotnet", str(binary), mode, str(source)], stdout=stdout,
                                                 stderr=stderr, timeout=args.timeout, creationflags=flags)
                        outcome = "completed" if process.returncode == 0 else "crashed"
                        exit_code = process.returncode
                    except subprocess.TimeoutExpired:
                        outcome, exit_code = "timeout", None
                wall_ms = round((time.perf_counter() - start) * 1000, 3)
                data = None
                if outcome == "completed":
                    data = json.loads((output / f"{stem}.stdout.json").read_text(encoding="utf-8-sig"))
                    result = data["result"]
                    if "message" in result:
                        result["message"] = result["message"].replace(str(fixtures), "<fixtures>")
                    if mode == "text" and result["status"] == "read":
                        expected = fixture["referencePages"]
                        result["matchesVisibleReference"] = ([normal(page) for page in result["pages"]] ==
                            [normal(page) for page in expected]) if expected else None
                        result["pageMatchesVisibleReference"] = [normal(page) == normal(reference)
                            for page, reference in zip(result["pages"], expected)]
                    if mode == "raster" and result["status"] == "read":
                        result["withinProductRasterLimits"] = all(
                            page["pixels"] <= 4_000_000 and page["Width"] <= 4096 and page["Height"] <= 4096
                            for page in result["pages"])
                reports.append({"file": fixture["file"], "mode": mode, "repeat": repeat + 1,
                                "processOutcome": outcome, "exitCode": exit_code, "wallMs": wall_ms, "data": data})
                print(f"{stem}: {outcome}, {wall_ms:.1f} ms", flush=True)
        if digest(source) != fixture["sha256"]:
            raise ValueError("Probe modified a fixture")
    assets = [binary, binary.parent / "LocalLm.Core.dll", binary.parent / "PDFtoImage.dll",
              binary.parent / "runtimes/win-x64/native/pdfium.dll"]
    evidence = {
        "schemaVersion": 1,
        "sourceCommit": subprocess.check_output(["git", "-C", str(root), "rev-parse", "HEAD"], text=True).strip(),
        "probeSourceSha256": {path.name: digest(path) for path in [Path(__file__), Path(__file__).with_name("Program.cs")]},
        "binarySha256": {path.name: digest(path) for path in assets},
        "host": {"system": platform.platform(), "processor": platform.processor(), "logicalProcessors": os.cpu_count()},
        "repeats": args.repeats, "workerTimeoutSeconds": args.timeout,
        "boundaries": ["Synthetic fixtures, one Windows host; not a security proof or a general PDF-quality score.",
                       "Native loading/text extraction and the product raster pipeline run in separate processes.",
                       "Raster mode replaces only the model client with a capture sink; there is no inference or OCR-accuracy score.",
                       "Process wall time includes startup; inner timings have their own narrower boundary.",
                       "The OS file cache may be warm. Worker peak working set is measured, not a hard memory limit.",
                       "Visible-reference comparison collapses whitespace but retains characters, repetitions and order."],
        "fixtures": manifest["result"]["fixtures"], "runs": reports,
    }
    (output / "evidence.json").write_text(json.dumps(evidence, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"Evidence: {output / 'evidence.json'}", flush=True)


if __name__ == "__main__":
    main()
