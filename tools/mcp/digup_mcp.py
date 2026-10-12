#!/usr/bin/env python3
"""
DigUp MCP (Model Context Protocol) Connector for AI Agents
===========================================================
Strict Read-Only access to local DigUp RAG indexes (SQLite FTS5 + Embeddings).

Supports:
  1. Standalone FastMCP server (Claude Desktop, Cursor, Antigravity, Docker Agent):
       uv run tools/mcp/digup_mcp.py
  2. Subprocess bridge calling `digup.exe mcp` directly with zero Python dependencies.
  3. Python library import:
       from tools.mcp.digup_mcp import DigUpClient
"""

import os
import sys
import json
import subprocess
from pathlib import Path
from typing import Optional, List, Dict, Any

# Locate digup.exe in standard install paths or repo root
DEFAULT_DIGUP_PATHS = [
    Path(os.path.expandvars(r"%LOCALAPPDATA%\DigUp\current\digup.exe")),
    Path(__file__).resolve().parents[2] / "run-vulkan" / "digup.exe",
    Path(__file__).resolve().parents[2] / "dist" / "staging" / "digup.exe",
]

def find_digup_exe() -> Optional[Path]:
    env_path = os.environ.get("DIGUP_EXE")
    if env_path and Path(env_path).is_file():
        return Path(env_path)
    for p in DEFAULT_DIGUP_PATHS:
        if p.is_file():
            return p
    return None


class DigUpClient:
    """Read-Only client for DigUp search and index queries."""

    def __init__(self, exe_path: Optional[str] = None, index_dir: Optional[str] = None):
        self.exe = Path(exe_path) if exe_path else find_digup_exe()
        if not self.exe or not self.exe.is_file():
            raise FileNotFoundError("digup.exe not found. Specify exe_path or install DigUp.")
        self.index_dir = index_dir or os.path.expandvars(r"%LOCALAPPDATA%\DigUp\index.noindex")

    def search(self, query: str, limit: int = 5, kind: Optional[str] = None) -> List[Dict[str, Any]]:
        """
        Executes hybrid semantic (vector) and keyword (FTS5) search strictly in Read-Only mode.
        """
        cmd = [str(self.exe), "search", query, "-n", str(limit), "--json", "--index", str(self.index_dir)]
        if kind:
            cmd.extend(["--kind", kind])

        proc = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8")
        if proc.returncode != 0:
            return []

        try:
            return json.loads(proc.stdout)
        except json.JSONDecodeError:
            return []

    def get_status(self) -> str:
        """Returns statistics of the current index."""
        cmd = [str(self.exe), "status", "--index", str(self.index_dir)]
        proc = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8")
        return proc.stdout.strip()


# ==============================================================================
# FastMCP Server (when imported or run under an MCP host like Claude or Cursor)
# ==============================================================================
try:
    from mcp.server.fastmcp import FastMCP

    mcp = FastMCP("DigUp-RAG")
    _client = DigUpClient()

    @mcp.tool()
    def search_documents(query: str, limit: int = 5, kind: Optional[str] = None) -> str:
        """
        Searches personal documents, notes, code, PDFs, and screenshots in DigUp RAG.
        Strictly Read-Only.

        :param query: Natural language search query or keywords. Prefix with 'code:' to search code index only.
        :param limit: Maximum number of results to return (default: 5, max: 20).
        :param kind: Optional category filter ('pdf', 'image', 'screenshot', 'doc', 'code', 'video', 'audio').
        """
        hits = _client.search(query, limit=limit, kind=kind)
        if not hits:
            return f"No matching documents found for: '{query}'"

        lines = [f"Found {len(hits)} matching document(s):\n"]
        for i, h in enumerate(hits, 1):
            loc = f" [loc: {h['loc']}]" if h.get("loc") is not None else ""
            lines.append(f"### [{i}] {Path(h['path']).name}{loc}")
            lines.append(f"- **Path:** `{h['path']}`")
            lines.append(f"- **Kind:** {h['kind']} | **Score:** {h.get('score', 0):.3f}")
            if h.get("excerpt"):
                lines.append(f"- **Excerpt:** > {h['excerpt'].strip()}")
            lines.append("")
        return "\n".join(lines).strip()

    @mcp.tool()
    def read_document_content(path: str, start_line: int = 1, max_lines: int = 150) -> str:
        """
        Safely reads plain text or code from a local file in strictly Read-Only mode.

        :param path: Absolute path to the file.
        :param start_line: Starting line number (1-based, default: 1).
        :param max_lines: Maximum number of lines to return (default: 150).
        """
        file_path = Path(path)
        if not file_path.is_file():
            return f"File not found: '{path}'"

        # Guardrail against sensitive system paths
        lower = str(file_path).lower()
        if "system32\\config" in lower or "sam" in lower:
            return "Access denied: Reading system security files is restricted."

        try:
            lines = []
            curr = 1
            end = start_line + max_lines - 1
            with open(file_path, "r", encoding="utf-8", errors="ignore") as f:
                for line in f:
                    if curr >= start_line and curr <= end:
                        lines.append(line)
                    if curr > end:
                        break
                    curr += 1

            res = f"### Content: {file_path.name} (lines {start_line}..{min(curr - 1, end)})\n```\n" + "".join(lines) + "\n```"
            if curr > end:
                res += f"\n*(truncated at line {end}; call again with start_line={end + 1} to continue)*"
            return res
        except Exception as e:
            return f"Error reading file: {e}"

    @mcp.tool()
    def get_index_status() -> str:
        """Returns the current status and statistics of the local DigUp RAG index."""
        return _client.get_status()

except ImportError:
    mcp = None


if __name__ == "__main__":
    if len(sys.argv) > 1 and sys.argv[1] == "search":
        client = DigUpClient()
        q = sys.argv[2] if len(sys.argv) > 2 else "test"
        res = client.search(q)
        print(json.dumps(res, indent=2, ensure_ascii=False))
    elif mcp is not None:
        mcp.run()
    else:
        # Fallback: start native digup.exe mcp process directly
        exe = find_digup_exe()
        if not exe:
            print("Error: digup.exe not found and mcp Python package not installed.", file=sys.stderr)
            sys.exit(1)
        proc = subprocess.run([str(exe), "mcp"])
        sys.exit(proc.returncode)
