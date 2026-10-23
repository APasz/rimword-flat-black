"""Boot isolated RimWorld instances and verify the skin with a later texture mod."""

from __future__ import annotations

import argparse
import os
import shutil
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET
from pathlib import Path
from typing import Literal


def write_metadata(folder: Path, package_id: str, name: str) -> None:
    about = folder / "About"
    about.mkdir(parents=True, exist_ok=True)
    root = ET.Element("ModMetaData")
    for tag, value in (
        ("name", name),
        ("author", "Flat Black tests"),
        ("packageId", package_id),
        ("description", "Temporary smoke test fixture."),
    ):
        ET.SubElement(root, tag).text = value
    ET.SubElement(ET.SubElement(root, "supportedVersions"), "li").text = "1.7"
    ET.SubElement(ET.SubElement(root, "loadAfter"), "li").text = "apasz.flatblack"
    ET.ElementTree(root).write(about / "About.xml", encoding="utf-8", xml_declaration=True)


def stage_game(source: Path, harmony: Path, repository: Path, output: Path) -> Path:
    game = output / "game"
    game.mkdir(parents=True, exist_ok=True)
    for entry in source.iterdir():
        if entry.name == "Mods":
            continue
        destination = game / entry.name
        if destination.exists() or destination.is_symlink():
            continue
        if entry.name == "RimWorld":
            shutil.copy2(entry, destination)
        else:
            destination.symlink_to(entry, target_is_directory=entry.is_dir())

    mods = game / "Mods"
    mods.mkdir(exist_ok=True)
    for name, target in (("Harmony", harmony), ("FlatBlack", repository)):
        link = mods / name
        if not link.is_symlink():
            link.symlink_to(target, target_is_directory=True)

    smoke = mods / "FlatBlackSmoke"
    write_metadata(smoke, "apasz.flatblack.smoke", "Flat Black smoke tests")
    (smoke / "Assemblies").mkdir(exist_ok=True)
    shutil.copy2(repository / "Tests/bin/Release/net10.0/FlatBlack.Tests.dll", smoke / "Assemblies/FlatBlack.Tests.dll")

    override = mods / "FlatBlackOverride"
    write_metadata(override, "apasz.flatblack.smokeoverride", "Flat Black later texture fixture")
    texture = override / "Textures/UI/Widgets/ButtonBG.png"
    texture.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(repository / "1.7/Textures/FlatBlack/UI/Widgets/ButtonBGClick.png", texture)
    return game


