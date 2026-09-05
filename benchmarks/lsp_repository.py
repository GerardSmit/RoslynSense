#!/usr/bin/env python3
"""Measure the real standalone LSP server against an existing, restored repository.

Python 3.10+, standard library only. No fixture generation, restore or source-file
edits by this driver; the server may perform its normal restore/design-time build.
Each sample starts a fresh server; OS/package caches are deliberately left intact.
"""

import argparse
import ctypes
import hashlib
import json
import os
from pathlib import Path
import platform
import queue
import signal
import statistics
import subprocess
import threading
import time


class LspClient:
    def __init__(self, server, solution, log_path, lazy):
        env = os.environ.copy()
        env["ROSLYNMCP_SHARED_HOST"] = "0"
        env["ROSLYNSENSE_SERVER_REDIRECTED"] = "1"
        env["ROSLYNMCP_LOAD_ENTIRE_SOLUTION"] = "0" if lazy else "1"
        # A tracing parent must not leave this child suspended at CLR startup.
        env.pop("DOTNET_DiagnosticPorts", None)
        self.log = log_path.open("wb")
        self.started = time.perf_counter()
        self.process = subprocess.Popen(
            ["dotnet", str(server), "--lsp", "--solution", str(solution)],
            cwd=solution.parent, env=env, stdin=subprocess.PIPE,
            stdout=subprocess.PIPE, stderr=self.log,
            creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0,
            start_new_session=os.name != "nt",
        )
        self.responses = queue.Queue()
        self.write_lock = threading.Lock()
        self.next_id = 0
        self.reader = threading.Thread(target=self._read, daemon=True)
        self.reader.start()

    def send(self, message):
        body = json.dumps(message, separators=(",", ":")).encode("utf-8")
        with self.write_lock:
            self.process.stdin.write(f"Content-Length: {len(body)}\r\n\r\n".encode("ascii") + body)
            self.process.stdin.flush()

    def _read(self):
        try:
            while True:
                headers = {}
                while True:
                    line = self.process.stdout.readline()
                    if not line:
                        raise EOFError("LSP server closed stdout")
                    if line == b"\r\n":
                        break
                    key, value = line.decode("ascii").split(":", 1)
                    headers[key.strip().lower()] = value.strip()
                remaining = int(headers["content-length"])
                chunks = []
                while remaining:
                    chunk = self.process.stdout.read(remaining)
                    if not chunk:
                        raise EOFError("Truncated LSP message")
                    chunks.append(chunk)
                    remaining -= len(chunk)
                message = json.loads(b"".join(chunks))
                if "method" in message:
                    if "id" in message:
                        # Capabilities do not advertise dynamic registration or
                        # refresh support; acknowledge incidental server requests.
                        self.send({"jsonrpc": "2.0", "id": message["id"], "result": None})
                else:
                    self.responses.put(message)
        except Exception as error:
            self.responses.put(error)

    def notify(self, method, params=None):
        message = {"jsonrpc": "2.0", "method": method}
        if params is not None:
            message["params"] = params
        self.send(message)

    def request(self, method, params=None, timeout=180):
        self.next_id += 1
        message = {"jsonrpc": "2.0", "id": self.next_id, "method": method}
        if params is not None:
            message["params"] = params
        start = time.perf_counter()
        self.send(message)
        try:
            response = self.responses.get(timeout=timeout)
        except queue.Empty as error:
            raise TimeoutError(f"{method} exceeded {timeout}s") from error
        elapsed = (time.perf_counter() - start) * 1000
        if isinstance(response, Exception):
            raise response
        if response.get("id") != self.next_id:
            raise RuntimeError(f"Unexpected response id: {response.get('id')}")
        if "error" in response:
            raise RuntimeError(f"{method}: {response['error']}")
        return elapsed, response.get("result")

    def memory(self):
        if os.name != "nt":
            return {"note": "Memory measurement currently requires Windows"}
        from ctypes import wintypes

        class Counters(ctypes.Structure):
            _fields_ = [("cb", wintypes.DWORD), ("PageFaultCount", wintypes.DWORD)] + [
                (name, ctypes.c_size_t) for name in (
                    "PeakWorkingSetSize", "WorkingSetSize", "QuotaPeakPagedPoolUsage",
                    "QuotaPagedPoolUsage", "QuotaPeakNonPagedPoolUsage", "QuotaNonPagedPoolUsage",
                    "PagefileUsage", "PeakPagefileUsage", "PrivateUsage")]

        kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        psapi = ctypes.WinDLL("psapi", use_last_error=True)
        kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
        kernel.OpenProcess.restype = wintypes.HANDLE
        kernel.CloseHandle.argtypes = [wintypes.HANDLE]
        kernel.GetProcessTimes.argtypes = [wintypes.HANDLE] + [ctypes.POINTER(wintypes.FILETIME)] * 4
        psapi.GetProcessMemoryInfo.argtypes = [wintypes.HANDLE, ctypes.POINTER(Counters), wintypes.DWORD]
        handle = kernel.OpenProcess(0x0410, False, self.process.pid)
        if not handle:
            raise ctypes.WinError(ctypes.get_last_error())
        try:
            counters = Counters()
            counters.cb = ctypes.sizeof(counters)
            if not psapi.GetProcessMemoryInfo(handle, ctypes.byref(counters), counters.cb):
                raise ctypes.WinError(ctypes.get_last_error())
            created, exited, kernel_time, user_time = (wintypes.FILETIME() for _ in range(4))
            if not kernel.GetProcessTimes(handle, ctypes.byref(created), ctypes.byref(exited),
                                          ctypes.byref(kernel_time), ctypes.byref(user_time)):
                raise ctypes.WinError(ctypes.get_last_error())
            def milliseconds(value):
                return ((value.dwHighDateTime << 32) | value.dwLowDateTime) / 10000
            return {"working_set_bytes": counters.WorkingSetSize,
                    "peak_working_set_bytes": counters.PeakWorkingSetSize,
                    "private_bytes": counters.PrivateUsage,
                    "cpu_user_ms": milliseconds(user_time),
                    "cpu_kernel_ms": milliseconds(kernel_time),
                    "scope": "LSP server process only; excludes MSBuild children"}
        finally:
            kernel.CloseHandle(handle)

    def close(self):
        errors = []
        self.cleanup_forced = False
        self.graceful_shutdown_error = None
        try:
            if self.process.poll() is None:
                try:
                    self.request("shutdown", timeout=10)
                    self.notify("exit")
                    self.process.stdin.close()
                    self.process.wait(timeout=15)
                except Exception as error:
                    self.graceful_shutdown_error = str(error)
                    if self.process.poll() is None:
                        self.cleanup_forced = True
                        if os.name == "nt":
                            subprocess.run(["taskkill", "/PID", str(self.process.pid), "/T", "/F"],
                                           stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                                           creationflags=subprocess.CREATE_NO_WINDOW, timeout=15)
                        else:
                            os.killpg(self.process.pid, signal.SIGKILL)
                        self.process.wait(timeout=15)
        except Exception as error:
            errors.append(f"Process cleanup: {error}")
        finally:
            try:
                self.log.close()
            except Exception as error:
                errors.append(f"Log close: {error}")
            if self.process.poll() is not None:
                self.reader.join(timeout=2)
                streams = [("stdin", self.process.stdin)]
                if self.reader.is_alive():
                    # BufferedReader.close can wait for the reader's lock when
                    # a child still owns the pipe. Preserve the completed data
                    # and report the leak instead of hanging this driver.
                    errors.append("LSP reader remained active after process exit")
                else:
                    streams.append(("stdout", self.process.stdout))
                for label, stream in streams:
                    try:
                        stream.close()
                    except Exception as error:
                        errors.append(f"{label} close: {error}")
        return errors


