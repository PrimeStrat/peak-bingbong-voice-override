import io
import json
import os
import shutil
import subprocess
import sys
import time
import urllib.error
import urllib.request
import zipfile
from pathlib import Path

MOD_AUTHOR = "PrimeStrat"
MOD_NAME = "BingBongVoiceOverride"
MOD_PLUGIN_FOLDER = f"{MOD_AUTHOR}-{MOD_NAME}"
BEPINEX_NAMESPACE = "BepInEx"
BEPINEX_NAME = "BepInExPack_PEAK"
THUNDERSTORE_API = "https://thunderstore.io/api/experimental/package"
REQUEST_HEADERS = {"User-Agent": f"{MOD_PLUGIN_FOLDER}/1.0"}

REPO_ROOT = Path(__file__).resolve().parent.parent
CSPROJ = REPO_ROOT / "src" / "MyMod.csproj"
DIST_DIR = REPO_ROOT / "dist"

PROFILE_BASE = Path(
    os.environ.get("APPDATA", ""),
    "Thunderstore Mod Manager",
    "DataFolder",
    "PEAK",
    "profiles",
)

PROFILE_SUBDIRS = [
    "_state",
    "BepInEx/plugins",
    "BepInEx/config",
    "BepInEx/core",
    "BepInEx/patchers",
]


def main():
    print("=" * 60)
    print("  BingBong Voice Override -- Create Test Profile")
    print("=" * 60)

    print("\n[1/4] Building and packaging mod...")
    mod_zip_path, mod_version = _build_and_package()
    print(f"  Packaged: {mod_zip_path.name}  (version: {mod_version})")

    print("\n[2/3] Downloading BepInEx pack...")
    bepinex_zip, bepinex_version = _download_bepinex()

    profile_name = f"{MOD_NAME}-Test-v{mod_version}"
    print(f"\n[3/3] Creating Thunderstore profile: {profile_name}")
    profile_dir = _create_profile(profile_name, mod_zip_path, bepinex_zip, mod_version, bepinex_version)

    print("\n" + "=" * 60)
    print("  Profile created!")
    print(f"  Profile : {profile_name}")
    print(f"  Path    : {profile_dir}")
    print()
    print("  To use:")
    print("  1. Open Thunderstore Mod Manager")
    print("  2. Select PEAK")
    print(f"  3. Switch to the '{profile_name}' profile")
    print("  4. Drop custom .wav or .ogg files into:")
    print(f"     {profile_dir / 'BepInEx' / 'plugins' / MOD_PLUGIN_FOLDER / 'sounds'}")
    print("  5. Click 'Start Modded' to launch")
    print("=" * 60)


def _build_and_package() -> tuple[Path, str]:
    """Build the mod in Release mode then zip build/package/ into dist/.

    @return: Tuple of (zip_path, version_string).
    """
    result = subprocess.run(
        ["dotnet", "build", str(CSPROJ), "-c", "Release"],
        cwd=REPO_ROOT,
    )
    if result.returncode != 0:
        print("ERROR: dotnet build failed.")
        sys.exit(result.returncode)

    package_dir = REPO_ROOT / "build" / "package"
    if not package_dir.exists() or not any(package_dir.iterdir()):
        print("ERROR: build/package/ is empty after build. Check the csproj StagePackage target.")
        sys.exit(1)

    props = _read_build_props()
    author = props.get("ModAuthor", MOD_AUTHOR)
    name = props.get("ModName", MOD_NAME)
    version = props.get("ModVersion", "???")

    DIST_DIR.mkdir(parents=True, exist_ok=True)
    zip_path = DIST_DIR / f"{author}-{name}-{version}.zip"
    if zip_path.exists():
        zip_path.unlink()

    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as zf:
        for file in package_dir.rglob("*"):
            if file.is_file():
                zf.write(file, file.relative_to(package_dir))

    return zip_path, version


