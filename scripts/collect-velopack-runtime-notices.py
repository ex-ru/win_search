#!/usr/bin/env python3
"""Collect pinned Velopack runtime crate license texts into a reviewable bundle.

The collector uses only Python's standard library. Crate archives are cached under
state/velopack-crates and are opened only after their SHA-256 matches Cargo.lock.
The inventory is intentionally a Cargo.lock registry-dependency superset; it does
not claim that every listed crate is included in the shipped updater binary.
"""

from __future__ import annotations

import argparse
import concurrent.futures
import hashlib
import json
import os
import re
import shutil
import sys
import tarfile
import tempfile
import time
import tomllib
from datetime import datetime, timezone
from pathlib import Path, PurePosixPath
from typing import Any
from urllib.error import HTTPError, URLError
from urllib.parse import quote, urlsplit
from urllib.request import Request, urlopen


REPOSITORY = "https://github.com/velopack/velopack"
COMMIT = "92d6a1c91716729d449034df5c50307dcce39493"
RAW_BASE = f"https://raw.githubusercontent.com/velopack/velopack/{COMMIT}"
CRATES_BASE = "https://static.crates.io/crates"
CRATES_IO_SOURCE = "registry+https://github.com/rust-lang/crates.io-index"
RETRIES = 3
TIMEOUT_SECONDS = 40
MAX_WORKERS = 12
CHUNK_SIZE = 1024 * 1024
LEGAL_PREFIXES = ("license", "copying", "notice")
LEGAL_DIRECTORIES = {"license", "licenses", "copying", "notice", "notices"}
SAFE_CRATE_NAME = re.compile(r"^[A-Za-z0-9_-]+$")
SAFE_CRATE_VERSION = re.compile(r"^[A-Za-z0-9][A-Za-z0-9.+_-]*$")
SAFE_CHECKSUM = re.compile(r"^[A-Fa-f0-9]{64}$")
SAFE_COMMIT = re.compile(r"^(?:[A-Fa-f0-9]{40}|[A-Fa-f0-9]{64})$")
WINDOWS_RESERVED = {
    "CON", "PRN", "AUX", "NUL",
    *(f"COM{number}" for number in range(1, 10)),
    *(f"LPT{number}" for number in range(1, 10)),
}


class CollectionError(Exception):
    """An expected collection failure represented by a stable reason code."""

    def __init__(self, reason: str):
        super().__init__(reason)
        self.reason = reason


def sha256_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(CHUNK_SIZE), b""):
            digest.update(chunk)
    return digest.hexdigest()


def crate_archive_url(name: str, version: str) -> str:
    encoded_name = quote(name, safe="-_.")
    encoded_filename = quote(f"{name}-{version}", safe="-_.")
    return f"{CRATES_BASE}/{encoded_name}/{encoded_filename}.crate"


def write_atomic(path: Path, data: bytes) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    descriptor, temporary_name = tempfile.mkstemp(prefix=f".{path.name}.", suffix=".tmp", dir=path.parent)
    temporary = Path(temporary_name)
    try:
        with os.fdopen(descriptor, "wb") as stream:
            stream.write(data)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)


def download_bytes(url: str) -> bytes:
    last_error = "download-failed"
    for attempt in range(RETRIES):
        request = Request(url, headers={"User-Agent": "win-search-license-collector/1.0"})
        try:
            with urlopen(request, timeout=TIMEOUT_SECONDS) as response:
                return response.read()
        except (HTTPError, URLError, TimeoutError, OSError) as error:
            last_error = "http-error" if isinstance(error, HTTPError) else "network-error"
            if attempt + 1 < RETRIES:
                time.sleep(0.5 * (attempt + 1))
    raise CollectionError(last_error)


