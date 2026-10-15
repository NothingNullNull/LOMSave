"""Check complete release packages and reject broken extract-to-root bundles."""

import argparse
import contextlib
import io
from pathlib import Path
import shutil
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools"))
import package_bundle as bundle

parser = argparse.ArgumentParser()
parser.add_argument("--release-dir", type=Path, default=ROOT / "dist")
parser.add_argument("--version", default="2.0.2")
OPTIONS, REMAINING = parser.parse_known_args()


class ReleasePackageTests(unittest.TestCase):
    def setUp(self):
        (ROOT / ".work").mkdir(exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(prefix="package-tests-", dir=ROOT / ".work")
        self.addCleanup(self.temp.cleanup)
        self.directory = Path(self.temp.name)
        self.archive = f"LOMSave-v{OPTIONS.version}-OneClick.zip"
        self.names = ["LOMSave2.dll", f"LOMSave-v{OPTIONS.version}.zip", self.archive]
        for name in self.names + ["SHA256SUMS.txt"]:
            shutil.copyfile(OPTIONS.release_dir / name, self.directory / name)

    def verify(self):
        with contextlib.redirect_stdout(io.StringIO()):
            bundle.verify_release(self.directory, OPTIONS.version)

    def rewrite_bundle(self, files):
        bundle.write_zip(self.directory / self.archive, files)
        text = "".join(f"{bundle.sha256((self.directory / n).read_bytes())}  {n}\n" for n in self.names)
        (self.directory / "SHA256SUMS.txt").write_text(text, encoding="utf-8")

    def test_valid_release_and_extract_to_root(self):
        self.verify()
        game = self.directory / "game"
        (game / "BepInEx/config").mkdir(parents=True)
        markers = {"Mortal.exe": b"game-marker", "save.dat": b"save-marker",
                   "BepInEx/config/com.nn.lomsave2.cfg": b"existing-config",
                   "BepInEx/plugins/OtherMod.dll": b"other-mod"}
        for name, data in markers.items():
            path = game / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(data)
        files = bundle.read_zip(self.directory / self.archive)
        for _ in range(2):
            for name, data in files.items():
                path = game / name
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(data)
        self.assertTrue((game / "winhttp.dll").is_file())
        self.assertTrue((game / ".doorstop_version").is_file())
        for name, data in files.items():
            self.assertEqual((game / name).read_bytes(), data)
        for name, data in markers.items():
            self.assertEqual((game / name).read_bytes(), data)

    def test_missing_bundle_is_rejected(self):
        (self.directory / self.archive).unlink()
        with self.assertRaises(FileNotFoundError):
            self.verify()

    def test_missing_bundle_checksum_is_rejected(self):
        path = self.directory / "SHA256SUMS.txt"
        path.write_text("".join(line + "\n" for line in path.read_text().splitlines()
                                if self.archive not in line), encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "must include"):
            self.verify()

    def test_bad_checksum_is_rejected(self):
        (self.directory / self.archive).write_bytes(b"incomplete download")
        with self.assertRaisesRegex(ValueError, "Release hash mismatch"):
            self.verify()

    def test_wrong_loader_or_mod_is_rejected_even_with_updated_checksums(self):
        for name in ("winhttp.dll", "BepInEx/core/BepInEx.dll", bundle.MOD_DLL,
                     bundle.SOURCE_PATH, bundle.DOCS + "第三方许可/BepInEx.txt"):
            with self.subTest(file=name):
                files = bundle.read_zip(OPTIONS.release_dir / self.archive)
                files[name] = b"wrong version or corrupt component"
                self.rewrite_bundle(files)
                with self.assertRaises(ValueError):
                    self.verify()

    def test_missing_files_and_extra_files_are_rejected(self):
        original = bundle.read_zip(OPTIONS.release_dir / self.archive)
        for name in (".doorstop_version", "doorstop_config.ini",
                     bundle.DOCS + "第三方许可/UnityDoorstop.txt"):
            with self.subTest(missing=name):
                files = dict(original)
                del files[name]
                self.rewrite_bundle(files)
                with self.assertRaisesRegex(ValueError, "layout differs"):
                    self.verify()
        for name in ("Mortal_Data/Managed/Mortal.Core.dll", "BepInEx/config/player.cfg",
                     "BepInEx/plugins/LOMSave.dll", "Install.cmd", "save.dat", "../escape.txt"):
            with self.subTest(extra=name):
                files = dict(original)
                files[name] = b"must not ship"
                self.rewrite_bundle(files)
                with self.assertRaises(ValueError):
                    self.verify()

    def test_extra_parent_directory_is_rejected(self):
        files = bundle.read_zip(OPTIONS.release_dir / self.archive)
        self.rewrite_bundle({"LOMSave/" + name: data for name, data in files.items()})
        with self.assertRaisesRegex(ValueError, "layout differs"):
            self.verify()

    def test_corrupt_cached_download_is_rejected(self):
        spec = bundle.components()["bepinex"]
        (self.directory / spec["name"]).write_bytes(b"bad cached ZIP")
        with self.assertRaisesRegex(ValueError, "Cache hash mismatch"):
            bundle.cached_download(spec, self.directory)


if __name__ == "__main__":
    unittest.main(argv=[sys.argv[0]] + REMAINING)
