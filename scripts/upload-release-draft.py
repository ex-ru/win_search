#!/usr/bin/env python3
"""Validate release assets and upload them to a GitHub draft release."""

from __future__ import annotations

import argparse
import hashlib
import http.client
import json
import os
import re
import sys
import tempfile
import urllib.error
import urllib.parse
import urllib.request
from dataclasses import dataclass
from pathlib import Path, PurePosixPath, PureWindowsPath
from typing import BinaryIO


API_ROOT = "https://api.github.com"
API_VERSION = "2022-11-28"
USER_AGENT = "WAI-Release-Draft-Uploader"
CHUNK_SIZE = 1024 * 1024
REQUEST_TIMEOUT_SECONDS = 1800


class UploadError(Exception):
    """A safe, user-facing validation or API error."""


@dataclass(frozen=True)
class ReleaseFile:
    name: str
    path: Path
    size: int
    sha256: str

    @property
    def github_digest(self) -> str:
        return f"sha256:{self.sha256}"


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    try:
        with path.open("rb") as stream:
            while block := stream.read(CHUNK_SIZE):
                digest.update(block)
    except OSError:
        raise UploadError(f"Cannot read release file: {path.name}") from None
    return digest.hexdigest()


def safe_name(name: str) -> None:
    if (
        not name
        or name in {".", ".."}
        or "\x00" in name
        or "/" in name
        or "\\" in name
        or PurePosixPath(name).name != name
        or PureWindowsPath(name).name != name
    ):
        raise UploadError("SHA256SUMS.txt entries must be basename-only filenames.")


def safe_file(directory: Path, name: str) -> Path:
    safe_name(name)
    path = directory / name
    try:
        if path.is_symlink() or not path.is_file():
            raise UploadError(f"Release artifact is missing or is not a regular file: {name}")
        if path.resolve(strict=True).parent != directory.resolve(strict=True):
            raise UploadError(f"Release artifact resolves outside the release directory: {name}")
    except OSError:
        raise UploadError(f"Cannot inspect release artifact: {name}") from None
    return path


def validate_release_directory(directory_arg: str, version: str) -> tuple[Path, list[ReleaseFile]]:
    try:
        directory = Path(directory_arg).resolve(strict=True)
    except OSError:
        raise UploadError("The release directory does not exist.") from None
    if not directory.is_dir():
        raise UploadError("The release directory is not a directory.")

    sums_path = safe_file(directory, "SHA256SUMS.txt")
    try:
        contents = sums_path.read_text(encoding="utf-8-sig")
    except (OSError, UnicodeError):
        raise UploadError("Cannot read SHA256SUMS.txt as UTF-8.") from None

    expected: dict[str, str] = {}
    folded_names: set[str] = set()
    for line_number, line in enumerate(contents.splitlines(), start=1):
        if not line.strip():
            continue
        match = re.fullmatch(r"([0-9a-fA-F]{64})[ \t]+(?:\*)?(.+)", line)
        if not match:
            raise UploadError(f"Malformed SHA256SUMS.txt entry on line {line_number}.")
        digest, name = match.groups()
        if name != name.strip() or name.casefold() == "sha256sums.txt":
            raise UploadError(f"Invalid SHA256SUMS.txt filename on line {line_number}.")
        safe_name(name)
        if name.casefold() in folded_names:
            raise UploadError(f"Duplicate SHA256SUMS.txt filename: {name}")
        folded_names.add(name.casefold())
        expected[name] = digest.lower()

    required = {
        "WinSearch.WAI-win-Setup.exe",
        "WinSearch.WAI-win-Portable.zip",
        f"WinSearch.WAI-{version}-full.nupkg",
        "ffmpeg-tools-win-x64.zip",
        "ffmpeg-corresponding-source.zip",
        "native-provenance.json",
        "RELEASES",
        "releases.win.json",
        "assets.win.json",
    }
    missing = sorted(required.difference(expected))
    if missing:
        raise UploadError("SHA256SUMS.txt is missing required release artifacts: " + ", ".join(missing))

    files: list[ReleaseFile] = []
    for name, expected_digest in expected.items():
        path = safe_file(directory, name)
        actual_digest = sha256_file(path)
        if actual_digest != expected_digest:
            raise UploadError(f"SHA-256 mismatch for release artifact: {name}")
        try:
            size = path.stat().st_size
        except OSError:
            raise UploadError(f"Cannot inspect release artifact: {name}") from None
        files.append(ReleaseFile(name, path, size, actual_digest))

    # The manifest cannot list its own digest without a circular definition.
    files.append(
        ReleaseFile(
            "SHA256SUMS.txt",
            sums_path,
            sums_path.stat().st_size,
            sha256_file(sums_path),
        )
    )
    return directory, files


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):  # noqa: ANN001
        return None


