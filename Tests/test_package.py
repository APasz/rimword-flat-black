"""Release packaging checks independent of the installed game."""

from __future__ import annotations

from pathlib import Path
import tempfile
import unittest

from Tools.package import package


class PackageTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory(prefix="flatblack-package-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.repository = self.root / "repository"
        self.repository.mkdir()
        self.files = {
            "About/About.xml": b"<ModMetaData />",
            "About/Preview.png": b"preview",
            "About/PublishedFileId.txt": b"3824985716",
            "LoadFolders.xml": b"<loadFolders />",
            "1.7/Assemblies/FlatBlack.dll": b"mod assembly",
            "1.7/Defs/Theme.xml": b"<Defs />",
            "1.7/Languages/English/Keyed/FlatBlack.xml": b"<LanguageData />",
            "1.7/Textures/UI/Widgets/ButtonBG.png": b"button texture",
            "1.7/Fonts/Exo2/Exo2-Regular.ttf": b"body font",
            "1.7/Fonts/Exo2/OFL.txt": b"body font licence",
            "1.7/Fonts/Oxanium/Oxanium-Medium.ttf": b"heading font",
            "1.7/Fonts/Oxanium/OFL.txt": b"heading font licence",
        }
        for relative, content in self.files.items():
            path = self.repository / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(content)
        for relative in (
            "About/PublishedFileId.txt.bak", "Art/Deferred/Loader.png", "Source/FlatBlack.cs",
            "Tests/Test.dll", "1.7/Assets.zip", "1.7/Assets.zip.stamps",
            "1.7/Assemblies/Assembly-CSharp.dll", "1.7/Defs/Theme.xml.bak",
            "1.7/Textures/UI/Widgets/ButtonBG.png.bak",
        ):
            path = self.repository / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(b"excluded")

    def test_release_contains_only_runtime_files_and_dereferences_symlinks(self) -> None:
        install = self.root / "install"
        install.mkdir()
        for name in ("About", "1.7", "LoadFolders.xml"):
            (install / name).symlink_to(self.repository / name, target_is_directory=name != "LoadFolders.xml")
        texture = self.repository / "1.7/Textures/UI/Widgets/ButtonBG.png"
        external = self.root / "external.png"
        texture.replace(external)
        texture.symlink_to(external)
        nested = self.root / "additional-textures"
        nested.mkdir()
        (nested / "CheckOn.png").write_bytes(b"nested texture")
        (self.repository / "1.7/Textures/Linked").symlink_to(nested, target_is_directory=True)

        destination = self.root / "release"
        package(install, destination)

        expected = self.files | {"1.7/Textures/Linked/CheckOn.png": b"nested texture"}
        actual = {path.relative_to(destination).as_posix(): path.read_bytes() for path in destination.rglob("*") if path.is_file()}
        self.assertEqual(actual, expected)
        self.assertFalse(any(path.is_symlink() for path in destination.rglob("*")))

    def test_missing_assembly_fails_before_creating_release(self) -> None:
        (self.repository / "1.7/Assemblies/FlatBlack.dll").unlink()
        destination = self.root / "release"
        with self.assertRaisesRegex(FileNotFoundError, "FlatBlack.dll"):
            package(self.repository, destination)
        self.assertFalse(destination.exists())

    def test_existing_destination_is_untouched(self) -> None:
        destination = self.root / "release"
        destination.mkdir()
        sentinel = destination / "keep.txt"
        sentinel.write_bytes(b"existing release")
        with self.assertRaises(FileExistsError):
            package(self.repository, destination)
        self.assertEqual(list(destination.iterdir()), [sentinel])
        self.assertEqual(sentinel.read_bytes(), b"existing release")

    def test_destination_inside_runtime_content_is_rejected(self) -> None:
        destination = self.repository / "1.7/Textures/release"
        with self.assertRaisesRegex(ValueError, "cannot be inside"):
            package(self.repository, destination)
        self.assertFalse(destination.exists())


if __name__ == "__main__":
    unittest.main()
