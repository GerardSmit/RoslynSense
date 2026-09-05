#!/usr/bin/env python3
"""Validate and time DNN completion, navigation, buffer reuse, and property rename.

Python 3.10+, standard library only. Every edit stays in LSP buffers; no DNN
source file is written. Uses the standalone production server, not an editor's
existing daemon.
"""

import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import statistics
import time
from urllib.parse import urlparse
from urllib.request import url2pathname

from lsp_repository import LspClient, position, result_count


OLD_NAME = "RequestsRemoval"
NEW_NAME = "RoslynSenseBenchmarkRemovalFlag"
PROBE_NAME = "RoslynSenseBenchmarkProbe"


def offset(text, point):
    """Convert an LSP UTF-16 position, rejecting partial surrogate positions."""
    lines = text.splitlines(keepends=True)
    if text.endswith("\n") or not lines:
        lines.append("")
    line, character = point["line"], point["character"]
    if line < 0 or line >= len(lines) or character < 0:
        raise ValueError(f"Invalid LSP position {point}")
    content = lines[line].rstrip("\r\n")
    encoded = content.encode("utf-16-le")
    if character * 2 > len(encoded):
        raise ValueError(f"LSP position beyond line {point}")
    prefix = encoded[:character * 2].decode("utf-16-le")
    return sum(len(value) for value in lines[:line]) + len(prefix)


def span(text, start, length):
    return {"start": position(text, start), "end": position(text, start + length)}


def apply_edits(text, edits):
    replacements = sorted((offset(text, edit["range"]["start"]),
                           offset(text, edit["range"]["end"]), edit["newText"])
                          for edit in edits)
    previous_end = -1
    for start, end, _ in replacements:
        if end < start or start < previous_end:
            raise ValueError("Invalid or overlapping workspace text edits")
        previous_end = end
    for start, end, value in reversed(replacements):
        text = text[:start] + value + text[end:]
    return text


def uri_path(uri):
    parsed = urlparse(uri)
    if parsed.scheme != "file" or parsed.netloc not in ("", "localhost"):
        raise ValueError(f"Expected local source URI, got {uri}")
    return Path(url2pathname(parsed.path)).resolve(strict=True)


def source_manifest(repository):
    manifest = {}
    for directory, directories, files in os.walk(repository):
        directories[:] = [name for name in directories if name.lower() not in
                          ("bin", "obj", ".git", ".vs", "node_modules")]
        for name in files:
            if name.lower().endswith(".cs"):
                path = (Path(directory) / name).resolve(strict=True)
                manifest[path] = hashlib.sha256(path.read_bytes()).hexdigest()
    return manifest


