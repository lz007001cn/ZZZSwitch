from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def resolve_under(root: Path, relative: str) -> Path:
    candidate = (root / relative.replace("\\", "/")).resolve()
    try:
        candidate.relative_to(root.resolve())
    except ValueError as error:
        raise ValueError(f"Unsafe package path: {relative}") from error
    return candidate


def update(config_root: Path, package_root: Path, check_only: bool) -> int:
    profiles: dict[str, dict] = {}
    for profile_path in sorted((config_root / "profiles").glob("*.json")):
        profile = json.loads(profile_path.read_text(encoding="utf-8"))
        profiles[profile["id"]] = profile

    changed_files = 0
    checked_entries = 0
    package_version = package_root.name
    for manifest_path in sorted((config_root / "transitions").glob("*.json")):
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        if not manifest.get("enabled", True):
            continue
        if manifest.get("gameVersion") != package_version:
            continue

        target_profile = profiles.get(manifest["targetProfile"])
        if target_profile is None:
            raise ValueError(
                f"Unknown target profile in {manifest_path.name}: "
                f"{manifest['targetProfile']}"
            )

        directory = package_root / target_profile["packageDirectoryName"]
        manifest_changed = False
        for entry in manifest.get("replaceFiles", []):
            source = resolve_under(directory, entry["source"])
            if not source.is_file():
                raise FileNotFoundError(f"Package file not found: {source}")

            length = source.stat().st_size
            digest = sha256(source)
            checked_entries += 1
            if entry.get("length") != length or str(entry.get("sha256", "")).upper() != digest:
                entry["length"] = length
                entry["sha256"] = digest
                manifest_changed = True

        if manifest_changed:
            changed_files += 1
            if not check_only:
                manifest_path.write_text(
                    json.dumps(manifest, ensure_ascii=False, indent=2) + "\n",
                    encoding="utf-8",
                )

    if checked_entries == 0:
        raise ValueError(
            f"No enabled transition manifests found for package version {package_version}."
        )

    action = "would update" if check_only else "updated"
    print(f"Checked {checked_entries} package files; {action} {changed_files} manifests.")
    return 1 if check_only and changed_files else 0


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Write deterministic length and SHA-256 metadata into ZZZSwitch manifests."
    )
    parser.add_argument("config_root", type=Path)
    parser.add_argument("package_root", type=Path)
    parser.add_argument(
        "--check",
        action="store_true",
        help="Verify manifests without writing; exit 1 when an update is required.",
    )
    args = parser.parse_args()
    return update(args.config_root.resolve(), args.package_root.resolve(), args.check)


if __name__ == "__main__":
    raise SystemExit(main())