class GitHubClient:
    def __init__(self, repo: str, token: str) -> None:
        self.repo = repo
        self.token = token
        self.api_repo = f"{API_ROOT}/repos/{repo}"
        self.opener = urllib.request.build_opener(NoRedirect())

    def headers(self) -> dict[str, str]:
        return {
            "Authorization": f"Bearer {self.token}",
            "Accept": "application/vnd.github+json",
            "User-Agent": USER_AGENT,
            "X-GitHub-Api-Version": API_VERSION,
        }

    @staticmethod
    def next_link(headers) -> str | None:  # noqa: ANN001
        for entry in re.split(r",\s*(?=<)", headers.get("Link", "")):
            match = re.match(r"\s*<([^>]+)>\s*;\s*rel=\"next\"", entry)
            if match:
                url = urllib.parse.urlsplit(match.group(1))
                if (
                    url.scheme != "https"
                    or url.hostname != "api.github.com"
                    or url.port not in (None, 443)
                    or url.username
                    or url.password
                ):
                    raise UploadError("GitHub returned an invalid pagination link.")
                return match.group(1)
        return None

    def request_json(
        self,
        method: str,
        url: str,
        body: dict | None = None,
        *,
        allow_not_found: bool = False,
    ) -> tuple[dict | list | None, object | None]:
        data = None if body is None else json.dumps(body).encode("utf-8")
        request_headers = self.headers()
        if data is not None:
            request_headers["Content-Type"] = "application/json; charset=utf-8"
        request = urllib.request.Request(url, data=data, headers=request_headers, method=method)
        try:
            with self.opener.open(request, timeout=REQUEST_TIMEOUT_SECONDS) as response:
                raw, response_headers = response.read(), response.headers
        except urllib.error.HTTPError as error:
            if allow_not_found and error.code == 404:
                error.close()
                return None, None
            error.close()
            raise UploadError(f"GitHub API request failed (HTTP {error.code}).") from None
        except (urllib.error.URLError, TimeoutError, OSError, http.client.HTTPException) as error:
            raise UploadError(f"GitHub API request failed ({type(error).__name__}).") from None
        try:
            return json.loads(raw), response_headers
        except (UnicodeDecodeError, json.JSONDecodeError):
            raise UploadError("GitHub API returned invalid JSON.") from None

    def list_releases(self) -> list[dict]:
        url = f"{self.api_repo}/releases?per_page=100"
        result: list[dict] = []
        while url:
            page, headers = self.request_json("GET", url)
            if not isinstance(page, list):
                raise UploadError("GitHub returned an invalid releases list.")
            result.extend(page)
            url = self.next_link(headers)
        return result

    def list_assets(self, release_id: int) -> list[dict]:
        url = f"{self.api_repo}/releases/{release_id}/assets?per_page=100"
        result: list[dict] = []
        while url:
            page, headers = self.request_json("GET", url)
            if not isinstance(page, list):
                raise UploadError("GitHub returned an invalid assets list.")
            result.extend(page)
            url = self.next_link(headers)
        return result

    def existing_tag_commit(self, tag: str) -> str | None:
        encoded = urllib.parse.quote(tag, safe="")
        ref, _ = self.request_json(
            "GET", f"{self.api_repo}/git/ref/tags/{encoded}", allow_not_found=True
        )
        if ref is None:
            return None
        obj = ref.get("object") if isinstance(ref, dict) else None
        seen: set[str] = set()
        while isinstance(obj, dict) and obj.get("type") == "tag":
            tag_sha = obj.get("sha")
            if not isinstance(tag_sha, str) or tag_sha in seen:
                raise UploadError("GitHub returned an invalid annotated tag.")
            seen.add(tag_sha)
            tag_obj, _ = self.request_json("GET", f"{self.api_repo}/git/tags/{tag_sha}")
            obj = tag_obj.get("object") if isinstance(tag_obj, dict) else None
        sha = obj.get("sha") if isinstance(obj, dict) and obj.get("type") == "commit" else None
        if not isinstance(sha, str) or not re.fullmatch(r"[0-9a-fA-F]{40}", sha):
            raise UploadError("The existing release tag does not resolve to a commit.")
        return sha

    def create_or_update_draft(
        self, release: dict | None, tag: str, version: str, commit: str, notes: str
    ) -> dict:
        body = {
            "tag_name": tag,
            "target_commitish": commit,
            "name": f"WAI {version} for Windows",
            "body": notes,
            "draft": True,
            "prerelease": False,
        }
        if release is None:
            result, _ = self.request_json("POST", f"{self.api_repo}/releases", body)
        else:
            result, _ = self.request_json("PATCH", f"{self.api_repo}/releases/{release['id']}", body)
        if not isinstance(result, dict):
            raise UploadError("GitHub did not return the draft release.")
        self.validate_draft(result, tag, commit, f"WAI {version} for Windows", notes)
        return result

    @staticmethod
    def validate_draft(release: dict, tag: str, commit: str, title: str, notes: str) -> None:
        if release.get("draft") is not True or release.get("tag_name") != tag:
            raise UploadError("GitHub returned the wrong release tag or draft state.")
        target = release.get("target_commitish")
        if not isinstance(target, str) or target.lower() != commit.lower():
            raise UploadError("GitHub draft does not point at the requested tested commit.")
        if release.get("name") != title or release.get("body") != notes:
            raise UploadError("GitHub draft title or notes do not match the requested values.")
        if release.get("prerelease") is not False:
            raise UploadError("GitHub draft unexpectedly has prerelease status.")
        if not isinstance(release.get("id"), int):
            raise UploadError("GitHub returned a draft without a release ID.")

    @staticmethod
    def upload_base(release: dict, repo: str) -> str:
        template = release.get("upload_url")
        if not isinstance(template, str):
            raise UploadError("GitHub draft has no asset upload endpoint.")
        base = template.split("{", 1)[0]
        parsed = urllib.parse.urlsplit(base)
        expected_path = f"/repos/{repo}/releases/{release['id']}/assets"
        if (
            parsed.scheme != "https"
            or parsed.hostname != "uploads.github.com"
            or parsed.port not in (None, 443)
            or parsed.username
            or parsed.password
            or parsed.path != expected_path
            or parsed.query
            or parsed.fragment
        ):
            raise UploadError("GitHub draft returned an invalid upload endpoint.")
        return base

    @staticmethod
    def validate_asset(asset: dict, local: ReleaseFile) -> None:
        if asset.get("name") != local.name:
            raise UploadError(f"GitHub returned the wrong asset name for: {local.name}")
        if asset.get("state") != "uploaded" or asset.get("size") != local.size:
            raise UploadError(f"Asset is incomplete or its size differs: {local.name}")
        if asset.get("digest") != local.github_digest:
            raise UploadError(f"Asset SHA-256 differs from the local file: {local.name}")

    def upload_asset(self, upload_base: str, local: ReleaseFile) -> dict:
        url = upload_base + "?" + urllib.parse.urlencode({"name": local.name})
        request_headers = self.headers()
        request_headers["Content-Type"] = "application/octet-stream"
        request_headers["Content-Length"] = str(local.size)
        try:
            stream: BinaryIO = local.path.open("rb")
        except OSError:
            raise UploadError(f"Cannot open release artifact: {local.name}") from None
        with stream:
            try:
                digest = hashlib.sha256()
                while block := stream.read(CHUNK_SIZE):
                    digest.update(block)
                if digest.hexdigest() != local.sha256 or stream.tell() != local.size:
                    raise UploadError(f"Release artifact changed after validation: {local.name}")
                stream.seek(0)
            except OSError:
                raise UploadError(f"Cannot verify release artifact before upload: {local.name}") from None
            request = urllib.request.Request(url, data=stream, headers=request_headers, method="POST")
            try:
                with self.opener.open(request, timeout=REQUEST_TIMEOUT_SECONDS) as response:
                    raw = response.read()
            except urllib.error.HTTPError as error:
                error.close()
                raise UploadError(
                    f"Asset upload failed for {local.name} (HTTP {error.code})."
                ) from None
            except (urllib.error.URLError, TimeoutError, OSError, http.client.HTTPException) as error:
                raise UploadError(
                    f"Asset upload failed for {local.name} ({type(error).__name__})."
                ) from None
        try:
            asset = json.loads(raw)
        except (UnicodeDecodeError, json.JSONDecodeError):
            raise UploadError(f"GitHub returned an invalid upload response for: {local.name}") from None
        if not isinstance(asset, dict):
            raise UploadError(f"GitHub returned an invalid upload response for: {local.name}")
        self.validate_asset(asset, local)
        return asset


