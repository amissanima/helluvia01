"""Run an isolated tModLoader client and collect evidence from its test mod."""
import argparse
import ctypes
from ctypes import wintypes
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import time
import traceback
import uuid

import environment


HERE = Path(__file__).resolve().parent
PHASES = ("environment", "prepare", "build", "process", "window", "loaded", "world",
          "inventory", "attack", "mechanic", "capture", "fixtures")
EVENT_PHASES = {"loaded", "world", "inventory", "attack", "mechanic"}
STATUSES = {"passed", "failed", "skipped", "inconclusive"}


def timestamp():
    return datetime.now(timezone.utc).isoformat()


def write_json(path, value):
    temp = path.with_suffix(".tmp")
    temp.write_text(json.dumps(value, indent=2, ensure_ascii=False), encoding="utf-8")
    temp.replace(path)


def phase(report, key, status, detail=None):
    report["stages"][key] = {"status": status, "detail": detail}


class GameWindow:
    """Only recognizes a visible SDL game window belonging to this launch."""

    def __init__(self, pid):
        self.pid = pid
        self.api = ctypes.WinDLL("user32", use_last_error=True)
        self.callback = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
        signatures = {
            "EnumWindows": ([self.callback, wintypes.LPARAM], wintypes.BOOL),
            "GetWindowThreadProcessId": ([wintypes.HWND, ctypes.POINTER(wintypes.DWORD)], wintypes.DWORD),
            "GetClassNameW": ([wintypes.HWND, wintypes.LPWSTR, ctypes.c_int], ctypes.c_int),
            "GetWindowTextW": ([wintypes.HWND, wintypes.LPWSTR, ctypes.c_int], ctypes.c_int),
            "IsWindowVisible": ([wintypes.HWND], wintypes.BOOL),
            "GetForegroundWindow": ([], wintypes.HWND),
            "SetForegroundWindow": ([wintypes.HWND], wintypes.BOOL),
            "PostMessageW": ([wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM], wintypes.BOOL)
        }
        for name, (args, result) in signatures.items():
            function = getattr(self.api, name)
            function.argtypes, function.restype = args, result

    def belongs(self, hwnd):
        owner = wintypes.DWORD()
        self.api.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
        return owner.value == self.pid and self.api.IsWindowVisible(hwnd)

    def find(self):
        matches = []

        @self.callback
        def inspect(hwnd, _):
            if self.belongs(hwnd):
                window_class = ctypes.create_unicode_buffer(128)
                self.api.GetClassNameW(hwnd, window_class, len(window_class))
                if window_class.value.startswith("SDL"):
                    title = ctypes.create_unicode_buffer(256)
                    self.api.GetWindowTextW(hwnd, title, len(title))
                    matches.append({"hwnd": hwnd, "title": title.value,
                                    "class": window_class.value, "pid": self.pid})
            return True

        self.api.EnumWindows(inspect, 0)
        return matches[0] if matches else None

    def active(self, hwnd):
        return self.belongs(hwnd) and self.api.GetForegroundWindow() == hwnd


def check_command(args, child_env, run):
    if not child_env.get("HELLUVIA_TEST_TOKEN") or Path(child_env["HELLUVIA_TEST_RUN"]).resolve() != run:
        raise ValueError("Game command has an invalid run directory or token")
    save = Path(child_env["HELLUVIA_TEST_SAVE"]).resolve()
    arguments = [str(arg) for arg in args]
    index = arguments.index("-tmlsavedirectory")
    if save == run or not save.is_relative_to(run) or Path(arguments[index + 1]).resolve() != save:
        raise ValueError("Game command must use the disposable save directory in this run")


def read_events(path, offset):
    if not path.exists():
        return [], offset
    with path.open("rb") as stream:
        stream.seek(offset)
        events = []
        for line in stream:
            if not line.endswith(b"\n"):
                break
            events.append(json.loads(line.decode("utf-8-sig")))
            offset = stream.tell()
    return events, offset


def validate_png(path):
    from PIL import Image
    with Image.open(path) as img:
        if img.format != "PNG" or min(img.size) < 64:
            raise ValueError(f"Invalid game screenshot: {path}")
        img.load()
        extrema = img.convert("RGB").getextrema()
        if all(low == high for low, high in extrema):
            raise ValueError(f"Game screenshot is a solid color: {path}")
        return list(img.size)