def download_verified_crate(entry: dict[str, Any], cache_dir: Path) -> tuple[Path, str]:
    name = entry["name"]
    version = entry["version"]
    expected = entry["checksum"].lower()
    if not SAFE_CRATE_NAME.fullmatch(name) or not SAFE_CRATE_VERSION.fullmatch(version):
        raise CollectionError("crate-name-or-version-invalid")
    if not SAFE_CHECKSUM.fullmatch(expected):
        raise CollectionError("cargo-lock-checksum-invalid")

    archive_name = f"{name}-{version}-{expected[:12]}.crate"
    destination = cache_dir / archive_name
    cache_dir.mkdir(parents=True, exist_ok=True)
    if destination.is_file() and sha256_file(destination) == expected:
        return destination, "verified-cache-hit"
    if destination.exists():
        destination.unlink()

    url = crate_archive_url(name, version)
    last_error = "download-failed"
    for attempt in range(RETRIES):
        temporary_name: str | None = None
        digest = hashlib.sha256()
        try:
            request = Request(url, headers={"User-Agent": "win-search-license-collector/1.0"})
            with urlopen(request, timeout=TIMEOUT_SECONDS) as response:
                descriptor, temporary_name = tempfile.mkstemp(prefix=f".{archive_name}.", suffix=".part", dir=cache_dir)
                with os.fdopen(descriptor, "wb") as output:
                    while True:
                        chunk = response.read(CHUNK_SIZE)
                        if not chunk:
                            break
                        digest.update(chunk)
                        output.write(chunk)
                    output.flush()
                    os.fsync(output.fileno())
            if digest.hexdigest() != expected:
                last_error = "checksum-mismatch"
                if attempt + 1 < RETRIES:
                    time.sleep(0.5 * (attempt + 1))
                    continue
                break
            os.replace(temporary_name, destination)
            temporary_name = None
            return destination, "downloaded-and-verified"
        except (HTTPError, URLError, TimeoutError, OSError) as error:
            last_error = "http-error" if isinstance(error, HTTPError) else "network-error"
            if attempt + 1 < RETRIES:
                time.sleep(0.5 * (attempt + 1))
        finally:
            if temporary_name:
                Path(temporary_name).unlink(missing_ok=True)
    raise CollectionError(last_error)


def safe_archive_relative_path(raw_name: str, archive_root: str) -> PurePosixPath | None:
    if "\\" in raw_name or raw_name.startswith("/") or "\x00" in raw_name:
        return None
    parts = PurePosixPath(raw_name).parts
    if not parts or parts[0] != archive_root or len(parts) == 1:
        return None
    relative_parts = parts[1:]
    if any(part in ("", ".", "..") for part in relative_parts):
        return None
    for part in relative_parts:
        if ":" in part or part.endswith((".", " ")) or any(ord(character) < 32 for character in part):
            return None
        if part.split(".", 1)[0].upper() in WINDOWS_RESERVED:
            return None
    return PurePosixPath(*relative_parts)


def is_legal_file(relative: PurePosixPath) -> bool:
    basename = relative.name.casefold()
    if basename.startswith(LEGAL_PREFIXES):
        return True
    return any(part.casefold() in LEGAL_DIRECTORIES for part in relative.parts[:-1])


def legal_kind(relative: PurePosixPath) -> str:
    basename = relative.name.casefold()
    if basename.startswith(("license", "copying")):
        return "license"
    if basename.startswith("notice"):
        return "notice"
    return "license-subtree-file"


def safe_manifest_value(value: Any, *, max_length: int = 512) -> str | None:
    if not isinstance(value, str):
        return None
    candidate = " ".join(value.split())
    if not candidate or len(candidate) > max_length:
        return None
    if re.search(r"(?i)(?:[A-Z]:\\|\\\\|/home/|/users/|bearer\s|(?:token|password|secret)=)", candidate):
        return None
    return candidate


def safe_git_origin(source: str) -> str | None:
    candidate = source[4:] if source.startswith("git+") else source
    parsed = urlsplit(candidate)
    if parsed.scheme not in {"https", "http", "ssh", "git"} or not parsed.hostname:
        return None
    host = parsed.hostname.lower()
    if parsed.port:
        host += f":{parsed.port}"
    return f"{parsed.scheme}://{host}{parsed.path}"


def parse_git_commit(source: str) -> str | None:
    fragment = urlsplit(source[4:] if source.startswith("git+") else source).fragment
    return fragment if fragment else None


def package_metadata(manifest_bytes: bytes) -> dict[str, Any]:
    try:
        document = tomllib.loads(manifest_bytes.decode("utf-8"))
    except (UnicodeDecodeError, tomllib.TOMLDecodeError):
        raise CollectionError("crate-manifest-parse-failed") from None
    package = document.get("package")
    if not isinstance(package, dict):
        raise CollectionError("crate-manifest-package-section-missing")

    raw_authors = package.get("authors", [])
    if isinstance(raw_authors, str):
        raw_authors = [raw_authors]
    authors = []
    if isinstance(raw_authors, list):
        for author in raw_authors:
            normalized = safe_manifest_value(author)
            if normalized:
                authors.append(normalized)

    return {
        "authors": authors,
        "license": safe_manifest_value(package.get("license")),
        "license_file": safe_manifest_value(package.get("license-file"), max_length=256),
    }