def public_draft_url(release: dict) -> str:
    value = release.get("html_url")
    parsed = urllib.parse.urlsplit(value) if isinstance(value, str) else None
    if (
        parsed is None
        or parsed.scheme != "https"
        or parsed.hostname != "github.com"
        or parsed.port not in (None, 443)
        or parsed.username
        or parsed.password
        or parsed.query
        or parsed.fragment
    ):
        raise UploadError("GitHub draft returned an invalid web URL.")
    return value


def write_result(
    directory: Path, repo: str, tag: str, commit: str, draft: dict, files: list[ReleaseFile]
) -> None:
    result_path = directory.parent / "release-draft.json"
    result = {
        "schema_version": 1,
        "repo": repo,
        "tag": tag,
        "commit": commit,
        "draft": True,
        "draft_url": public_draft_url(draft),
        "assets": [{"name": item.name, "size": item.size, "sha256": item.sha256} for item in files],
    }
    encoded = json.dumps(result, ensure_ascii=False, indent=2) + "\n"
    temporary_name: str | None = None
    try:
        with tempfile.NamedTemporaryFile(
            "w",
            encoding="utf-8",
            newline="\n",
            dir=directory.parent,
            prefix=".release-draft-",
            suffix=".tmp",
            delete=False,
        ) as temporary:
            temporary_name = temporary.name
            temporary.write(encoded)
            temporary.flush()
            os.fsync(temporary.fileno())
        os.replace(temporary_name, result_path)
    except OSError:
        if temporary_name:
            try:
                os.unlink(temporary_name)
            except OSError:
                pass
        raise UploadError("Could not save release-draft.json beside the release directory.") from None