def position(text, offset):
    before = text[:offset]
    return {"line": before.count("\n"),
            "character": len(before.rsplit("\n", 1)[-1].encode("utf-16-le")) // 2}


def result_count(result):
    if isinstance(result, list):
        return len(result)
    if isinstance(result, dict):
        for key in ("items", "data", "path", "edits"):
            if isinstance(result.get(key), list):
                return len(result[key])
    return int(result is not None)


def run_sample(args, index):
    client = LspClient(args.server, args.solution, args.output / f"sample-{index}.stderr.log", args.lazy)
    metrics = []
    sample_result = {"sample": index, "metrics": metrics}

    def measure(label, method, params=None, require_nonempty=False):
        elapsed, result = client.request(method, params, timeout=args.timeout)
        count = result_count(result)
        if require_nonempty and count == 0:
            raise RuntimeError(f"{label} returned no usable result")
        metrics.append({"operation": label, "milliseconds": round(elapsed, 3), "count": count})
        print(f"  {label}: {elapsed:.1f} ms ({count})", flush=True)
        return result

    try:
        measure("initialize", "initialize", {
            "processId": os.getpid(), "rootUri": args.solution.parent.as_uri(),
            "capabilities": {"textDocument": {"diagnostic": {},
                "completion": {"completionItem": {"snippetSupport": True},
                    "completionList": {"itemDefaults": ["editRange", "data"]}}}},
            "initializationOptions": {"roslynSense": {"loadEntireSolution": not args.lazy}},
        }, True)
        metrics.append({"operation": "process_start_to_initialized", "milliseconds":
                        round((time.perf_counter() - client.started) * 1000, 3), "count": 1})
        client.notify("initialized", {})
        measure("initial_tree_roots", "roslynSense/solutionTree", {}, True)
        measure("initial_tree_solution", "roslynSense/solutionTree", {"nodeId": f"solution:{args.solution}"}, True)
        measure("initial_tree_project", "roslynSense/solutionTree", {"nodeId": f"project:{args.project}"}, True)
        # Preserve CRLF: text-mode newline conversion would make didOpen look
        # like a whole-file edit even though this scenario starts from disk.
        text = args.file.read_bytes().decode("utf-8-sig")
        uri = args.file.as_uri()
        doc = {"textDocument": {"uri": uri}}
        symbol_offset = text.index(args.symbol_marker) + len(args.symbol_marker) - len(args.symbol)
        symbol = {**doc, "position": position(text, symbol_offset)}
        completion_offset = text.index(args.completion_marker) + len(args.completion_marker)
        completion = {**doc, "position": position(text, completion_offset), "context": {"triggerKind": 1}}
        start = time.perf_counter()
        client.notify("textDocument/didOpen", {"textDocument": {
            "uri": uri, "languageId": "csharp", "version": 1, "text": text}})
        measure("first_document_symbols", "textDocument/documentSymbol", doc, True)
        metrics.append({"operation": "did_open_to_symbols", "milliseconds":
                        round((time.perf_counter() - start) * 1000, 3), "count": 1})
        measure("first_hover", "textDocument/hover", symbol, True)
        measure("first_definition", "textDocument/definition", symbol, True)
        measure("first_completion", "textDocument/completion", completion, True)
        tokens = measure("first_semantic_tokens", "textDocument/semanticTokens/full", doc, True)
        diagnostic = measure("first_diagnostics", "textDocument/diagnostic", doc)
        counters = measure("load_counter_after_first_features", "roslynSense/diagnosticsCounters")
        memory_first = client.memory()
        # workspace/symbol waits for the configured solution warm-up. This keeps
        # the warm repeats from racing unfinished full-solution startup, and
        # records that remaining startup cost instead of hiding it in a sleep.
        measure("first_workspace_symbols", "workspace/symbol", {"query": args.symbol}, True)
        metrics.append({"operation": "process_start_to_workspace_symbols", "milliseconds":
                        round((time.perf_counter() - client.started) * 1000, 3), "count": 1})
        # A small explicit settling interval separates active startup from warm
        # request latency. Logs/counters still disclose unfinished eager loading.
        time.sleep(args.settle_seconds)
        for _ in range(args.warm_repeats):
            measure("warm_target_for_file", "roslynSense/targetForFile", {"filePath": str(args.file)}, True)
            measure("warm_tree_reveal", "roslynSense/solutionTreeReveal", {"uri": uri}, True)
            measure("warm_document_symbols", "textDocument/documentSymbol", doc, True)
            measure("warm_hover", "textDocument/hover", symbol, True)
            measure("warm_completion", "textDocument/completion", completion, True)
            tokens = measure("warm_semantic_tokens", "textDocument/semanticTokens/full", doc, True)
            delta = measure("warm_semantic_tokens_delta", "textDocument/semanticTokens/full/delta",
                            {**doc, "previousResultId": tokens["resultId"]})
            if delta.get("edits") != []:
                raise RuntimeError("Unchanged semantic-token delta did not return empty edits")
            tokens["resultId"] = delta["resultId"]
            diagnostic = measure("warm_diagnostics", "textDocument/diagnostic", {
                **doc, "previousResultId": diagnostic.get("resultId") if diagnostic else None})
        # An actual buffer change, never a disk write. Append a comment so all
        # measured symbol/completion positions continue to address the same code.
        client.notify("textDocument/didChange", {"textDocument": {"uri": uri, "version": 2},
            "contentChanges": [{"text": text + "\n// RoslynSense benchmark unsaved edit\n"}]})
        measure("after_edit_document_symbols", "textDocument/documentSymbol", doc, True)
        measure("after_edit_completion", "textDocument/completion", completion, True)
        delta = measure("after_edit_semantic_tokens_delta", "textDocument/semanticTokens/full/delta",
                        {**doc, "previousResultId": tokens["resultId"]})
        reconstructed = list(tokens["data"])
        if "data" in delta:
            reconstructed = delta["data"]
        else:
            for edit in sorted(delta["edits"], key=lambda item: item["start"], reverse=True):
                reconstructed[edit["start"]:edit["start"] + edit["deleteCount"]] = edit.get("data") or []
        # This full request validates the preceding delta; its timing is not the
        # first semantic request after the edit and is labelled accordingly.
        full = measure("after_edit_semantic_tokens_verification_full", "textDocument/semanticTokens/full", doc, True)
        if reconstructed != full["data"] or reconstructed == tokens["data"]:
            raise RuntimeError("Edited semantic-token delta did not reconstruct the changed full token array")
        final_counters = measure("load_counter_final", "roslynSense/diagnosticsCounters")
        memory_final = client.memory()
        sample_result.update({"load_counter_after_first_features": counters,
                "load_counter_final": final_counters, "memory_after_first_features": memory_first,
                "memory_final": memory_final})
    except Exception as error:
        sample_result["error"] = str(error)
    finally:
        if errors := client.close():
            sample_result["cleanup_errors"] = errors
        sample_result["shutdown"] = {"forced": client.cleanup_forced,
                                     "graceful_error": client.graceful_shutdown_error}
    return sample_result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--server", type=Path, required=True, help="Built RoslynMCP.dll")
    parser.add_argument("--solution", type=Path, required=True)
    parser.add_argument("--project", type=Path, required=True)
    parser.add_argument("--file", type=Path, required=True)
    parser.add_argument("--symbol", default="UserInfo")
    parser.add_argument("--symbol-marker", default="class UserInfo")
    parser.add_argument("--completion-marker", default="this.")
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--samples", type=int, default=3)
    parser.add_argument("--warm-repeats", type=int, default=7)
    parser.add_argument("--timeout", type=float, default=180)
    parser.add_argument("--settle-seconds", type=float, default=5)
    parser.add_argument("--lazy", action="store_true", help="Disable default background full-solution loading")
    args = parser.parse_args()
    for name in ("server", "solution", "project", "file"):
        value = getattr(args, name).resolve(strict=True)
        setattr(args, name, value)
    if args.samples < 1 or args.warm_repeats < 1 or args.timeout <= 0 or args.settle_seconds < 0:
        parser.error("Samples/repeats/timeout must be positive and settling time non-negative")
    args.output.mkdir(parents=True, exist_ok=True)
    source_hash = hashlib.sha256(args.file.read_bytes()).hexdigest()
    report = {"timestamp_utc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
              "platform": platform.platform(), "cpu_count": os.cpu_count(),
              "python": platform.python_version(), "server": str(args.server),
              "server_sha256": hashlib.sha256(args.server.read_bytes()).hexdigest(),
              "solution": str(args.solution), "project": str(args.project), "file": str(args.file),
              "symbol": args.symbol, "symbol_marker": args.symbol_marker,
              "completion_marker": args.completion_marker, "timeout_seconds": args.timeout,
              "file_sha256": source_hash, "mode": "standalone --lsp (shared host disabled)",
              "runtime_environment": {key: os.environ.get(key) for key in
                  ("DOTNET_gcServer", "DOTNET_PROCESSOR_COUNT", "DOTNET_GCHeapCount")},
              "load_entire_solution": not args.lazy, "warm_repeats": args.warm_repeats,
              "settle_seconds": args.settle_seconds, "samples": []}
    for index in range(1, args.samples + 1):
        print(f"Sample {index}/{args.samples}", flush=True)
        try:
            sample = run_sample(args, index)
        except Exception as error:
            sample = {"sample": index, "metrics": [], "error": str(error)}
        report["samples"].append(sample)
        (args.output / "results.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
        if "error" in sample or "cleanup_errors" in sample:
            print(f"FAILED: {sample.get('error', sample.get('cleanup_errors'))}", flush=True)
            break
    report["source_unchanged"] = source_hash == hashlib.sha256(args.file.read_bytes()).hexdigest()
    grouped = {}
    for sample in report["samples"]:
        if "error" in sample or "cleanup_errors" in sample:
            continue
        for metric in sample["metrics"]:
            grouped.setdefault(metric["operation"], []).append(metric["milliseconds"])
    report["summary"] = {name: {"samples": len(values), "min_ms": min(values),
        "median_ms": round(statistics.median(values), 3), "max_ms": max(values)}
        for name, values in grouped.items()}
    (args.output / "results.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps(report["summary"], indent=2), flush=True)
    return int(not report["source_unchanged"] or any(
        "error" in sample or "cleanup_errors" in sample for sample in report["samples"]))


if __name__ == "__main__":
    raise SystemExit(main())