def capture(run, event, window, elapsed):
    name = event["data"]["name"]
    if not isinstance(name, str) or not re.fullmatch(r"[A-Za-z0-9_-]{1,64}", name):
        raise ValueError("Invalid capture event name")
    path = run / f"{name}.png"
    source = "game_backbuffer"
    if not path.exists():
        if elapsed < 3:
            return None
        current = window.find()
        if not current or not window.active(current["hwnd"]):
            return None
        from PIL import ImageGrab
        # HWND capture excludes other applications. A late frame is labelled with its origin.
        shot = ImageGrab.grab(window=current["hwnd"])
        if not window.active(current["hwnd"]):
            return None
        path = run / f"{name}-window.png"
        shot.save(path, format="PNG")
        source = "foreground_window_after_event"
    size = validate_png(path)
    (run / f"capture-{name}.ack").touch(exist_ok=False)
    return {"event": name, "tick": event["tick"], "path": str(path), "size": size,
            "source": source, "capturedAt": timestamp(), "visualStatus": "inconclusive"}


def end_client(process, window, run, token):
    if process.poll() is not None:
        return "client exited"
    if (run / "result.json").is_file():
        request = run / "exit.request.tmp"
        request.write_text(token, encoding="utf-8")
        request.replace(run / "exit.request")
        try:
            process.wait(timeout=12)
            return "client accepted exit request"
        except subprocess.TimeoutExpired:
            pass
    current = window.find() if window else None
    if current and window.belongs(current["hwnd"]):
        window.api.PostMessageW(current["hwnd"], 0x0010, 0, 0)  # WM_CLOSE
        try:
            process.wait(timeout=10)
            return "client closed its window"
        except subprocess.TimeoutExpired:
            pass
    process.terminate()  # Operates on the owned process handle, including during a PID reuse.
    process.wait(timeout=10)
    return "terminated owned client process"


def monitor(run, process, window, token, report, deadline):
    offset = 0
    pending = {}
    result = None
    current = None
    focused = None
    while True:
        now = time.monotonic()
        if current is None or not window.belongs(current["hwnd"]):
            current = window.find()
            if current:
                current["foregroundAtDetection"] = window.active(current["hwnd"])
                if report["stages"]["window"]["status"] != "passed" and not current["foregroundAtDetection"]:
                    # An inactive single-player client can pause before input hooks run.
                    current["focusRequested"] = bool(window.api.SetForegroundWindow(current["hwnd"]))
                phase(report, "window", "passed", current)
        active = bool(current and window.active(current["hwnd"]))
        if focused != active:
            report["windowFocus"].append({"at": timestamp(), "foreground": active})
            focused = active
        events, offset = read_events(run / "events.jsonl", offset)
        for event in events:
            if event.get("pid") != process.pid or event.get("token") != token:
                raise ValueError("Event source does not match the launched client")
            report["events"].append(event)
            kind = event["kind"]
            if kind in EVENT_PHASES:
                status = "failed" if event["data"].get("passed") is False else "passed"
                if report["stages"][kind]["status"] != "failed":
                    phase(report, kind, status, event["data"])
            elif kind == "capture":
                name = event["data"]["name"]
                if name in pending or any(shot["event"] == name for shot in report["screenshots"]):
                    raise ValueError(f"Duplicate capture name: {name}")
                pending[name] = (event, now)
        for name, (event, start) in list(pending.items()):
            evidence = capture(run, event, window, now - start)
            if evidence:
                report["screenshots"].append(evidence)
                phase(report, "capture", "passed", [s["event"] for s in report["screenshots"]])
                del pending[name]
        if result is None and (run / "result.json").is_file():
            result = json.loads((run / "result.json").read_text(encoding="utf-8-sig"))
            if result["token"] != token or result["pid"] != process.pid or result["status"] not in STATUSES:
                raise ValueError("Invalid result or result does not match this process")
            report["gameResult"] = result
            # The game can append its last checks between our JSONL read and
            # publishing the result. Drain the stream once after publication.
            continue
        if result and (not pending or result["status"] != "passed"):
            return result
        if process.poll() is not None:
            if result is None and (run / "result.json").exists():
                continue
            raise RuntimeError(f"Client exited before reporting all results (exit {process.returncode})")
        if now >= deadline:
            raise TimeoutError("Timed out waiting for the client, test result or capture")
        time.sleep(0.1)