def inspect_crate(entry: dict[str, Any], archive_path: Path, output_dir: Path) -> dict[str, Any]:
    name = entry["name"]
    version = entry["version"]
    archive_root = f"{name}-{version}"
    metadata: dict[str, Any] = {"authors": [], "license": None, "license_file": None}
    manifest_error: str | None = None
    collected: list[tuple[PurePosixPath, bytes]] = []
    invalid_legal_member_count = 0

    try:
        with tarfile.open(archive_path, mode="r:gz") as archive:
            members = archive.getmembers()
            manifest_member = None
            for member in members:
                relative = safe_archive_relative_path(member.name, archive_root)
                if relative is None:
                    continue
                if relative.as_posix() == "Cargo.toml" and member.isreg():
                    manifest_member = member
            if manifest_member is not None:
                manifest_stream = archive.extractfile(manifest_member)
                if manifest_stream is not None:
                    try:
                        metadata = package_metadata(manifest_stream.read())
                    except CollectionError as error:
                        manifest_error = error.reason
            else:
                manifest_error = "crate-manifest-missing"

            declared_license = metadata.get("license_file")
            declared_path = None
            if declared_license:
                candidate = PurePosixPath(declared_license)
                if (
                    not candidate.is_absolute()
                    and "\\" not in declared_license
                    and all(part not in ("", ".", "..") for part in candidate.parts)
                    and all(":" not in part for part in candidate.parts)
                ):
                    declared_path = candidate

            for member in members:
                if not member.isreg():
                    continue
                relative = safe_archive_relative_path(member.name, archive_root)
                if relative is None:
                    if PurePosixPath(member.name).name.casefold().startswith(LEGAL_PREFIXES):
                        invalid_legal_member_count += 1
                    continue
                if not is_legal_file(relative) and relative != declared_path:
                    continue
                stream = archive.extractfile(member)
                if stream is None:
                    continue
                collected.append((relative, stream.read()))
    except (tarfile.TarError, OSError, EOFError):
        raise CollectionError("crate-archive-invalid") from None

    output_files = []
    root_license_files = []
    root_notice_files = []
    nested_license_files = []
    for relative, contents in sorted(collected, key=lambda item: item[0].as_posix().casefold()):
        output_relative = PurePosixPath("crates", name, version, relative.as_posix())
        destination = output_dir.joinpath(*output_relative.parts)
        write_atomic(destination, contents)
        record = {
            "kind": legal_kind(relative),
            "package_path": relative.as_posix(),
            "output_path": output_relative.as_posix(),
            "sha256": sha256_bytes(contents),
            "size_bytes": len(contents),
        }
        output_files.append(record)
        if len(relative.parts) == 1 and relative.name.casefold().startswith(("license", "copying")) and contents.strip():
            root_license_files.append(record["output_path"])
        elif len(relative.parts) == 1 and relative.name.casefold().startswith("notice"):
            root_notice_files.append(record["output_path"])
        elif record["kind"] in {"license", "license-subtree-file"}:
            nested_license_files.append(record["output_path"])

    reasons = []
    if manifest_error:
        reasons.append(manifest_error)
    if invalid_legal_member_count:
        reasons.append("unsafe-license-path-in-crate-archive")
    if not root_license_files:
        reasons.append("no-root-license-file")
    if not output_files:
        reasons.append("no-license-or-notice-files-in-crate-archive")

    result = dict(entry)
    result.update(metadata)
    result.update(
        {
            "archive_cache_file": f"state/velopack-crates/{archive_path.name}",
            "archive_verification": "sha256-matched-to-cargo-lock",
            "legal_files": output_files,
            "root_license_files": root_license_files,
            "root_notice_files": root_notice_files,
            "nested_license_files": nested_license_files,
            "status": "resolved" if not reasons else "unresolved",
            "unresolved_reasons": reasons,
        }
    )
    return result