def run_case(
    game: Path,
    output: Path,
    case: Literal["baseline", "override"],
    *,
    headless: bool,
    x11: bool,
    bake: bool = False,
    gameplay: bool = False,
) -> None:
    save = output / case
    save.mkdir(exist_ok=True)
    packages = ["brrainz.harmony", "apasz.flatblack", "apasz.flatblack.smoke"]
    if case == "override":
        packages.append("apasz.flatblack.smokeoverride")
        configuration = save / "Config"
        configuration.mkdir(exist_ok=True)
        settings = ET.Element("SettingsBlock")
        ET.SubElement(ET.SubElement(settings, "ModSettings", {"Class": "FlatBlack.ThemeSettings"}), "fontPreset").text = "Vanilla"
        ET.ElementTree(settings).write(configuration / "Mod_FlatBlack_FlatBlackMod.xml", encoding="utf-8", xml_declaration=True)
    command = [
        "./RimWorld",
        "-Headless" if headless else "-NoWindow",
        f"-savedatafolder={save}",
        f"-mod={','.join(packages)}",
        "-DisableLogStackTraces",
        "-NoFileLogCallstacks",
    ]
    if bake:
        command.append("-FlatBlackBakeTest")
    if gameplay:
        command.extend(("-quicktest", "-FlatBlackGameplayTest"))
    if case == "override":
        command.append("-FlatBlackVanillaFontTest")
    environment = os.environ.copy()
    for variable, folder in (
        ("XDG_CONFIG_HOME", "config"),
        ("XDG_DATA_HOME", "data"),
        ("XDG_STATE_HOME", "state"),
        ("XDG_CACHE_HOME", "cache"),
    ):
        directory = save / folder
        directory.mkdir(exist_ok=True)
        environment[variable] = str(directory)
    if x11:
        environment["XDG_SESSION_TYPE"] = "x11"
        environment.pop("WAYLAND_DISPLAY", None)

    log = output / f"{case}.log"
    print(f"Running {case}; log: {log}", flush=True)
    with log.open("w", encoding="utf-8") as stream:
        process = subprocess.Popen(
            command, cwd=game, stdout=stream, stderr=subprocess.STDOUT, env=environment, text=True
        )
        try:
            code = process.wait(timeout=120 if gameplay else 55)
        except subprocess.TimeoutExpired:
            process.terminate()
            try:
                process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait()
            raise RuntimeError(f"{case} timed out; inspect {log}") from None

    content = log.read_text(encoding="utf-8", errors="replace")
    marker = (
        "PASS boot and resource compatibility checks"
        if headless
        else "PASS rendered gameplay screens"
        if gameplay
        else "PASS rendered vanilla controls"
    )
    if code != 0 or f"[Flat Black smoke] {marker}" not in content:
        raise RuntimeError(f"{case} failed with exit code {code}; inspect {log}")
    if any(marker in content for marker in ("[Flat Black] Could not", "[Flat Black smoke] FAIL")):
        raise RuntimeError(f"Theme errors in {log}")
    if not headless:
        screenshots = (
            tuple(f"gameplay-{name}.png" for name in ("work", "health", "research", "inventory", "menu"))
            if gameplay
            else (
                "menu.png",
                "controls.png",
                "settings.png",
                "settings-custom.png",
                "settings-controls-bottom.png",
                "settings-vanilla.png",
                "settings-compact.png",
                "settings-compact-bottom.png",
                "settings-fonts.png",
                "font-controls.png",
            )
        )
        for name in screenshots:
            image = save / name
            if not image.is_file() or image.stat().st_size == 0:
                raise RuntimeError(f"Missing screenshot: {image}")
            print(f"Screenshot: {image}", flush=True)
    print(f"PASS {case}", flush=True)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    default_game = Path.home() / ".steam/steam/steamapps/common/RimWorld"
    parser.add_argument("--game", type=Path, default=default_game)
    parser.add_argument("--harmony", type=Path, help="Harmony workshop mod root")
    parser.add_argument("--output", type=Path)
    parser.add_argument("--headless", action="store_true", help="Verify boot without rendering or screenshots")
    parser.add_argument("--x11", action="store_true", help="Use X11 for hidden rendering on a Wayland desktop")
    parser.add_argument(
        "--packed", action="store_true", help="Bake a temporary release and verify it without loose textures"
    )
    parser.add_argument(
        "--gameplay", action="store_true", help="Generate an isolated test colony and capture dense gameplay screens"
    )
    args = parser.parse_args()
    if args.gameplay and args.headless:
        parser.error("Gameplay review requires rendering; omit --headless.")
    repository = Path(__file__).resolve().parents[1]
    game = args.game.resolve()
    harmony = args.harmony.resolve() if args.harmony else game.parent.parent / "workshop/content/294100/2009463077"
    output = args.output.resolve() if args.output else Path(tempfile.mkdtemp(prefix="flatblack-smoke-"))
    assembly = repository / "Tests/bin/Release/net10.0/FlatBlack.Tests.dll"
    if not (game / "RimWorld").is_file() or not (harmony / "About/About.xml").is_file():
        parser.error("Set --game and --harmony to the Linux RimWorld 1.7 installation and Harmony mod.")
    if not assembly.is_file():
        parser.error("Build Tests/FlatBlack.Tests.csproj in Release first.")

    staged = stage_game(game, harmony, repository, output)
    if args.packed:
        packaged = output / "package"
        subprocess.run([sys.executable, str(repository / "Tools/package.py"), "--output", str(packaged)], check=True)
        link = staged / "Mods/FlatBlack"
        if not link.is_symlink():
            raise RuntimeError(f"Expected a temporary test symlink: {link}")
        link.unlink()
        link.symlink_to(packaged, target_is_directory=True)
        baking = output / "baking"
        baking.mkdir()
        run_case(staged, baking, "baseline", headless=True, x11=args.x11, bake=True)
        (packaged / "1.7/Textures").rename(output / "source-textures")
        for sidecar in (packaged / "1.7").glob("Assets.zip.*"):
            sidecar.unlink()
    for case in ("baseline", "override"):
        run_case(staged, output, case, headless=args.headless, x11=args.x11, gameplay=args.gameplay)


if __name__ == "__main__":
    main()