def build_diagnostics(path):
    """Use each mod's compiler summary and count individual diagnostics when compilation aborts early."""
    summary = re.compile(r"Compilation finished with (\d+) errors? and (\d+) warnings?", re.I)
    diagnostic = re.compile(r"\b(error|warning|ошибка|предупреждение)\s+([A-Z]+\d+)\s*:", re.I)
    sections = re.split(r"^Building:\s*[^\r\n]*$", path.read_text(encoding="utf-8", errors="replace"),
                        flags=re.MULTILINE | re.IGNORECASE)
    errors = warnings = 0
    recognized = False
    messages = []
    for section in sections:
        counts = summary.search(section)
        found = {}
        for line in section.splitlines():
            match = diagnostic.search(line)
            if match:
                severity = "error" if match[1].lower() in {"error", "ошибка"} else "warning"
                found[line.strip()] = {"severity": severity, "code": match[2].upper(), "text": line.strip()}
        messages.extend(found.values())
        if counts:
            errors += int(counts[1])
            warnings += int(counts[2])
        else:
            errors += sum(m["severity"] == "error" for m in found.values())
            warnings += sum(m["severity"] == "warning" for m in found.values())
        recognized |= bool(counts or found)
    return {"errors": errors if recognized else None, "warnings": warnings if recognized else None,
            "messages": messages, "log": str(path)}


def execute(options, scenario_path, run, report):
    stage = "environment"
    process = window = None
    env = None
    try:
        scenario = json.loads(scenario_path.read_text(encoding="utf-8-sig"))
        report["scenario"] = scenario
        env = environment.discover(options)
        report["environment"] = env
        phase(report, stage, "passed")
        stage = "prepare"
        environment.prepare(run, env, scenario_path)
        scenario = json.loads((run / "scenario.json").read_text(encoding="utf-8-sig"))
        if report["scenario"] != scenario:
            raise RuntimeError("Scenario changed while preparing the test")
        phase(report, stage, "passed")
        stage = "build"
        environment.build(run, env)
        phase(report, stage, "passed")
        stage = "process"
        args, cwd, child_env = environment.client_command(run, env)
        check_command(args, child_env, run)
        with (run / "client.log").open("wb") as log:
            process = subprocess.Popen(args, cwd=cwd, env=child_env, stdout=log,
                                       stderr=subprocess.STDOUT, stdin=subprocess.DEVNULL,
                                       creationflags=subprocess.CREATE_NO_WINDOW)
            phase(report, stage, "passed", {"pid": process.pid})
            stage = None
            window = GameWindow(process.pid)
            result = monitor(run, process, window, child_env["HELLUVIA_TEST_TOKEN"], report,
                             time.monotonic() + options.timeout)
        report["status"] = result["status"]
        if result["status"] == "passed":
            required = ["window", "loaded", "world", "mechanic"]
            if "steps" in scenario:
                required.append("inventory")
                steps = scenario["steps"]
                expects_attack = any(step.get("op") in {"assert", "await"}
                                     and step.get("metric") == "attacks"
                                     and (step.get("eq", 0) >= 1 or step.get("min", 0) >= 1)
                                     for step in steps)
                if scenario.get("requireAttack") or expects_attack:
                    required.append("attack")
                expected_frames = {step["name"] for step in steps if step["op"] == "capture"}
                if expected_frames:
                    required.append("capture")
                actual_frames = {shot["event"] for shot in report["screenshots"]}
                if expected_frames != actual_frames:
                    raise ValueError(f"Expected captures {sorted(expected_frames)}, got {sorted(actual_frames)}")
            elif scenario.get("weapon"):
                required.extend(["inventory", "attack", "capture"])
            failed = [key for key in required if report["stages"][key]["status"] == "failed"]
            if failed:
                report["status"] = "failed"
                report["errors"].append("Failed checks: " + ", ".join(failed))
            missing = [key for key in required if report["stages"][key]["status"] == "skipped"]
            if missing:
                raise ValueError("Missing required evidence: " + ", ".join(missing))
        elif result["status"] == "failed":
            report["errors"].append(result["message"])
    except (Exception, KeyboardInterrupt) as exc:
        if isinstance(exc, KeyboardInterrupt):
            report["interrupted"] = True
        if stage:
            phase(report, stage, "failed", str(exc))
        report["errors"].append(f"{type(exc).__name__}: {exc}")
        report["status"] = "inconclusive"
        (run / "runner-error.log").write_text(traceback.format_exc(), encoding="utf-8")
    finally:
        build_log = run / "build.log"
        if build_log.is_file():
            try:
                diagnostics = build_diagnostics(build_log)
                report["buildDiagnostics"] = diagnostics
                if report["stages"]["build"]["status"] == "failed":
                    first = next((m["text"] for m in diagnostics["messages"] if m["severity"] == "error"), None)
                    if first:
                        report["errors"].append(first)
                        phase(report, "build", "failed", first)
            except OSError as exc:
                report["errors"].append(f"Could not read compiler diagnostics: {exc}")
        if process is not None:
            try:
                report["shutdown"] = end_client(process, window, run, child_env["HELLUVIA_TEST_TOKEN"])
                report["exitCode"] = process.returncode
            except (OSError, subprocess.TimeoutExpired) as exc:
                report["errors"].append(f"Client shutdown: {exc}")
                report["status"] = "inconclusive"
            # Several clients can share an installation. Only copy a loader log
            # if its startup header identifies this unique temporary save path.
            for path in (Path(env["tml"]) / "tModLoader-Logs").glob("client*.log"):
                try:
                    with path.open("rb") as source:
                        header = source.read(32768).decode("utf-8", errors="replace")
                    if child_env["HELLUVIA_TEST_SAVE"] in header:
                        shutil.copyfile(path, run / "loader-client.log")
                        break
                except OSError:
                    continue
        manifest = run / "backup/sha256.json"
        if env is not None and manifest.is_file():
            try:
                environment.verify_originals(run, env)
                phase(report, "fixtures", "passed", {"sha256Manifest": str(manifest)})
            except (ValueError, RuntimeError, OSError) as exc:
                phase(report, "fixtures", "failed", str(exc))
                report["errors"].append(f"Fixture verification: {exc}")
                report["status"] = "inconclusive"