def deduplicate_packages(lock_document: dict[str, Any]) -> tuple[list[dict[str, Any]], list[dict[str, Any]], list[dict[str, Any]]]:
    registry: dict[tuple[str, str, str], dict[str, Any]] = {}
    git_dependencies: dict[tuple[str, str, str], dict[str, Any]] = {}
    other_registries: dict[tuple[str, str, str], dict[str, Any]] = {}
    for package in lock_document.get("package", []):
        name = package.get("name")
        version = package.get("version")
        source = package.get("source")
        if not isinstance(name, str) or not isinstance(version, str) or not isinstance(source, str):
            continue
        if source == CRATES_IO_SOURCE:
            checksum = package.get("checksum")
            key = (name, version, str(checksum or ""))
            registry[key] = {
                "name": name,
                "version": version,
                "source": CRATES_IO_SOURCE,
                "checksum": checksum,
                "source_url": crate_archive_url(name, version),
                "authors": [],
                "license": None,
                "license_file": None,
                "legal_files": [],
                "root_license_files": [],
                "root_notice_files": [],
                "nested_license_files": [],
                "unresolved_reasons": [],
            }
        elif source.startswith("git+"):
            commit = parse_git_commit(source)
            safe_origin = safe_git_origin(source)
            key = (name, version, source)
            git_dependencies[key] = {
                "name": name,
                "version": version,
                "source": safe_origin,
                "exact_commit": commit,
                "commit_is_full_hash": bool(commit and SAFE_COMMIT.fullmatch(commit)),
                "processing_status": "listed-only-not-processed",
            }
        elif source.startswith("registry+"):
            key = (name, version, source)
            other_registries[key] = {
                "name": name,
                "version": version,
                "source_kind": "non-crates-io-registry",
                "processing_status": "not-downloaded",
            }
    return (
        sorted(registry.values(), key=lambda item: (item["name"].casefold(), item["version"], item["checksum"] or "")),
        sorted(git_dependencies.values(), key=lambda item: (item["name"].casefold(), item["version"], item["source"] or "")),
        sorted(other_registries.values(), key=lambda item: (item["name"].casefold(), item["version"])),
    )


def fetch_repository_files(output_dir: Path) -> tuple[dict[str, bytes], list[dict[str, Any]]]:
    sources = {
        "Cargo.lock": f"{RAW_BASE}/Cargo.lock",
        "Cargo.toml": f"{RAW_BASE}/Cargo.toml",
        "LICENSE": f"{RAW_BASE}/LICENSE",
    }
    contents_by_name: dict[str, bytes] = {}
    records = []
    for name, url in sources.items():
        contents = download_bytes(url)
        output_path = output_dir / name
        write_atomic(output_path, contents)
        contents_by_name[name] = contents
        records.append(
            {
                "name": name,
                "url": url,
                "output_path": name,
                "sha256": sha256_bytes(contents),
                "size_bytes": len(contents),
            }
        )
    return contents_by_name, records


def ensure_owned_output(output_dir: Path) -> Path:
    output_dir.mkdir(parents=True, exist_ok=True)
    output_root = output_dir.resolve()
    crates_dir = output_dir / "crates"
    resolved_crates = crates_dir.resolve(strict=False)
    if output_root not in resolved_crates.parents:
        raise CollectionError("output-directory-containment-check-failed")
    if crates_dir.is_symlink():
        raise CollectionError("output-crates-directory-is-symlink")
    if crates_dir.exists():
        shutil.rmtree(crates_dir)
    crates_dir.mkdir(parents=True, exist_ok=True)
    return output_root