def _read_build_props() -> dict[str, str]:
    """Parse ModAuthor, ModName, and ModVersion from Directory.Build.props.

    @return: Dict with keys ModAuthor, ModName, ModVersion.
    """
    props_path = REPO_ROOT / "Directory.Build.props"
    result: dict[str, str] = {}
    if not props_path.exists():
        return result
    import re
    text = props_path.read_text(encoding="utf-8")
    for key in ("ModAuthor", "ModName", "ModVersion"):
        match = re.search(rf"<{key}>([^<]+)</{key}>", text)
        if match:
            result[key] = match.group(1).strip()
    return result


def _download_bepinex() -> tuple[bytes, str]:
    """Fetch the BepInEx pack zip and version from the Thunderstore API.

    @return: Tuple of (zip_bytes, version_string).
    """
    url = f"{THUNDERSTORE_API}/{BEPINEX_NAMESPACE}/{BEPINEX_NAME}/"
    print(f"  Querying Thunderstore for {BEPINEX_NAMESPACE}-{BEPINEX_NAME}...")
    data = _api_get(url)
    if not data:
        print(f"ERROR: Could not fetch BepInEx metadata from Thunderstore.")
        sys.exit(1)

    latest = data.get("latest", {})
    download_url = latest.get("download_url", "")
    version = latest.get("version_number", "???")
    if not download_url:
        print("ERROR: No download_url found in BepInEx Thunderstore metadata.")
        sys.exit(1)

    print(f"  Found BepInEx {version}")
    return _download_bytes(download_url, f"BepInEx {version}"), version


def _create_profile(
    profile_name: str,
    mod_zip_path: Path,
    bepinex_zip: bytes,
    mod_version: str,
    bepinex_version: str,
) -> Path:
    """Create the Thunderstore profile directory and install all mods.

    @param profile_name: Name for the Thunderstore profile folder.
    @param mod_zip_path: Path to the local built mod zip.
    @param bepinex_zip: Raw bytes of the BepInEx zip archive.
    @param mod_version: Version string for the mod.
    @param bepinex_version: Version string for BepInEx as fetched from Thunderstore.
    @return: Path to the created profile directory.
    """
    profile_dir = PROFILE_BASE / profile_name

    if profile_dir.exists():
        print(f"  Profile '{profile_name}' already exists. Overwriting...")
        shutil.rmtree(profile_dir)

    for sub in PROFILE_SUBDIRS:
        (profile_dir / sub).mkdir(parents=True, exist_ok=True)

    print("  Extracting BepInEx...")
    _install_bepinex_zip(bepinex_zip, profile_dir)

    print("  Installing mod DLL...")
    _install_mod_zip(mod_zip_path, profile_dir)

    sounds_dir = profile_dir / "BepInEx" / "plugins" / MOD_PLUGIN_FOLDER / "sounds"
    sounds_dir.mkdir(parents=True, exist_ok=True)
    print(f"  Created sounds folder: {sounds_dir}")

    _write_debug_bepinex_config(profile_dir)
    print("  Wrote BepInEx debug config (console window enabled)")

    bepinex_ver = _parse_version(bepinex_version)
    mod_ver = _parse_version(mod_version)
    mods_yml = _build_mods_yml(bepinex_ver, mod_ver)
    (profile_dir / "mods.yml").write_text(mods_yml, encoding="utf-8")
    print("  Wrote mods.yml")

    return profile_dir


def _install_bepinex_zip(zip_bytes: bytes, profile_dir: Path):
    """Extract the BepInEx pack into the profile directory, stripping any top-level wrapper. Top-level package metadata (icon.png, manifest.json, README, CHANGELOG) is staged into BepInEx/plugins/<namespace>-<name>/ so mod managers display the icon.

    @param zip_bytes: Raw bytes of the BepInEx zip.
    @param profile_dir: Destination profile directory.
    @return: None
    """
    _META_NAMES = {"icon.png", "manifest.json", "readme.md", "changelog.md"}
    bepinex_plugin_dir = profile_dir / "BepInEx" / "plugins" / f"{BEPINEX_NAMESPACE}-{BEPINEX_NAME}"
    bepinex_plugin_dir.mkdir(parents=True, exist_ok=True)

    with zipfile.ZipFile(io.BytesIO(zip_bytes)) as zf:
        members = zf.namelist()
        strip_prefix = _detect_wrapping_folder(members)
        for member in members:
            if member.startswith("/") or ".." in member:
                continue
            bare_name = member.split("/")[-1]
            rel = member[len(strip_prefix):] if strip_prefix and member.startswith(strip_prefix) else member
            if not rel:
                continue
            if bare_name.lower() in _META_NAMES and "/" not in rel.rstrip("/"):
                if member.endswith("/"):
                    continue
                target = bepinex_plugin_dir / bare_name
                with zf.open(member) as src, open(target, "wb") as dst:
                    shutil.copyfileobj(src, dst)
                continue
            target = profile_dir / rel
            if member.endswith("/"):
                target.mkdir(parents=True, exist_ok=True)
            else:
                target.parent.mkdir(parents=True, exist_ok=True)
                with zf.open(member) as src, open(target, "wb") as dst:
                    shutil.copyfileobj(src, dst)


