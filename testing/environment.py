"""Discover the installed loader and build inside a private, disposable profile."""
import hashlib
import json
import os
from pathlib import Path
import re
import secrets
import shutil
import subprocess
import sys
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
MODS = ("helluvia01", "HelluviaTestHarness")


def discover(options):
    if os.name != "nt":
        raise RuntimeError("This client runner requires Windows.")
    import PIL
    try:
        import pyautogui
        gui_version = pyautogui.__version__
    except ImportError:
        gui_version = None  # No global keyboard/mouse input is required.
    if options.tml:
        tml = Path(options.tml).resolve()
    else:
        targets = ET.parse(ROOT.parent / "tModLoader.targets")
        tml = Path(next(node.attrib["Project"] for node in targets.iter()
                        if node.tag.split("}")[-1] == "Import")).parent
    tml = tml.resolve()
    saves = Path(options.saves or ROOT.parents[1]).resolve()
    for file in ("tModLoader.dll", "dotnet/dotnet.exe", "tMLMod.targets"):
        if not (tml / file).is_file():
            raise FileNotFoundError(tml / file)
    dotnet = shutil.which("dotnet")
    if not dotnet:
        raise RuntimeError("dotnet SDK was not found on PATH.")
    sdks = subprocess.check_output([dotnet, "--list-sdks"], text=True, timeout=20)
    if not re.search(r"^8[.]", sdks, re.MULTILINE):
        raise RuntimeError("Install .NET SDK 8 or make it visible on PATH.")
    runtime = json.loads((tml / "tModLoader.runtimeconfig.json").read_text())
    tfm = runtime["runtimeOptions"]["tfm"]
    if tfm != "net8.0":
        raise RuntimeError(f"The installed loader targets {tfm}. This harness uses net8.0.")
    enabled = saves / "Mods/enabled.json"
    return dict(tml=str(tml), saves=str(saves), dotnet=dotnet, sdks=sdks.splitlines(),
                version=tfm, python=sys.version.split()[0], pillow=PIL.__version__,
                pyautogui=gui_version, heroEnabled=enabled.is_file() and
                "HEROsMod" in json.loads(enabled.read_text(encoding="utf-8-sig")))


def save_files(saves):
    for folder, stem, extensions in (("Players", "ModTester", (".plr", ".tplr")),
                                     ("Worlds", "ModTestWorld", (".wld", ".twld"))):
        for ext in extensions:
            file = saves / folder / (stem + ext)
            if not file.is_file() and ext.startswith(".t"):
                continue
            if not file.is_file():
                raise FileNotFoundError(f"Missing test fixture: {file}")
            yield file
            yield from (p for p in file.parent.glob(file.name + ".bak*") if p.is_file())
    maps = saves / "Players/ModTester"
    if maps.is_dir():
        yield from (p for p in maps.rglob("*") if p.is_file())


def digest(file):
    with file.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def verify_originals(run, env):
    manifest = run / "backup/sha256.json"
    if not manifest.is_file():
        return
    for relative, expected in json.loads(manifest.read_text()).items():
        for root in (Path(env["saves"]), run / "backup"):
            if digest(root / relative) != expected:
                raise RuntimeError(f"Fixture checksum changed: {root / relative}")


def prepare(run, env, scenario_path):
    saves = Path(env["saves"])
    profile = run / "profile"
    profile.mkdir()
    (profile / "Mods").mkdir()
    shutil.copyfile(scenario_path, run / "scenario.json")
    hashes = {}
    for source in save_files(saves):
        relative = source.relative_to(saves)
        backup = run / "backup" / relative
        backup.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source, backup)
        hashes[str(relative)] = digest(source)
        if digest(backup) != hashes[str(relative)]:
            raise RuntimeError(f"Fixture changed during backup: {relative}")
        work = profile / relative
        work.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(backup, work)
    (run / "backup/sha256.json").write_text(json.dumps(hashes, indent=2))
    # Reuse the loader's first-run markers, without copying account/config secrets.
    config = saves / "config.json"
    old = json.loads(config.read_text(encoding="utf-8-sig")) if config.exists() else {}
    settings = {k: v for k, v in old.items() if k.startswith(("LastLaunched", "LastPreview", "SeenFirstLaunch"))}
    settings.update(Language="en-US", Fullscreen=False, WindowBorderless=False,
                    DisplayWidth=1280, DisplayHeight=720, WindowMaximized=False,
                    AutoPause=False, AutoSave=False, FrameSkipMode=0, ThrottleWhenInactive=False,
                    WarnedFamilyShareDontShowAgain=True, ShowModMenuNotifications=False,
                    ShowNewUpdatedModsInfo=False)
    (profile / "config.json").write_text(json.dumps(settings))
    scenario = json.loads(scenario_path.read_text(encoding="utf-8-sig"))
    if scenario.get("sakuraDiagnostics"):
        quality = scenario.get("sakuraQuality", "High")
        if quality not in ("Low", "Medium", "High", "Ultra"):
            raise ValueError(f"Unknown Sakura quality: {quality}")
        configs = profile / "ModConfigs"
        configs.mkdir(exist_ok=True)
        (configs / "helluvia01_SakuraVisualConfig.json").write_text(
            json.dumps({"Diagnostics": True, "Quality": quality}))
    sources = profile / "ModSources"
    shutil.copytree(ROOT, sources / MODS[0],
                    ignore=shutil.ignore_patterns(".*", "bin", "obj", "testing", "__pycache__"))
    shutil.copytree(ROOT / "testing" / MODS[1], sources / MODS[1])


def loader_command(run, env):
    tml = Path(env["tml"])
    return [str(tml / "dotnet/dotnet.exe"), "tModLoader.dll",
            "-tmlsavedirectory", str(run / "profile"),
            "-savedirectory", str(run / "terraria")]


def build(run, env):
    profile = run / "profile"
    with (run / "build.log").open("wb") as log:
        for name in MODS:
            command = loader_command(run, env) + ["-server", "-build", str(profile / "ModSources" / name)]
            log.write((subprocess.list2cmdline(command) + "\n").encode())
            log.flush()
            result = subprocess.run(command, cwd=env["tml"], stdout=log,
                                    stderr=subprocess.STDOUT, timeout=180,
                                    creationflags=subprocess.CREATE_NO_WINDOW)
            artifact = profile / "Mods" / (name + ".tmod")
            if result.returncode or not artifact.is_file() or not artifact.stat().st_size:
                raise RuntimeError(f"Build of {name} failed ({result.returncode}). See {run / 'build.log'}")
    (profile / "Mods/enabled.json").write_text(json.dumps(MODS))


def client_command(run, env):
    child_env = dict(os.environ, HELLUVIA_TEST_RUN=str(run), HELLUVIA_TEST_SAVE=str(run / "profile"),
                     HELLUVIA_TEST_TOKEN=secrets.token_hex(24))
    return loader_command(run, env), env["tml"], child_env
