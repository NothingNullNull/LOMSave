"""Build and verify the extract-to-game-root bundle using pinned official files."""

import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
import re
import urllib.request
import zipfile


ROOT = Path(__file__).resolve().parents[1]
RESOURCES = Path(__file__).resolve().parent / "bundle"
DOCS = "LOMSave2说明/"
MOD_DLL = "BepInEx/plugins/LOMSave2/LOMSave2.dll"
SOURCE_PATH = DOCS + "第三方源码/UnityDoorstop-4.5.0-source.zip"


def require(condition, message):
    if not condition:
        raise ValueError(message)


def sha256(data):
    return hashlib.sha256(data).hexdigest()


def components():
    return json.loads((RESOURCES / "components.json").read_text(encoding="utf-8"))


def read_zip(path):
    files = {}
    seen = set()
    with zipfile.ZipFile(path) as archive:
        for info in archive.infolist():
            name = info.filename
            parts = PurePosixPath(name).parts
            require(name and not name.startswith("/") and "\\" not in name
                    and ":" not in name and ".." not in parts,
                    f"Unsafe ZIP path: {name}")
            require(name.casefold() not in seen, f"Duplicate ZIP path: {name}")
            seen.add(name.casefold())
            require((info.external_attr >> 16) & 0o170000 != 0o120000,
                    f"ZIP contains a symbolic link: {name}")
            if not info.is_dir():
                files[name] = archive.read(info)
    return files


def write_zip(path, files):
    # Stable timestamps make rebuilds independent of download/extraction dates.
    with zipfile.ZipFile(path, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for name, data in sorted(files.items()):
            entry = zipfile.ZipInfo(name, (2026, 1, 1, 0, 0, 0))
            entry.compress_type = zipfile.ZIP_DEFLATED
            entry.external_attr = 0o100644 << 16
            archive.writestr(entry, data)


def cached_download(asset, cache):
    cache.mkdir(parents=True, exist_ok=True)
    path = cache / asset["name"]
    if not path.is_file():
        request = urllib.request.Request(asset["url"], headers={"User-Agent": "LOMSave-release"})
        with urllib.request.urlopen(request, timeout=60) as response:
            data = response.read()
        require(sha256(data) == asset["sha256"], f"Download hash mismatch: {path.name}")
        path.write_bytes(data)
    require(sha256(path.read_bytes()) == asset["sha256"], f"Cache hash mismatch: {path.name}")
    return path


def loader_path(name):
    return DOCS + "BepInEx-changelog.txt" if name == "changelog.txt" else name


def check_mod(files, dll):
    require(files.get(MOD_DLL) == dll, "Packaged MOD DLL differs from standalone DLL")
    require(DOCS + "README.md" in files, "Package README is missing")
    for name, data in files.items():
        require(name == MOD_DLL or (name.startswith(DOCS) and name.endswith(".md")),
                f"Unexpected MOD package file: {name}")
        if name.endswith("README.md"):
            require(re.search(r"QQ\s*群|398012894", data.decode("utf-8-sig"), re.I) is None,
                    "README still contains the removed QQ group")


def expected_extras(spec):
    result = {loader_path(name): digest for name, digest in spec["bepinex"]["files"].items()}
    result.update(spec["notices"])
    result[SOURCE_PATH] = spec["doorstop_source"]["sha256"]
    return result


def check_bundle(bundle, mod, spec):
    extras = expected_extras(spec)
    require(set(bundle) == set(mod) | set(extras),
            f"Bundle layout differs: missing={sorted((set(mod) | set(extras)) - set(bundle))}, "
            f"unexpected={sorted(set(bundle) - (set(mod) | set(extras)))}")
    for name, data in mod.items():
        require(bundle[name] == data, f"Bundle differs from MOD package: {name}")
    for name, digest in extras.items():
        require(sha256(bundle[name]) == digest, f"Bundled component hash mismatch: {name}")


def build_bundle(mod_package, output, cache):
    spec = components()
    mod = read_zip(mod_package)
    require(MOD_DLL in mod, "MOD DLL is missing")
    check_mod(mod, mod[MOD_DLL])
    loader = read_zip(cached_download(spec["bepinex"], cache))
    require({name: sha256(data) for name, data in loader.items()} == spec["bepinex"]["files"],
            "Official BepInEx file list differs from pinned metadata")
    source = cached_download(spec["doorstop_source"], cache)
    bundle = dict(mod)
    bundle.update({loader_path(name): data for name, data in loader.items()})
    bundle[DOCS + "第三方组件.md"] = (RESOURCES / "THIRD-PARTY.md").read_bytes()
    for path in (RESOURCES / "licenses").glob("*.txt"):
        bundle[DOCS + "第三方许可/" + path.name] = path.read_bytes()
    bundle[SOURCE_PATH] = source.read_bytes()
    check_bundle(bundle, mod, spec)
    output.parent.mkdir(parents=True, exist_ok=True)
    write_zip(output, bundle)
    print(f"Bundle created: {output.name} ({output.stat().st_size} bytes)")


def verify_release(directory, version):
    require(re.fullmatch(r"\d+\.\d+\.\d+", version), "Invalid release version")
    names = {"LOMSave2.dll", f"LOMSave-v{version}.zip", f"LOMSave-v{version}-OneClick.zip"}
    checksums = {}
    for line in (directory / "SHA256SUMS.txt").read_text(encoding="utf-8-sig").splitlines():
        match = re.fullmatch(r"([0-9a-f]{64})  (.+)", line)
        require(match is not None, "Malformed checksum line")
        digest, name = match.groups()
        require(name in names and name not in checksums, f"Unexpected/duplicate checksum: {name}")
        checksums[name] = digest
    require(set(checksums) == names, "Release must include DLL, MOD ZIP and OneClick ZIP checksums")
    for name, digest in checksums.items():
        require(sha256((directory / name).read_bytes()) == digest, f"Release hash mismatch: {name}")
    mod = read_zip(directory / f"LOMSave-v{version}.zip")
    check_mod(mod, (directory / "LOMSave2.dll").read_bytes())
    check_bundle(read_zip(directory / f"LOMSave-v{version}-OneClick.zip"), mod, components())
    print(f"Release {version}: DLL, MOD ZIP, OneClick ZIP, layout and official components verified")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    build = commands.add_parser("build")
    build.add_argument("--mod-package", type=Path, required=True)
    build.add_argument("--output", type=Path, required=True)
    build.add_argument("--cache-dir", type=Path, default=ROOT / ".work" / "bundle-cache")
    verify = commands.add_parser("verify")
    verify.add_argument("--release-dir", type=Path, required=True)
    verify.add_argument("--version", required=True)
    args = parser.parse_args()
    if args.command == "build":
        build_bundle(args.mod_package, args.output, args.cache_dir)
    else:
        verify_release(args.release_dir, args.version)


if __name__ == "__main__":
    main()
