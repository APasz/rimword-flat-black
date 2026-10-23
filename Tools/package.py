"""Copy the mod's runtime files into a fresh release directory."""

from __future__ import annotations

import argparse
from pathlib import Path
import shutil


RELEASE_FILES = (
    Path("About/About.xml"),
    Path("About/Preview.png"),
    Path("About/PublishedFileId.txt"),
    Path("LoadFolders.xml"),
    Path("1.7/Assemblies/FlatBlack.dll"),
)
CONTENT_DIRECTORIES = (Path("1.7/Defs"), Path("1.7/Textures"), Path("1.7/Languages"), Path("1.7/Fonts"))
IGNORE_CONTENT = shutil.ignore_patterns("*.bak", "*.tmp", "__pycache__", ".DS_Store", "Thumbs.db")


def package(repository: Path, destination: Path) -> None:
    """Package real files, excluding development files and generated asset packs."""
    repository = repository.resolve(strict=True)
    destination = destination.resolve()
    for relative in RELEASE_FILES:
        if not (repository / relative).is_file():
            raise FileNotFoundError(f"Missing release file: {relative}. Build the mod before packaging.")
    for relative in CONTENT_DIRECTORIES:
        directory = repository / relative
        if not directory.is_dir():
            raise FileNotFoundError(f"Missing content directory: {relative}.")
    for relative in (Path("About"), Path("1.7")):
        if destination.is_relative_to((repository / relative).resolve()):
            raise ValueError(f"The release directory cannot be inside {relative}.")

    destination.mkdir(parents=True, exist_ok=False)
    for relative in RELEASE_FILES:
        target = destination / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(repository / relative, target, follow_symlinks=True)
    for relative in CONTENT_DIRECTORIES:
        shutil.copytree(repository / relative, destination / relative, symlinks=False, ignore=IGNORE_CONTENT)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=Path("dist/FlatBlack"), help="A directory that does not already exist")
    args = parser.parse_args()
    repository = Path(__file__).resolve().parents[1]
    try:
        package(repository, args.output)
    except (OSError, ValueError) as error:
        parser.exit(1, f"Packaging failed: {error}\n")
    print(f"Release directory: {args.output.resolve()}")


if __name__ == "__main__":
    main()
