#!/usr/bin/env python3
"""Regenerates diagnostic-codes.json from the upstream documentation repositories.

    python build-diagnostic-codes.py            # writes diagnostic-codes.json beside this script

Downloads the NuGet docs tarball and the MSBuild error pages into a temporary directory, pulls the
message and the explanation out of each page, and writes the table the catalog embeds. Only codes a
build can actually be told to ignore are kept: every NU code, and the MSB pages that describe a
warning — the several hundred MSB pages that describe errors are dead weight in a NoWarn list.

Provenance, licence and the shape of the output are in README.md beside this file.
"""

import json
import os
import re
import shutil
import sys
import tarfile
import tempfile
import time
import urllib.request
from concurrent.futures import ThreadPoolExecutor

NUGET_TARBALL = "https://codeload.github.com/NuGet/docs.microsoft.com-nuget/tar.gz/refs/heads/main"
NUGET_DOCS = "docs/reference/errors-and-warnings/"
MSBUILD_LISTING = (
    "https://api.github.com/repos/MicrosoftDocs/visualstudio-docs/contents/docs/msbuild/errors"
)

OUTPUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "diagnostic-codes.json")


def fetch(url, binary=False, attempts=4):
    """One page. Retried: raw.githubusercontent answers a burst of parallel reads with 502s."""
    request = urllib.request.Request(url, headers={"User-Agent": "roslynsense-docs-import"})

    for attempt in range(attempts):
        try:
            with urllib.request.urlopen(request, timeout=120) as response:
                payload = response.read()
            return payload if binary else payload.decode("utf-8")
        except (urllib.error.HTTPError, urllib.error.URLError) as error:
            if attempt == attempts - 1:
                raise
            print(f"  retrying {url} ({error})", file=sys.stderr)
            time.sleep(2 ** attempt)


def strip_markdown(text):
    text = re.sub(r"<[^>]+>", "", text)
    text = re.sub(r"\[!INCLUDE[^\]]*\][^\n]*", "", text)
    text = re.sub(r"\[([^\]]+)\]\([^)]*\)", r"\1", text)
    text = text.replace("&nbsp;", " ").replace("`", "")
    return re.sub(r"\s+", " ", text).strip()


def split_frontmatter(text):
    match = re.match(r"^---\n(.*?)\n---\n", text, re.S)
    return (match.group(1), text[match.end():]) if match else ("", text)


def paragraph_after(body, heading):
    match = re.search(heading, body, re.I | re.M)
    if not match:
        return None

    lines = []
    for line in body[match.end():].splitlines():
        stripped = line.strip()
        if stripped.startswith("#"):
            break
        if stripped.startswith("<!--") or stripped.startswith("[!"):
            continue
        if not stripped:
            if lines:
                break
            continue
        lines.append(stripped)

    return strip_markdown(" ".join(lines)) or None


def first_blockquote(body):
    lines, started = [], False
    for line in body.splitlines():
        stripped = line.strip()
        if stripped.startswith(">"):
            started = True
            lines.append(stripped.lstrip(">").strip())
        elif started:
            break
    return strip_markdown(" ".join(lines)) or None


def clip(text, limit):
    """One or two sentences. A hover is a popup, not the documentation page."""
    if not text:
        return None

    text = re.split(r"<!--", text)[0].strip()
    if len(text) <= limit:
        return text

    head = text[:limit]
    stop = max(head.rfind(". "), head.rfind("? "), head.rfind("! "))
    return head[: stop + 1] if stop > limit * 0.4 else head.rstrip() + "…"


def read_nuget(directory):
    entries = {}
    root = os.path.join(directory, "nuget")

    for name in sorted(os.listdir(root)):
        codes = re.findall(r"NU\d{4}", name.upper())
        if not name.endswith(".md") or not codes:
            continue

        frontmatter, body = split_frontmatter(open(os.path.join(root, name), encoding="utf-8").read())
        title = re.search(r"^title:\s*(.+)$", frontmatter, re.M)
        title = title.group(1) if title else ""

        entry = {}
        if "Error" in title:
            entry["severity"] = "error"
        elif "Warning" in title:
            entry["severity"] = "warning"

        if message := clip(first_blockquote(body), 200):
            entry["message"] = message
        if description := clip(paragraph_after(body, r"^#+\s*Issue\s*$"), 260):
            entry["description"] = description

        for code in codes:
            entries[code] = entry

    return entries


def read_msbuild(directory):
    entries = {}
    root = os.path.join(directory, "msbuild")

    for name in sorted(os.listdir(root)):
        code = name[:-3].upper()
        if not name.endswith(".md") or not re.fullmatch(r"MSB\d{4}", code):
            continue

        text = open(os.path.join(root, name), encoding="utf-8").read()
        if not re.search(r"warning", text, re.I):
            continue

        _, body = split_frontmatter(text)
        description = paragraph_after(body, r"^##\s*Description\s*$") \
            or paragraph_after(body, r"^##\s*Message text\s*$")

        entry = {"severity": "warning"}
        if description := clip(description, 220):
            entry["description"] = description

        entries[code] = entry

    return entries


def download(directory):
    os.makedirs(os.path.join(directory, "nuget"))
    os.makedirs(os.path.join(directory, "msbuild"))

    archive = os.path.join(directory, "nuget-docs.tar.gz")
    with open(archive, "wb") as file:
        file.write(fetch(NUGET_TARBALL, binary=True))

    with tarfile.open(archive) as tar:
        for member in tar.getmembers():
            if NUGET_DOCS in member.name and member.name.endswith(".md"):
                source = tar.extractfile(member)
                if source is None:
                    continue
                target = os.path.join(directory, "nuget", os.path.basename(member.name))
                with open(target, "wb") as file:
                    shutil.copyfileobj(source, file)

    listing = json.loads(fetch(MSBUILD_LISTING))
    urls = sorted({entry["download_url"] for entry in listing if entry["name"].endswith(".md")})
    print(f"  {len(urls)} MSBuild pages", file=sys.stderr)

    def page(url):
        name = url.rsplit("/", 1)[-1]
        with open(os.path.join(directory, "msbuild", name), "w", encoding="utf-8") as file:
            file.write(fetch(url))

    with ThreadPoolExecutor(max_workers=8) as pool:
        list(pool.map(page, urls))


def main():
    with tempfile.TemporaryDirectory() as directory:
        print("Downloading upstream documentation…", file=sys.stderr)
        download(directory)

        entries = read_nuget(directory)
        entries.update(read_msbuild(directory))

    with open(OUTPUT, "w", encoding="utf-8") as file:
        json.dump(entries, file, indent=0, ensure_ascii=False, sort_keys=True)
        file.write("\n")

    print(f"{len(entries)} codes → {OUTPUT} ({os.path.getsize(OUTPUT)} bytes)", file=sys.stderr)


if __name__ == "__main__":
    main()