def _install_mod_zip(zip_path: Path, profile_dir: Path):
    """Extract the mod DLL plus icon.png/manifest.json/README into BepInEx/plugins/<author>-<mod>/.

    @param zip_path: Path to the built mod zip file.
    @param profile_dir: Destination profile directory.
    @return: None
    """
    plugin_dir = profile_dir / "BepInEx" / "plugins" / MOD_PLUGIN_FOLDER
    plugin_dir.mkdir(parents=True, exist_ok=True)

    _COPY_META = {"icon.png", "manifest.json", "readme.md", "changelog.md"}
    with zipfile.ZipFile(zip_path) as zf:
        for member in zf.namelist():
            if member.startswith("/") or ".." in member:
                continue
            filename = Path(member).name
            lower = filename.lower()
            is_binary = lower.endswith(".dll") or lower.endswith(".pdb")
            is_meta = lower in _COPY_META and "/" not in member.rstrip("/")
            if not is_binary and not is_meta:
                continue
            target = plugin_dir / filename
            with zf.open(member) as src, open(target, "wb") as dst:
                shutil.copyfileobj(src, dst)
            print(f"    Installed: {filename}")


def _build_mods_yml(bepinex_ver: tuple[int, int, int], mod_ver: tuple[int, int, int]) -> str:
    """Generate a mods.yml manifest for the Thunderstore profile.

    @param bepinex_ver: BepInEx version as (major, minor, patch).
    @param mod_ver: Mod version as (major, minor, patch).
    @return: YAML string for mods.yml.
    """
    now_ms = int(time.time() * 1000)
    bx_maj, bx_min, bx_pat = bepinex_ver
    mod_maj, mod_min, mod_pat = mod_ver
    bepinex_pkg = f"{BEPINEX_NAMESPACE}-{BEPINEX_NAME}"
    mod_pkg = f"{MOD_AUTHOR}-{MOD_NAME}"

    return (
        f"- manifestVersion: 1\n"
        f"  name: {bepinex_pkg}\n"
        f"  authorName: {BEPINEX_NAMESPACE}\n"
        f"  websiteUrl: https://thunderstore.io/c/peak/p/{BEPINEX_NAMESPACE}/{BEPINEX_NAME}/\n"
        f"  displayName: {BEPINEX_NAME}\n"
        f"  description: BepInEx pack for PEAK. Preconfigured and ready to use.\n"
        f"  gameVersion: '0'\n"
        f"  networkMode: both\n"
        f"  packageType: other\n"
        f"  installMode: managed\n"
        f"  installedAtTime: {now_ms}\n"
        f"  loaders: []\n"
        f"  dependencies: []\n"
        f"  incompatibilities: []\n"
        f"  optionalDependencies: []\n"
        f"  versionNumber:\n"
        f"    major: {bx_maj}\n"
        f"    minor: {bx_min}\n"
        f"    patch: {bx_pat}\n"
        f"  enabled: true\n"
        f"- manifestVersion: 1\n"
        f"  name: {mod_pkg}\n"
        f"  authorName: {MOD_AUTHOR}\n"
        f"  websiteUrl: https://thunderstore.io/c/peak/p/{MOD_AUTHOR}/{MOD_NAME}/\n"
        f"  displayName: {MOD_NAME}\n"
        f"  description: Drop custom audio files into the sounds folder to override Bing Bong's SFX in PEAK.\n"
        f"  gameVersion: '0'\n"
        f"  networkMode: both\n"
        f"  packageType: other\n"
        f"  installMode: managed\n"
        f"  installedAtTime: {now_ms}\n"
        f"  loaders: []\n"
        f"  dependencies:\n"
        f"    - {bepinex_pkg}-{bx_maj}.{bx_min}.{bx_pat}\n"
        f"  incompatibilities: []\n"
        f"  optionalDependencies: []\n"
        f"  versionNumber:\n"
        f"    major: {mod_maj}\n"
        f"    minor: {mod_min}\n"
        f"    patch: {mod_pat}\n"
        f"  enabled: true\n"
    )


