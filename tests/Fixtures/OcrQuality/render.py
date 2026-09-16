"""Regenerate the retained OCR reference images using an explicitly selected font."""
import argparse
import hashlib
import json
from pathlib import Path

import PIL
from PIL import Image, ImageDraw, ImageFont


def sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def samples():
    yield "short-repeated-en", "en", "short", ["STAMP: OK", "STAMP: OK"], [
        {"label": "STAMP", "value": "OK"}, {"label": "STAMP", "value": "OK"}
    ]
    yield "short-fields-ru", "ru", "short", ["КОД: 42", "СТАТУС: ГОТОВО"], [
        {"label": "КОД", "value": "42"}, {"label": "СТАТУС", "value": "ГОТОВО"}
    ]
    for language in ("en", "ru"):
        english = language == "en"
        batch = "EN-2026-0916" if english else "RU-2026-0916"
        lines = (["WAREHOUSE CHECK", f"Batch: {batch}", "Location: North depot", "Date: 2026-09-16", "Status: READY", ""]
                 if english else ["СВЕРКА СКЛАДА", f"Партия: {batch}", "Склад: Северный", "Дата: 2026-09-16", "Статус: ГОТОВО", ""])
        fields = [{"label": "Batch" if english else "Партия", "value": batch},
                  {"label": "Status" if english else "Статус", "value": "READY" if english else "ГОТОВО"}]
        for number in range(1, 33):
            label = f"ITEM {number:02d}" if english else f"ПОЗИЦИЯ {number:02d}"
            value = (f"P{number:03d} | Quantity {number % 5 + 1} | Zone {chr(65 + number % 3)}"
                     if english else f"P{number:03d} | Количество {number % 5 + 1} | Зона {chr(65 + number % 3)}")
            lines.append(f"{label}: {value}")
            if number in (1, 16, 32):
                fields.append({"label": label, "value": value})
        lines += (["", "TOTAL ITEMS: 32", "Verifier: ANTON", "END OF REPORT"]
                  if english else ["", "ВСЕГО ПОЗИЦИЙ: 32", "Проверил: АНТОН", "КОНЕЦ ОТЧЕТА"])
        fields += [{"label": "TOTAL ITEMS" if english else "ВСЕГО ПОЗИЦИЙ", "value": "32"},
                   {"label": "Verifier" if english else "Проверил", "value": "ANTON" if english else "АНТОН"}]
        yield f"long-fields-{language}", language, "long", lines, fields


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--font", required=True, type=Path)
    parser.add_argument("--output", type=Path, default=Path(__file__).parent)
    arguments = parser.parse_args()
    arguments.output.mkdir(parents=True, exist_ok=True)
    corpus = {"schemaVersion": 1, "renderer": {"library": "Pillow", "version": PIL.__version__,
               "fontFileName": arguments.font.name, "fontSha256": sha256(arguments.font)}, "cases": []}
    for identifier, language, size, lines, fields in samples():
        width, height, pixels, step = (900, 300, 44, 70) if size == "short" else (1200, 1900, 28, 42)
        image = Image.new("RGB", (width, height), "white")
        draw = ImageDraw.Draw(image)
        font = ImageFont.truetype(str(arguments.font), pixels)
        bounds = []
        for index, line in enumerate(lines):
            if not line:
                continue
            position = (60, 50 + index * step)
            box = draw.textbbox(position, line, font=font)
            if box[0] < 0 or box[1] < 0 or box[2] > width - 40 or box[3] > height - 40:
                raise ValueError(f"Reference line does not fit: {identifier}:{index + 1}")
            draw.text(position, line, fill="black", font=font)
            bounds.append({"line": index + 1, "box": list(box)})
        image_path = arguments.output / f"{identifier}.png"
        text_path = arguments.output / f"{identifier}.txt"
        image.save(image_path, compress_level=9)
        text_path.write_text("\n".join(lines) + "\n", encoding="utf-8", newline="\n")
        corpus["cases"].append({"id": identifier, "language": language, "length": size,
            "image": image_path.name, "reference": text_path.name, "width": width, "height": height,
            "fontPixels": pixels, "imageSha256": sha256(image_path), "referenceSha256": sha256(text_path),
            "lineBoxes": bounds, "fields": fields})
    (arguments.output / "corpus.json").write_text(json.dumps(corpus, indent=2, ensure_ascii=False) + "\n", encoding="utf-8", newline="\n")
    print(f"Rendered {len(corpus['cases'])} OCR reference pages; all text boxes fit.")


if __name__ == "__main__":
    main()