def run(args: argparse.Namespace) -> int:
    if not re.fullmatch(r"\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?", args.version):
        raise UploadError("Version must look like 1.2.1.")
    if not re.fullmatch(r"[0-9a-fA-F]{40}", args.commit):
        raise UploadError("Commit must be a full 40-character SHA.")
    repo_parts = args.repo.split("/")
    repo_component = r"[A-Za-z0-9](?:[A-Za-z0-9_.-]*[A-Za-z0-9])?"
    if len(repo_parts) != 2 or any(not re.fullmatch(repo_component, part) for part in repo_parts):
        raise UploadError("Repository must be in owner/name form.")

    directory, files = validate_release_directory(args.directory, args.version)
    if args.validate_only:
        for item in files:
            print(f"Verified {item.name}")
        return 0

    token = os.environ.get("GITHUB_TOKEN")
    if not token:
        raise UploadError("Set GITHUB_TOKEN in the environment before uploading.")
    try:
        notes = Path(args.notes).read_text(encoding="utf-8-sig")
    except (OSError, UnicodeError):
        raise UploadError("Cannot read release notes as UTF-8.") from None
    if not notes.strip():
        raise UploadError("Release notes are empty.")

    tag = f"v{args.version}"
    client = GitHubClient(args.repo, token)
    existing_tag_commit = client.existing_tag_commit(tag)
    if existing_tag_commit and existing_tag_commit.lower() != args.commit.lower():
        raise UploadError(f"Tag {tag} points to a different commit; no release was changed.")

    releases = [item for item in client.list_releases() if item.get("tag_name") == tag]
    if len(releases) > 1:
        raise UploadError(f"Multiple GitHub releases use tag {tag}; no release was changed.")
    release = releases[0] if releases else None
    if release is not None and release.get("draft") is not True:
        raise UploadError(f"Tag {tag} is already published; no release was changed.")

    # Do not update a draft until every same-name existing asset has been checked.
    if release is not None:
        release_id = release.get("id")
        if not isinstance(release_id, int):
            raise UploadError("GitHub returned a draft without a valid release ID.")
        latest_release, _ = client.request_json("GET", f"{client.api_repo}/releases/{release_id}")
        if not isinstance(latest_release, dict):
            raise UploadError("Could not recheck the existing GitHub draft.")
        if latest_release.get("draft") is not True or latest_release.get("tag_name") != tag:
            raise UploadError(f"Tag {tag} is no longer an editable draft; no release was changed.")
        assets = client.list_assets(release_id)
        for local in files:
            matches = [asset for asset in assets if asset.get("name") == local.name]
            if len(matches) > 1:
                raise UploadError(f"GitHub draft has duplicate assets named: {local.name}")
            if matches:
                client.validate_asset(matches[0], local)

    draft = client.create_or_update_draft(release, tag, args.version, args.commit, notes)
    upload_base = client.upload_base(draft, args.repo)
    release_id = draft["id"]
    assets = client.list_assets(release_id)
    for local in files:
        matches = [asset for asset in assets if asset.get("name") == local.name]
        if len(matches) > 1:
            raise UploadError(f"GitHub draft has duplicate assets named: {local.name}")
        if matches:
            client.validate_asset(matches[0], local)
        else:
            client.upload_asset(upload_base, local)

    final_draft, _ = client.request_json("GET", f"{client.api_repo}/releases/{release_id}")
    if not isinstance(final_draft, dict):
        raise UploadError("Could not verify the final GitHub draft.")
    client.validate_draft(
        final_draft, tag, args.commit, f"WAI {args.version} for Windows", notes
    )
    final_assets = client.list_assets(release_id)
    for local in files:
        matches = [asset for asset in final_assets if asset.get("name") == local.name]
        if len(matches) != 1:
            raise UploadError(f"GitHub draft is missing or duplicates asset: {local.name}")
        client.validate_asset(matches[0], local)

    draft_url = public_draft_url(final_draft)
    write_result(directory, args.repo, tag, args.commit, final_draft, files)
    for item in files:
        print(f"Verified {item.name}")
    print(f"Draft URL: {draft_url}")
    return 0


def make_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Validate release checksums and create/update a GitHub draft release."
    )
    parser.add_argument("--version", required=True, help="Release version, for example 1.2.1")
    parser.add_argument("--directory", required=True, help="Directory containing release artifacts")
    parser.add_argument("--notes", required=True, help="UTF-8 Markdown release notes")
    parser.add_argument("--commit", required=True, help="Exact tested commit SHA (40 hex characters)")
    parser.add_argument("--repo", default="ex-ru/win_search", help="GitHub repository owner/name")
    parser.add_argument(
        "--validate-only",
        action="store_true",
        help="Validate local files and SHA256SUMS.txt without contacting GitHub",
    )
    return parser


def main() -> int:
    args = make_parser().parse_args()
    try:
        return run(args)
    except UploadError as error:
        print(f"error: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
