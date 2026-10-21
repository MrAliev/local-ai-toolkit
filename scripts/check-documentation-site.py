"""Check rendered internal links and fragments without network requests."""
import argparse
from html.parser import HTMLParser
import json
from pathlib import Path
from urllib.parse import unquote, urlsplit


class Page(HTMLParser):
    def __init__(self):
        super().__init__()
        self.targets = []
        self.ids = set()

    def handle_starttag(self, tag, attrs):
        attributes = dict(attrs)
        if attributes.get("id"):
            self.ids.add(attributes["id"])
        if tag in ("a", "img", "script", "link"):
            target = attributes.get("href" if tag in ("a", "link") else "src")
            if target:
                self.targets.append(target)


def check(root):
    pages = {}
    for file in root.rglob("*.html"):
        page = Page()
        page.feed(file.read_text(encoding="utf-8"))
        pages[file.resolve()] = page
    failures = []
    checked = 0
    for file, page in pages.items():
        for target in page.targets:
            url = urlsplit(target)
            if url.scheme or url.netloc:
                if url.netloc != "mraliev.github.io" or not url.path.startswith("/local-ai-toolkit/"):
                    continue
            checked += 1
            path = unquote(url.path)
            if path.startswith("/local-ai-toolkit/"):
                destination = root / path[len("/local-ai-toolkit/"):]
            elif path.startswith("/"):
                destination = root / path.lstrip("/")
            elif not path:
                destination = file
            else:
                destination = file.parent / path
            if destination.is_dir():
                destination /= "index.html"
            destination = destination.resolve()
            reason = None
            if not destination.is_relative_to(root) or not destination.exists():
                reason = "missing-file"
            elif url.fragment and destination in pages and unquote(url.fragment) not in pages[destination].ids:
                reason = "missing-anchor"
            if reason:
                failures.append({"page": str(file.relative_to(root)), "target": target, "reason": reason})
    if not pages:
        failures.append({"reason": "no-rendered-pages"})
    return {"pages": len(pages), "internalLinks": checked, "failureCount": len(failures), "failures": failures}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("root", type=Path)
    parser.add_argument("--report", type=Path)
    args = parser.parse_args()
    report = check(args.root.resolve())
    if args.report:
        args.report.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({**report, "failures": report["failures"][:20]}, ensure_ascii=False, indent=2))
    raise SystemExit(1 if report["failureCount"] else 0)
