"""Validate and retain a completed retention_probe run without raw model answers."""
import argparse
import hashlib
import json
import math
from pathlib import Path
import re


def require(condition, message):
    if not condition:
        raise ValueError(message)


def blocks(result):
    # Keep nonce-bound data as data. The exporter never executes model output.
    texts = "\n".join(c["text"] for c in result["content"] if c["type"] == "text")
    return re.findall(r'<untrusted-content id="([a-z0-9]+)" origin="([^"]*)">\n(.*?)\n</untrusted-content id="\1">', texts, re.S)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("raw", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    raw = json.loads(args.raw.read_text(encoding="utf-8"))
    require(raw["complete"], "Incomplete run")
    require(raw["policy"]["IdleModelKeepAliveSeconds"] == 10, "Wrong policy")
    expected = ["index-before", "prime"]
    sequence = ["immediate", "file", "resident-after-file", "after-file", "ocr",
                "resident-after-ocr", "after-ocr", "resident-after-idle", "after-idle"]
    expected += [f"{i}:{label}" for i in range(3) for label in sequence]
    expected += ["index-after"]
    require([o["label"] for o in raw["observations"]] == expected, "Incomplete/reordered observations")
    saved = {k: v for k, v in raw.items() if k != "observations"}
    saved["rawSha256"] = hashlib.sha256(args.raw.read_bytes()).hexdigest()
    saved["observations"] = []
    search_hashes = set()
    index_paths = set()
    reference = "STAMP: OK\nSTAMP: OK"
    for row in raw["observations"]:
        result = row["result"]
        require(not result.get("isError"), row["label"] + " failed")
        require(math.isfinite(row["elapsedMs"]) and row["elapsedMs"] >= 0, "Invalid timer")
        usage = result["_meta"]["localai/usage"]
        require(usage["outcome"]["technical"] == "succeeded", "Technical failure")
        record = {k: row[k] for k in ("label", "tool", "elapsedMs")}
        record["models"] = usage["models"]
        record["timings"] = usage["timings"]
        record["usageNotice"] = result.get("_meta", {}).get("localai/usageNotice") or next(
            (c["text"] for c in result["content"] if c["type"] == "text" and "INPUT" in c["text"]), None)
        if row["tool"] == "search_code":
            found = blocks(result)
            require(len(found) == 5, "Expected five search hits")
            # Preserve all displayed content, including rounded scores, symbols, signatures,
            # chunk ids and snippets. Only unpredictable boundary nonces are excluded.
            canonical = json.dumps([(origin, body) for _, origin, body in found], ensure_ascii=False)
            record["displayedHitSha256"] = hashlib.sha256(canonical.encode("utf-8")).hexdigest()
            record["hitCount"] = len(found)
            record["firstHit"] = found[0][2].splitlines()[0]
            require("WeightPricing.cs:6-11" in record["firstHit"], "Expected relevant first hit")
            search_hashes.add(record["displayedHitSha256"])
        elif row["tool"] in ("ask_local", "read_image"):
            found = blocks(result)
            require(len(found) == 1, "Expected one protected answer")
            answer = found[0][2].replace("\r\n", "\n").strip()
            record["exactReferenceMatch"] = answer == reference
            record["referenceOccursOnce"] = answer.count(reference) == 1
            record["answerSha256"] = hashlib.sha256(answer.encode("utf-8")).hexdigest()
            # A formatting deviation is retained, never relabelled as exact success.
            require(record["referenceOccursOnce"], "Reference transcription not preserved")
        elif row["tool"] == "local_models_status":
            status = json.loads(next(c["text"] for c in result["content"] if c["type"] == "text"))
            record["residentModels"] = status["ResidentModels"]
            record["residency"] = status["Residency"]
        elif row["tool"] == "index_status":
            text = "\n".join(c["text"] for c in result["content"] if c["type"] == "text")
            require("Status:     current" in text, "Stale index")
            index_path = re.search(r"^Index:\s+(.+)$", text, re.M).group(1).strip()
            index_paths.add(index_path)
            record["indexPath"] = index_path
        saved["observations"].append(record)
    require(len(search_hashes) == 1, "Search results changed")
    require(len(index_paths) == 1, "Index changed")
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(saved, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    for label in ("immediate", "after-file", "after-ocr", "after-idle", "file", "ocr"):
        durations = sorted(r["elapsedMs"] for r in saved["observations"] if r["label"].endswith(":" + label))
        print(label, "n=", len(durations), "p50=", durations[math.ceil(.5 * len(durations)) - 1],
              "p90=", durations[math.ceil(.9 * len(durations)) - 1])


if __name__ == "__main__":
    main()