def _api_get(url: str) -> dict | list | None:
    """Send a GET request and return parsed JSON, or None on 404.

    @param url: Full URL to request.
    @return: Parsed JSON as dict or list, or None if 404.
    """
    req = urllib.request.Request(url, headers=REQUEST_HEADERS)
    try:
        with urllib.request.urlopen(req) as resp:
            return json.loads(resp.read().decode())
    except urllib.error.HTTPError as exc:
        if exc.code == 404:
            return None
        raise


def _download_bytes(url: str, label: str) -> bytes:
    """Download a file from a URL with a progress indicator.

    @param url: Direct download URL.
    @param label: Display label for progress output.
    @return: Raw bytes of the downloaded file.
    """
    req = urllib.request.Request(url, headers=REQUEST_HEADERS)
    with urllib.request.urlopen(req) as resp:
        total = int(resp.headers.get("Content-Length", 0))
        data = bytearray()
        chunk_size = 1024 * 256
        while True:
            chunk = resp.read(chunk_size)
            if not chunk:
                break
            data.extend(chunk)
            downloaded_kb = len(data) // 1024
            if total:
                pct = len(data) * 100 // total
                total_kb = total // 1024
                print(f"\r  Downloading {label}: {pct:3d}%  ({downloaded_kb:,} KB / {total_kb:,} KB)", end="", flush=True)
            else:
                print(f"\r  Downloading {label}: {downloaded_kb:,} KB", end="", flush=True)
        print()
    return bytes(data)


def _detect_wrapping_folder(members: list[str]) -> str:
    """Detect if nested zip entries share a single top-level wrapping folder to strip.
    Root-level Thunderstore metadata files (icon.png, manifest.json, README.md) are
    intentionally ignored so they do not prevent prefix detection.

    @param members: List of zip entry paths.
    @return: The wrapping folder prefix to strip, or empty string if none.
    """
    nested = [m for m in members if "/" in m]
    if not nested:
        return ""
    top_dirs = {m.split("/")[0] for m in nested}
    if len(top_dirs) != 1:
        return ""
    candidate = next(iter(top_dirs)) + "/"
    if all(m.startswith(candidate) for m in nested):
        return candidate
    return ""


def _parse_version(version_string: str) -> tuple[int, int, int]:
    """Parse a version tag into major, minor, patch integers.

    @param version_string: Version string like 'v1.0.0', '1.0.0', or '5.4.2100'.
    @return: Tuple of (major, minor, patch).
    """
    cleaned = version_string.lstrip("v").split("-")[0]
    parts = cleaned.split(".")
    parts += ["0"] * (3 - len(parts))
    return int(parts[0]), int(parts[1]), int(parts[2])


def _write_debug_bepinex_config(profile_dir: Path):
    """Write a BepInEx.cfg that enables the console window so all log output appears in a separate terminal when the game runs.

    @param profile_dir: Root of the Thunderstore profile directory.
    @return: None
    """
    config_path = profile_dir / "BepInEx" / "config" / "BepInEx.cfg"
    config_path.parent.mkdir(parents=True, exist_ok=True)
    config_path.write_text(
        "[Logging.Console]\n"
        "Enabled = true\n"
        "HideDefaultConsoleWindow = false\n"
        "\n"
        "[Logging.Disk]\n"
        "WriteUnityLog = true\n"
        "AppendLog = false\n",
        encoding="utf-8",
    )


if __name__ == "__main__":
    main()