def run_one(options, scenario_path):
    if options.output_root:
        run = options.output_root / f"helluvia-test-{uuid.uuid4().hex}"
        run.mkdir()
    else:
        run = Path(tempfile.mkdtemp(prefix="helluvia-test-")).resolve()
    report = {"startedAt": timestamp(), "run": str(run), "scenarioPath": str(scenario_path),
              "status": "inconclusive", "stages": {key: {"status": "skipped"} for key in PHASES},
              "events": [], "screenshots": [], "errors": [], "windowFocus": []}
    print(f"Run: {run} ({scenario_path.name})", flush=True)
    try:
        execute(options, scenario_path, run, report)
    finally:
        report["finishedAt"] = timestamp()
        report["logs"] = [str(path) for path in sorted(run.glob("*.log"))]
        if (run / "events.jsonl").exists():
            report["logs"].append(str(run / "events.jsonl"))
        write_json(run / "report.json", report)
    print(f"{report['status']}: {run / 'report.json'}", flush=True)
    diagnostics = report.get("buildDiagnostics")
    if diagnostics and diagnostics["errors"] is not None:
        print(f"Build: {diagnostics['errors']} errors, {diagnostics['warnings']} warnings", flush=True)
    for message in report["errors"]:
        print(message, file=sys.stderr)
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--scenario", type=Path, help="Scenario JSON (default smg.json)")
    mode.add_argument("--all", action="store_true", help="Run each bundled scenario in its own profile")
    parser.add_argument("--tml", type=Path, help="Installed tModLoader directory")
    parser.add_argument("--saves", type=Path, help="Directory containing the original test fixtures")
    parser.add_argument("--timeout", type=int, default=240,
                        help="Seconds per client after launch, excluding build time (default 240)")
    parser.add_argument("--output-root", type=Path,
                        help="Parent directory for new run folders, outside ModSources")
    options = parser.parse_args()
    if options.timeout <= 0:
        parser.error("--timeout must be positive")
    if options.output_root:
        options.output_root = options.output_root.resolve()
        if options.output_root.is_relative_to(HERE.parents[1]):
            parser.error("--output-root must be outside ModSources")
        options.output_root.mkdir(exist_ok=True, parents=True)

    directory = HERE / "scenarios"
    if options.all:
        scenarios = sorted(directory.glob("*.json"))
        if not scenarios:
            parser.error("No bundled scenarios found")
    else:
        scenarios = [(options.scenario or directory / "smg.json").resolve()]
    passed = 0
    for path in scenarios:
        report = run_one(options, path)
        passed += report["status"] == "passed"
        if report.get("interrupted"):
            return 130
    if options.all:
        print(f"Passed {passed} of {len(scenarios)} scenarios", flush=True)
    return 0 if passed == len(scenarios) else 1


if __name__ == "__main__":
    sys.exit(main())