def parse_args() -> argparse.Namespace:
    repository_root = Path(__file__).resolve().parents[1]
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output-dir", type=Path, default=repository_root / "licenses" / "velopack-runtime")
    parser.add_argument("--cache-dir", type=Path, default=repository_root / "state" / "velopack-crates")
    parser.add_argument("--max-workers", type=int, default=MAX_WORKERS)
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    if args.max_workers < 1:
        print("max-workers-must-be-positive", file=sys.stderr)
        return 2
    workers = min(args.max_workers, MAX_WORKERS)
    output_dir = args.output_dir.expanduser().resolve()
    cache_dir = args.cache_dir.expanduser().resolve()
    if output_dir == cache_dir or output_dir in cache_dir.parents or cache_dir in output_dir.parents:
        print("output-and-cache-directories-must-not-overlap", file=sys.stderr)
        return 2
    try:
        ensure_owned_output(output_dir)
        repo_files, repo_file_records = fetch_repository_files(output_dir)
        lock_bytes = repo_files["Cargo.lock"]
        try:
            lock_document = tomllib.loads(lock_bytes.decode("utf-8"))
        except (UnicodeDecodeError, tomllib.TOMLDecodeError):
            raise CollectionError("cargo-lock-parse-failed") from None
        registry_packages, git_dependencies, other_registries = deduplicate_packages(lock_document)
        if not registry_packages:
            raise CollectionError("no-crates-io-registry-dependencies-found")

        results_by_key: dict[tuple[str, str, str], dict[str, Any]] = {}
        failures: list[dict[str, str]] = []
        def work(entry: dict[str, Any]) -> tuple[tuple[str, str, str], dict[str, Any]]:
            key = (entry["name"], entry["version"], str(entry.get("checksum") or ""))
            try:
                if not entry.get("checksum"):
                    raise CollectionError("cargo-lock-checksum-missing")
                archive_path, _download_status = download_verified_crate(entry, cache_dir)
                return key, inspect_crate(entry, archive_path, output_dir)
            except CollectionError as error:
                failed = dict(entry)
                failed.update(
                    {
                        "archive_cache_file": None,
                        "archive_verification": "failed-before-extraction",
                        "status": "unresolved",
                        "unresolved_reasons": [error.reason],
                    }
                )
                return key, failed

        completed = 0
        with concurrent.futures.ThreadPoolExecutor(max_workers=workers) as executor:
            futures = {executor.submit(work, entry): entry for entry in registry_packages}
            for future in concurrent.futures.as_completed(futures):
                key, result = future.result()
                results_by_key[key] = result
                completed += 1
                if completed % 50 == 0 or completed == len(registry_packages):
                    print(f"Processed {completed}/{len(registry_packages)} registry crates", flush=True)

        packages = sorted(results_by_key.values(), key=lambda item: (item["name"].casefold(), item["version"], item["checksum"] or ""))
        from resolve_velopack_licenses import complete_missing_licenses
        complete_missing_licenses(packages, output_dir, Path(__file__).resolve().parents[1], cache_dir)
        unresolved = []
        for package in packages:
            reasons = package.get("unresolved_reasons", [])
            if reasons:
                unresolved.append(
                    {
                        "name": package["name"],
                        "version": package["version"],
                        "checksum": package.get("checksum"),
                        "reasons": reasons,
                        "nested_license_files": package.get("nested_license_files", []),
                    }
                )

        provenance = {
            "repository": REPOSITORY,
            "commit": COMMIT,
            "commit_confirmation": "provided-by-release-maintainer",
            "scope": "Cargo.lock registry-dependency superset; feature and target pruning is not applied",
            "not_a_claim": "This bundle does not assert that every development or platform-specific crate is shipped in the Velopack runtime.",
            "files": repo_file_records,
        }
        index = {
            "schema_version": 1,
            "generated_at_utc": datetime.now(timezone.utc).isoformat(timespec="seconds").replace("+00:00", "Z"),
            "repository_provenance": provenance,
            "registry_dependency_count": len(packages),
            "downloaded_and_verified_archive_count": sum(p.get("archive_verification") == "sha256-matched-to-cargo-lock" for p in packages),
            "legal_file_count": sum(len(p.get("legal_files", [])) for p in packages),
            "unresolved_count": len(unresolved),
            "git_dependency_count": len(git_dependencies),
            "non_crates_io_registry_dependency_count": len(other_registries),
            "registry_dependencies": packages,
            "git_dependencies_not_processed": git_dependencies,
            "other_registry_dependencies_not_processed": other_registries,
            "unresolved": unresolved,
        }
        index_path = output_dir / "index.json"
        write_atomic(index_path, (json.dumps(index, ensure_ascii=False, indent=2) + "\n").encode("utf-8"))
        print(
            json.dumps(
                {
                    "index": "index.json",
                    "registry_dependencies": len(packages),
                    "verified_archives": index["downloaded_and_verified_archive_count"],
                    "legal_files": index["legal_file_count"],
                    "git_dependencies_not_processed": len(git_dependencies),
                    "other_registries_not_processed": len(other_registries),
                    "unresolved_count": len(unresolved),
                },
                ensure_ascii=False,
            ),
            flush=True,
        )
        return 1 if unresolved or other_registries else 0
    except CollectionError as error:
        print(error.reason, file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
