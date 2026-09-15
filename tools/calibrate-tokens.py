"""Reproduce the synthetic token reference report; never reads arbitrary user files.

Install tiktoken==0.12.0, then run this script with --verify (read-only) or --write.
The normal .NET test suite consumes the committed references without Python/network/GPU.
"""
import argparse
import hashlib
import json
import math
from pathlib import Path
import struct
import unicodedata
import zlib
import tiktoken

ROOT = Path(__file__).resolve().parents[1] / "tests/Fixtures/TokenCalibration"
RANK_HASHES = {
    "cl100k_base": "223921b76ee99bde995b7ff738513eef100fb51d18c93597a113bcffe865b2a7",
    "o200k_base": "446a9538cb6c348e3516120d7c08b09f57c36495e2acfffe59a5bf8b0cfb1a2d",
}


def sha(data):
    return hashlib.sha256(data).hexdigest()


def measures(text):
    characters = len(text.encode("utf-16-le")) // 2
    letters = sum(unicodedata.category(ch).startswith("L") for ch in text)
    cyrillic = sum(unicodedata.category(ch).startswith("L") and 0x400 <= ord(ch) <= 0x4ff for ch in text)
    share = cyrillic / letters if letters else 0
    return {"sha256": sha(text.encode()), "utf8Bytes": len(text.encode()),
            "utf16Characters": characters, "letters": letters, "cyrillicLetters": cyrillic,
            "weightedEstimate": math.ceil(characters / (4 * (1 - share) + 2.2 * share)),
            "characterProxy": (characters + 3) // 4,
            "heuristicLower": (characters + 5) // 6, "heuristicUpper": (characters + 2) // 3}


def png(width, height):
    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data))
    # A deterministic checkerboard in 8-bit grayscale; no image library needed.
    pixels = b"".join(b"\0" + bytes(224 if (x // 16 + y // 16) % 2 else 32 for x in range(width)) for y in range(height))
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 0, 0, 0, 0)) + chunk(b"IDAT", zlib.compress(pixels, 9)) + chunk(b"IEND", b"")


def pdf(document):
    objects = [b"<< /Type /Catalog /Pages 2 0 R >>", b"<< /Type /Pages /Kids [3 0 R 5 0 R] /Count 2 >>"]
    for text in document["pages"]:
        content_id = len(objects) + 2
        objects.append(f"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {document['widthPoints']} {document['heightPoints']}] /Resources << /Font << /F1 7 0 R >> >> /Contents {content_id} 0 R >>".encode())
        stream = f"BT /F1 10 Tf 40 720 Td ({text}) Tj ET\n".encode()
        objects.append(f"<< /Length {len(stream)} >>\nstream\n".encode() + stream + b"endstream")
    objects.append(b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>")
    data = b"%PDF-1.4\n"
    offsets = [0]
    for index, obj in enumerate(objects, 1):
        offsets.append(len(data))
        data += f"{index} 0 obj\n".encode() + obj + b"\nendobj\n"
    start = len(data)
    data += f"xref\n0 {len(offsets)}\n0000000000 65535 f \n".encode()
    data += b"".join(f"{offset:010} 00000 n \n".encode() for offset in offsets[1:])
    return data + f"trailer\n<< /Size {len(offsets)} /Root 1 0 R >>\nstartxref\n{start}\n%%EOF\n".encode()


def image_formula(width, height, detail):
    if detail == "low":
        return 85
    scale = min(1, 2048 / max(width, height))
    width, height = width * scale, height * scale
    scale = min(1, 768 / min(width, height))
    width, height = math.floor(width * scale), math.floor(height * scale)
    return 85 + 170 * math.ceil(width / 512) * math.ceil(height / 512)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument("--verify", action="store_true")
    mode.add_argument("--write", action="store_true")
    args = parser.parse_args()
    if tiktoken.__version__ != "0.12.0":
        raise SystemExit("This reference requires tiktoken==0.12.0.")
    corpus = json.loads((ROOT / "corpus.json").read_text(encoding="utf-8"))
    encodings = {name: tiktoken.get_encoding(name) for name in RANK_HASHES}
    texts = {}
    for case in corpus["texts"]:
        unit = case.get("text")
        if unit is None:
            unit = "".join(texts[name] + "\n" for name in case["parts"])
        texts[case["id"]] = unit * case["repeat"]
    observations = {}

    def observe(text):
        data = measures(text)
        observations[data["sha256"]] = {**data, "tokens": {name: len(encoding.encode_ordinary(text)) for name, encoding in encodings.items()}}
        return data["sha256"]

    scenarios = []
    for case in corpus["scenarios"]:
        source, output = texts[case["source"]], texts[case["output"]]
        arguments = {"source": source} if case["arguments"] == "source" else {"path": "input.txt"}
        if case["delivery"] == "file":
            arguments["outPath"] = "output.txt"
        arguments = json.dumps(arguments, ensure_ascii=False, separators=(",", ":"))
        body = "Saved output.txt." if case["delivery"] == "file" else '<untrusted-content id="fixture">' + output + '</untrusted-content id="fixture">'
        payload = texts["notice"] + "\n" + body
        additional_input = arguments + ("\n" + output if case["readOutput"] else "")
        hashes = {"source": observe(source), "output": observe(output), "arguments": observe(arguments),
                  "additionalInput": observe(additional_input), "payload": observe(payload)}
        expected = {}
        for name in encodings:
            def count(key):
                return observations[hashes[key]]["tokens"][name]
            before = 0 if case["presence"] == "AlreadyPresent" else count("source") if case["presence"] == "NotPresent" else None
            after = count("additionalInput") + count("payload")
            expected[name] = {"inputWithoutDelegation": before, "inputWithDelegation": after,
                              "inputSaved": None if before is None else before - after,
                              "outputWithoutDelegation": count("output"), "outputWithDelegation": count("arguments"),
                              "outputSaved": count("output") - count("arguments")}
        scenarios.append({"id": case["id"], "hashes": hashes, "expected": expected})
    cases = []
    for name, text in texts.items():
        cases.append({"id": name, **observations[observe(text)]})
    assets = {case["path"]: png(case["width"], case["height"]) for case in corpus["images"]}
    assets[corpus["pdf"]["path"]] = pdf(corpus["pdf"])
    image_cases = [{**case, "sha256": sha(assets[case["path"]]), "referenceTokens": image_formula(case["width"], case["height"], case["detail"]),
                    "referenceStatus": "published_formula_estimate", "legacyAreaEstimate": math.ceil(case["width"] * case["height"] / 750)} for case in corpus["images"]]
    report = {
        "schemaVersion": 1, "tiktokenVersion": tiktoken.__version__, "rankFileSha256": RANK_HASHES,
        "textScope": "Plain text counted with encode_ordinary, without message framing or provider billing. Scenario inputs retain tool arguments in the next context; outputs count their generation separately. Payloads are complete synthetic text responses, not captured live MCP traffic.",
        "visionProfile": "gpt-4o-tiles-85-170-docs-2026-09-16",
        "visionSource": "https://developers.openai.com/api/docs/guides/images-vision",
        "pdfSource": "https://developers.openai.com/api/docs/guides/file-inputs",
        "rangeBasis": "heuristic_not_confidence_interval", "texts": cases,
        "observations": observations, "scenarios": scenarios, "images": image_cases,
        "pdf": {"sha256": sha(assets[corpus["pdf"]["path"]]), "pages": len(corpus["pdf"]["pages"]),
                "textTokens": {name: sum(len(enc.encode_ordinary(page)) for page in corpus["pdf"]["pages"]) for name, enc in encodings.items()},
                "fullInputTokens": None, "reason": "provider_page_rendering_unknown"},
        "summary": {name: {"cases": len(cases),
                            "weightedAbsoluteErrorSum": sum(abs(case["weightedEstimate"] - case["tokens"][name]) for case in cases),
                            "characterRangeMisses": [case["id"] for case in cases if not case["heuristicLower"] <= case["tokens"][name] <= case["heuristicUpper"]]}
                    for name in encodings},
    }
    assets["reference.json"] = (json.dumps(report, ensure_ascii=False, indent=2) + "\n").encode()
    for name, data in assets.items():
        path = ROOT / name
        if args.write:
            path.write_bytes(data)
        # Git owns checkout text endings. Only the binary fixtures require byte identity.
        elif (path.read_text(encoding="utf-8").encode() if name.endswith(".json") else path.read_bytes()) != data:
            raise SystemExit(f"Reference mismatch: {name}")
    print(f"Verified {len(cases)} text cases, {len(scenarios)} scenarios, {len(image_cases)} image cases and one PDF with tiktoken {tiktoken.__version__}.")


if __name__ == "__main__":
    main()
