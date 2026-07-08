"""
Transcript I/O: stream raw .jsonl Claude Code transcripts and yield tool_use /
tool_result blocks. No business logic — just file walking and content flattening.
"""

from __future__ import annotations

import json
from pathlib import Path
from typing import Iterable


def _iter_transcript_tool_uses(path: Path, tool_names: set[str]) -> Iterable[dict]:
    """Yield raw tool_use blocks for the given tool names from one .jsonl transcript file."""
    try:
        with open(path, encoding="utf-8", errors="replace") as f:
            for raw_line in f:
                raw_line = raw_line.strip()
                if not raw_line:
                    continue
                try:
                    line = json.loads(raw_line)
                except json.JSONDecodeError:
                    continue
                msg = line.get("message")
                content = msg.get("content") if isinstance(msg, dict) else None
                if not isinstance(content, list):
                    continue
                for block in content:
                    if isinstance(block, dict) and block.get("type") == "tool_use" and block.get("name") in tool_names:
                        yield block
    except OSError:
        return


def _tool_result_text(content) -> str:
    """Flatten a tool_result content field (string, or list of content blocks) to text."""
    if isinstance(content, str):
        return content
    if isinstance(content, list):
        parts = []
        for item in content:
            if isinstance(item, dict) and item.get("type") == "text":
                parts.append(item.get("text", "") or "")
        return "".join(parts)
    return ""


def _iter_transcript_tool_use_results(path: Path, tool_names: set[str]) -> Iterable[dict]:
    """Yield {tool_input, result_chars, num_lines, total_lines, is_error} for each tool_use
    of the given names paired with its tool_result from one .jsonl transcript file.

    num_lines/total_lines come from the sidecar `toolUseResult.file` block Claude Code
    attaches to Read results (cheap: already parsed, no extra `wc -l`); None if absent.
    """
    pending: dict[str, dict] = {}
    try:
        with open(path, encoding="utf-8", errors="replace") as f:
            for raw_line in f:
                raw_line = raw_line.strip()
                if not raw_line:
                    continue
                try:
                    line = json.loads(raw_line)
                except json.JSONDecodeError:
                    continue
                msg = line.get("message")
                content = msg.get("content") if isinstance(msg, dict) else None
                if not isinstance(content, list):
                    continue
                for block in content:
                    if not isinstance(block, dict):
                        continue
                    if block.get("type") == "tool_use" and block.get("name") in tool_names:
                        pending[block.get("id")] = block.get("input") or {}
                    elif block.get("type") == "tool_result":
                        tool_use_id = block.get("tool_use_id")
                        if tool_use_id not in pending:
                            continue
                        tool_input = pending.pop(tool_use_id)
                        result_text = _tool_result_text(block.get("content"))
                        num_lines = None
                        total_lines = None
                        tur = line.get("toolUseResult")
                        if isinstance(tur, dict):
                            file_info = tur.get("file")
                            if isinstance(file_info, dict):
                                num_lines = file_info.get("numLines")
                                total_lines = file_info.get("totalLines")
                        yield {
                            "tool_input": tool_input,
                            "result_chars": len(result_text),
                            "num_lines": num_lines,
                            "total_lines": total_lines,
                            "is_error": bool(block.get("is_error")),
                        }
    except OSError:
        return


__all__ = [
    "_iter_transcript_tool_uses",
    "_tool_result_text",
    "_iter_transcript_tool_use_results",
]