class Buffers:
    def __init__(self, client, repository, manifest):
        self.client = client
        self.repository = repository
        self.manifest = manifest
        self.original = {}
        self.current = {}
        self.versions = {}
        self.opened = set()
        self.events = []

    def read(self, path):
        path = path.resolve(strict=True)
        if not path.is_relative_to(self.repository) or path.suffix.lower() != ".cs":
            raise ValueError(f"Unexpected rename target outside DNN C# sources: {path}")
        if path not in self.original:
            raw = path.read_bytes()
            text = raw.decode("utf-8-sig")
            sha256 = hashlib.sha256(raw).hexdigest()
            if self.manifest.get(path) != sha256:
                raise ValueError(f"Source changed since pre-server manifest, or unexpected generated target: {path}")
            self.original[path] = {"text": text, "sha256": sha256}
            self.current[path] = text
            self.versions[path] = 0
        return self.current[path]

    def open(self, path, text=None):
        self.read(path)
        if path in self.opened:
            raise ValueError(f"Document already open: {path}")
        if text is not None:
            self.current[path] = text
        self.versions[path] += 1
        self.client.notify("textDocument/didOpen", {"textDocument": {
            "uri": path.as_uri(), "languageId": "csharp", "version": self.versions[path],
            "text": self.current[path]}})
        self.opened.add(path)
        self.events.append({"method": "didOpen", "file": str(path), "version": self.versions[path]})

    def change(self, path, text):
        old = self.read(path)
        if path not in self.opened:
            self.open(path, text)
            return
        # A real ranged incremental change, against the preceding version.
        prefix = 0
        while prefix < min(len(old), len(text)) and old[prefix] == text[prefix]:
            prefix += 1
        suffix = 0
        while suffix < min(len(old), len(text)) - prefix and old[-suffix - 1] == text[-suffix - 1]:
            suffix += 1
        old_end, new_end = len(old) - suffix, len(text) - suffix
        change_range = span(old, prefix, old_end - prefix)
        change_text = text[prefix:new_end]
        self.versions[path] += 1
        self.client.notify("textDocument/didChange", {"textDocument": {
            "uri": path.as_uri(), "version": self.versions[path]}, "contentChanges": [{
                "range": change_range, "text": change_text}]})
        self.current[path] = text
        self.events.append({"method": "didChange", "file": str(path),
                            "version": self.versions[path], "incremental": True,
                            "range": change_range, "text": change_text})

    def close(self, path):
        self.client.notify("textDocument/didClose", {"textDocument": {"uri": path.as_uri()}})
        self.opened.remove(path)
        self.events.append({"method": "didClose", "file": str(path), "version": self.versions[path]})

    def hashes(self):
        return [{"file": str(path), "before": original["sha256"],
                 "after": hashlib.sha256(path.read_bytes()).hexdigest()}
                for path, original in self.original.items()]


def workspace_changes(edit, buffers):
    changes = {}
    if edit.get("documentChanges") is None:
        for uri, edits in (edit.get("changes") or {}).items():
            changes.setdefault(uri_path(uri), []).extend(edits)
        return changes
    # LSP documentChanges takes precedence when both representations are sent.
    for change in edit["documentChanges"]:
        if "textDocument" not in change:
            raise ValueError("Property rename unexpectedly requested file creation/move/deletion")
        document = change["textDocument"]
        path = uri_path(document["uri"])
        if document.get("version") is not None and (
                path not in buffers.opened or document["version"] != buffers.versions[path]):
            raise ValueError(f"WorkspaceEdit document version does not match current open buffer: {path}")
        changes.setdefault(path, []).extend(change["edits"])
    return changes


def summarize(metrics):
    grouped = {}
    for metric in metrics:
        grouped.setdefault(metric["operation"], []).append(metric["milliseconds"])
    return {name: {"samples": len(values), "min_ms": min(values),
            "median_ms": round(statistics.median(values), 3), "max_ms": max(values)}
            for name, values in grouped.items()}


def run_sample(args, index):
    client = LspClient(args.server, args.solution, args.output / f"sample-{index}.stderr.log", args.lazy)
    buffers = Buffers(client, args.repository, args.source_manifest)
    metrics, checks = [], []
    result = {"sample": index, "metrics": metrics, "checks": checks}
    a, b, membership, contract, dto = args.files

    def check(condition, description):
        if not condition:
            raise AssertionError(description)
        checks.append(description)

    def measure(label, method, params=None):
        elapsed, value = client.request(method, params, timeout=args.timeout)
        metric = {"operation": label, "method": method, "milliseconds": round(elapsed, 3),
                  "count": result_count(value)}
        if params and "textDocument" in params:
            metric["document_uri"] = params["textDocument"]["uri"]
        if params and "position" in params:
            metric["position"] = params["position"]
        metrics.append(metric)
        print(f"  {label}: {elapsed:.1f} ms ({result_count(value)})", flush=True)
        return value

    def doc(path):
        return {"textDocument": {"uri": path.as_uri()}}

    def at(path, index):
        return {**doc(path), "position": position(buffers.read(path), index)}

    def member_reference(path, name):
        text = buffers.read(path)
        return text.index("user." + name) + len("user.")

    def declaration(name):
        text = buffers.read(a)
        marker = "class UserInfo" if name == "UserInfo" else (
            "public string " + name if name == PROBE_NAME else "public bool " + name)
        return text.index(marker) + len(marker) - len(name)

    def definition(label, source, source_index, name):
        value = measure(label, "textDocument/definition", at(source, source_index))
        locations = value if isinstance(value, list) else [value] if value else []
        expected_range = span(buffers.read(a), declaration(name), len(name))
        valid = [location for location in locations if
                 uri_path(location.get("targetUri", location.get("uri", ""))) == a and
                 location.get("targetSelectionRange", location.get("range")) == expected_range]
        check(bool(valid), f"{label}: exact UserInfo.cs {name} declaration URI and identifier range")

    def completion(label, path, required, absent=(), receiver_name=OLD_NAME, resolve=False):
        text = buffers.read(path)
        start = text.index("this.") + 5 if path == a else member_reference(path, receiver_name)
        value = measure(label, "textDocument/completion", {
            **at(path, start), "context": {"triggerKind": 1}})
        items = value.get("items", []) if isinstance(value, dict) else value or []
        by_name = {item["label"]: item for item in items}
        for name in required:
            check(name in by_name and by_name[name].get("kind") == 10,
                  f"{label}: completion includes property {name}")
        for name in absent:
            check(name not in by_name, f"{label}: completion excludes stale property {name}")
        if resolve:
            # Member-list checks above use the caret immediately after the dot.
            # Commit checks use the end of the actual identifier: at its start,
            # Roslyn legitimately offers an insertion before the existing word.
            commit_list = measure(label + "_commit", "textDocument/completion", {
                **at(path, start + len(receiver_name)), "context": {"triggerKind": 1}})
            commit_items = {entry["label"]: entry for entry in commit_list["items"]}
            check(receiver_name in commit_items, f"{label}: typed property has a completion candidate")
            item = measure(label + "_resolve", "completionItem/resolve", commit_items[receiver_name])
            result.setdefault("completion_resolve_evidence", []).append({
                "operation": label, "item": item, "itemDefaults": commit_list.get("itemDefaults"),
                "expected_range": span(text, start, len(receiver_name))})
            edit = item.get("textEdit")
            if edit is None:
                edit = {"range": commit_list["itemDefaults"]["editRange"],
                        "newText": item.get("textEditText") or item["label"]}
            check(edit["range"] == span(text, start, len(receiver_name)),
                  f"{label}: resolved completion replaces exactly the receiver member token")
            check(edit["newText"] == receiver_name,
                  f"{label}: resolved completion inserts the exact property name")
            documentation = item.get("documentation") or {}
            documentation = documentation.get("value", "") if isinstance(documentation, dict) else documentation
            check("UserInfo" in documentation and receiver_name in documentation and "bool" in documentation,
                  f"{label}: resolve supplies documentation for the correct UserInfo bool property")
            check(not item.get("additionalTextEdits"),
                  f"{label}: ordinary member completion has no unexpected additional edits")
            check(apply_edits(text, [edit]) == text,
                  f"{label}: accepting existing property completion preserves buffer text")
        return value

    def symbols(label, name, absent=()):
        value = measure(label, "textDocument/documentSymbol", doc(a))
        def flatten(items):
            for item in items:
                yield item
                yield from flatten(item.get("children") or [])
        names = {item["name"] for item in flatten(value or [])}
        check(name in names, f"{label}: symbols include {name}")
        for item in absent:
            check(item not in names, f"{label}: symbols exclude {item}")

    try:
        for path in args.files:
            buffers.read(path)
        check(all(PROBE_NAME not in buffers.read(path) and NEW_NAME not in buffers.read(path)
                  for path in args.files), "Benchmark names are absent from all scenario inputs")
        measure("initialize", "initialize", {
            "processId": os.getpid(), "rootUri": args.repository.as_uri(),
            "capabilities": {"textDocument": {"diagnostic": {}, "completion": {
                "completionItem": {"snippetSupport": True},
                "completionList": {"itemDefaults": ["editRange", "data"]}}}},
            "initializationOptions": {"roslynSense": {"loadEntireSolution": not args.lazy}}})
        client.notify("initialized", {})
        buffers.open(a)
        completion("first_a_completion", a, [OLD_NAME, "UserID", "Username"])
        buffers.open(b)
        completion("first_b_completion", b, [OLD_NAME, "UserID", "Username"], resolve=True)
        definition("first_b_property_definition", b, member_reference(b, OLD_NAME), OLD_NAME)
        class_use = buffers.read(b).index("UserInfo user")
        definition("first_b_type_definition", b, class_use, "UserInfo")
        measure("workspace_warmup_barrier", "workspace/symbol", {"query": "UserInfo"})
        time.sleep(args.settle_seconds)
        result["memory_before_warm"] = client.memory()
        for _ in range(args.warm_repeats):
            # Focus changes leave both documents open, as they do in VS Code.
            completion("switch_back_a_completion", a, [OLD_NAME, "UserID", "Username"])
            completion("switch_to_b_completion", b, [OLD_NAME, "UserID", "Username"])
            definition("switch_b_to_a_definition", b, member_reference(b, OLD_NAME), OLD_NAME)
            completion("switch_return_a_completion", a, [OLD_NAME, "UserID", "Username"])

        original_a, original_b = buffers.read(a), buffers.read(b)
        body_marker = ".AddHours(-1 * settings.DataConsentDelay)"
        check(original_b.count(body_marker) == 1, "Unique ordinary body-edit marker")
        for body_index in range(args.body_repeats):
            body_text = original_b.replace(body_marker,
                f".AddHours(-{body_index + 2} * settings.DataConsentDelay)", 1)
            buffers.change(b, body_text)
            completion("body_edit_completion", b, [OLD_NAME, "UserID", "Username"])
        buffers.change(b, original_b)
        marker = "        public bool " + OLD_NAME
        inserted = "        public string " + PROBE_NAME + " { get; set; }\r\n\r\n"
        check(original_a.count(marker) == 1, "Unique class-edit insertion marker")
        b_version = buffers.versions[b]
        # Keep B as the last completion document before changing A. Some Roslyn
        # reuse paths only reveal stale dependency models in this request order.
        completion("before_class_edit_b_completion", b, [OLD_NAME, "UserID", "Username"])
        buffers.change(a, original_a.replace(marker, inserted + marker, 1))
        # No retries or settling: this first response must see the other file's edit.
        completion("class_edit_dependent_completion", b,
                   [OLD_NAME, PROBE_NAME, "UserID"], receiver_name=OLD_NAME)
        check(buffers.versions[b] == b_version,
              "Dependent completion observed A edit while B version stayed unchanged")
        symbols("class_edit_symbols", PROBE_NAME)
        definition("class_edit_shifted_definition", b, member_reference(b, OLD_NAME), OLD_NAME)
        consumer_marker = "user." + OLD_NAME + ")"
        check(original_b.count(consumer_marker) == 1, "Unique consumer edit insertion marker")
        buffers.change(b, original_b.replace(consumer_marker,
            "user." + OLD_NAME + " && user." + PROBE_NAME + " != null)", 1))
        definition("consumer_edit_new_property_definition", b, member_reference(b, PROBE_NAME), PROBE_NAME)
        completion("consumer_edit_completion", b, [OLD_NAME, PROBE_NAME], resolve=True)

        result["load_counters_before_rename"] = measure("load_counters_before_rename", "roslynSense/diagnosticsCounters")
        result["stderr_bytes_before_rename"] = (args.output / f"sample-{index}.stderr.log").stat().st_size
        result["open_files_before_rename"] = [str(path) for path in sorted(buffers.opened)]
        prepared = measure("prepare_property_rename", "textDocument/prepareRename",
                           at(b, member_reference(b, OLD_NAME)))
        check(prepared is not None and prepared.get("placeholder") == OLD_NAME and
              prepared.get("range") == span(buffers.read(b), member_reference(b, OLD_NAME), len(OLD_NAME)),
              "Prepare rename selects the exact property use")
        edit = measure("property_rename", "textDocument/rename", {
            **at(b, member_reference(b, OLD_NAME)), "newName": NEW_NAME})
        check(bool(edit), "Rename returns a WorkspaceEdit")
        (args.output / f"sample-{index}.rename.json").write_text(json.dumps(edit, indent=2), encoding="utf-8")
        changes = workspace_changes(edit, buffers)
        result["rename_files"] = [str(path) for path in changes]
        result["rename_edit_count"] = sum(len(edits) for edits in changes.values())
        for path in args.files:
            check(path in changes, f"Rename includes known declaration/reference file {path.relative_to(args.repository)}")
        before_rename = {path: buffers.read(path) for path in changes}
        renamed = {path: apply_edits(before_rename[path], edits) for path, edits in changes.items()}
        for path in (b, membership):
            check("user." + OLD_NAME not in renamed[path] and "user." + NEW_NAME in renamed[path],
                  f"Rename replaces every known UserInfo member access in {path.name}")
        check(before_rename[dto].count("user." + OLD_NAME) == 3,
              "DTO contains two UserInfo uses and one unrelated UserDetailDto use")
        expected_dto = before_rename[dto].replace("user." + OLD_NAME, "user." + NEW_NAME, 2)
        check(renamed[dto] == expected_dto,
              "Rename changes only DTO's two UserInfo references; unrelated property, targets, and UserDetailDto reference stay intact")
        check("bool " + NEW_NAME + " { get; set; }" in renamed[contract] and
              "bool " + OLD_NAME + " { get; set; }" not in renamed[contract],
              "Rename updates the IUserInfo property contract")
        check(PROBE_NAME in renamed[a] and PROBE_NAME in renamed[b],
              "Rename preserves prior unsaved class and consumer edits")
        for path, text in renamed.items():
            buffers.change(path, text)
        completion("renamed_dependent_completion", b, [NEW_NAME, PROBE_NAME], [OLD_NAME], NEW_NAME, resolve=True)
        definition("renamed_property_definition", b, member_reference(b, NEW_NAME), NEW_NAME)
        definition("renamed_dto_property_definition", dto, member_reference(dto, NEW_NAME), NEW_NAME)
        symbols("renamed_symbols", NEW_NAME, [OLD_NAME])
        for path, text in before_rename.items():
            buffers.change(path, text)
        completion("undo_rename_completion", b, [OLD_NAME, PROBE_NAME], [NEW_NAME], resolve=True)
        definition("undo_rename_definition", b, member_reference(b, OLD_NAME), OLD_NAME)
        buffers.change(a, original_a)
        buffers.change(b, original_b)
        completion("undo_class_edit_completion", b, [OLD_NAME, "UserID"], [PROBE_NAME, NEW_NAME], resolve=True)
        definition("undo_class_edit_definition", b, member_reference(b, OLD_NAME), OLD_NAME)
        symbols("undo_class_edit_symbols", OLD_NAME, [PROBE_NAME, NEW_NAME])

        buffers.close(b)
        completion("b_closed_a_completion", a, [OLD_NAME, "UserID"])
        buffers.open(b)
        completion("reopened_b_completion", b, [OLD_NAME, "UserID"], resolve=True)
        definition("reopened_b_definition", b, member_reference(b, OLD_NAME), OLD_NAME)
        check(all(buffers.current[path] == value["text"] for path, value in buffers.original.items()),
              "Undo restored every touched buffer to its original source text")
        result["memory_final"] = client.memory()
        result["load_counters"] = measure("load_counters", "roslynSense/diagnosticsCounters")
    except Exception as error:
        result["error"] = str(error)
    finally:
        result["buffer_events"] = buffers.events
        errors = client.close()
        if errors:
            result["cleanup_errors"] = errors
        result["shutdown"] = {"forced": client.cleanup_forced,
                              "graceful_error": client.graceful_shutdown_error}
        try:
            result["source_hashes"] = buffers.hashes()
            result["source_unchanged"] = all(item["before"] == item["after"] for item in result["source_hashes"])
        except Exception as error:
            result["source_unchanged"] = False
            result["source_verification_error"] = str(error)
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--server", type=Path, required=True)
    parser.add_argument("--repository", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--samples", type=int, default=3)
    parser.add_argument("--warm-repeats", type=int, default=7)
    parser.add_argument("--body-repeats", type=int, default=3)
    parser.add_argument("--timeout", type=float, default=180)
    parser.add_argument("--settle-seconds", type=float, default=30)
    parser.add_argument("--lazy", action="store_true")
    args = parser.parse_args()
    args.server = args.server.resolve(strict=True)
    args.repository = args.repository.resolve(strict=True)
    args.solution = (args.repository / "DNN_Platform.sln").resolve(strict=True)
    args.files = [(args.repository / path).resolve(strict=True) for path in (
        "DNN Platform/Library/Entities/Users/UserInfo.cs",
        "DNN Platform/Library/Services/Users/PurgeDeletedUsers.cs",
        "DNN Platform/Library/Security/Membership/AspNetMembershipProvider.cs",
        "DNN Platform/DotNetNuke.Abstractions/Users/IUserInfo.cs",
        "Dnn.AdminExperience/Dnn.PersonaBar.Extensions/Components/Users/Dto/UserBasicDto.cs")]
    if args.samples < 1 or args.warm_repeats < 1 or args.body_repeats < 1 or args.timeout <= 0 or args.settle_seconds < 0:
        parser.error("Samples/repeats/timeout must be positive; settling cannot be negative")
    args.output.mkdir(parents=True, exist_ok=True)
    # This read-only manifest is outside request timings and deliberately warms
    # source-file OS caches. It also covers rename targets discovered later.
    args.source_manifest = source_manifest(args.repository)
    (args.output / "source-manifest.json").write_text(json.dumps(
        {str(path): value for path, value in args.source_manifest.items()}, indent=2), encoding="utf-8")
    report = {"timestamp_utc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
              "server": str(args.server), "server_sha256": hashlib.sha256(args.server.read_bytes()).hexdigest(),
              "repository": str(args.repository), "files": [str(path) for path in args.files],
              "platform": platform.platform(), "cpu_count": os.cpu_count(), "python": platform.python_version(),
              "load_entire_solution": not args.lazy, "warm_repeats": args.warm_repeats,
              "body_repeats": args.body_repeats,
              "settle_seconds": args.settle_seconds, "timeout_seconds": args.timeout,
              "runtime_environment": {key: os.environ.get(key) for key in
                  ("DOTNET_gcServer", "DOTNET_PROCESSOR_COUNT", "DOTNET_GCHeapCount")},
              "mode": "standalone --lsp (shared host disabled)", "samples": []}
    for index in range(1, args.samples + 1):
        print(f"Sample {index}/{args.samples}", flush=True)
        try:
            sample = run_sample(args, index)
        except Exception as error:
            sample = {"sample": index, "metrics": [], "error": str(error)}
        if "error" not in sample and "cleanup_errors" not in sample and sample.get("source_unchanged"):
            sample["summary"] = summarize(sample["metrics"])
        report["samples"].append(sample)
        (args.output / "results.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
        if "error" in sample or "cleanup_errors" in sample or not sample.get("source_unchanged"):
            print(f"FAILED: {sample.get('error', sample.get('cleanup_errors', 'Source hash changed'))}", flush=True)
            break
    report["summary"] = summarize([
        metric for sample in report["samples"] if "summary" in sample for metric in sample["metrics"]])
    final_manifest = source_manifest(args.repository)
    report["repository_source_files_checked"] = len(args.source_manifest)
    report["repository_sources_unchanged"] = final_manifest == args.source_manifest
    (args.output / "results.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps(report["summary"], indent=2), flush=True)
    return int(not report["repository_sources_unchanged"] or any(
        "error" in sample or "cleanup_errors" in sample or not sample.get("source_unchanged")
        for sample in report["samples"]))


if __name__ == "__main__":
    raise SystemExit(main())
