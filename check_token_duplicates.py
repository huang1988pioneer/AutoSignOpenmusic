"""Fail if two configured OPENMUSIC_ACCESS_TOKEN secrets share a value.

Reads TOKEN_1 … TOKEN_33 (workflow mapping) or the real secret names.
Never prints token values — only secret names, counts, and pass/fail.
"""

from __future__ import annotations

import hashlib
import os
import sys

MAX_ACCOUNTS = 33


def secret_name(account: int) -> str:
    if account == 1:
        return "OPENMUSIC_ACCESS_TOKEN"
    return f"OPENMUSIC_ACCESS_TOKEN{account}"


def token_value(env: dict, account: int) -> str:
    mapped = (env.get(f"TOKEN_{account}") or "").strip()
    if mapped:
        return mapped
    return (env.get(secret_name(account)) or "").strip()


def configured_tokens(env: dict, max_accounts: int = MAX_ACCOUNTS) -> list[tuple[str, str]]:
    found = []
    for account in range(1, max_accounts + 1):
        value = token_value(env, account)
        if value:
            found.append((secret_name(account), value))
    return found


def duplicate_pairs(tokens: list[tuple[str, str]]) -> list[tuple[str, str]]:
    """Return (duplicate_name, first_name) for each later secret with the same value."""
    first_by_digest: dict[str, str] = {}
    pairs = []
    for name, value in tokens:
        digest = hashlib.sha256(value.encode("utf-8")).hexdigest()
        first = first_by_digest.get(digest)
        if first is None:
            first_by_digest[digest] = name
        else:
            pairs.append((name, first))
    return pairs


def format_report(configured_count: int, pairs: list[tuple[str, str]]) -> tuple[str, int]:
    lines = ["### Token secret duplicate check", ""]
    lines.append(f"- Configured token secrets: {configured_count}")

    if configured_count == 0:
        lines.append("- Result: failed because no OPENMUSIC_ACCESS_TOKEN secrets are configured.")
        return "\n".join(lines) + "\n", 1

    for name, first in pairs:
        lines.append(f"- Duplicate: {name} matches {first}.")

    if pairs:
        lines.append(
            f"- Result: failed because duplicate token secret values were found ({len(pairs)})."
        )
        return "\n".join(lines) + "\n", 1

    lines.append("- Result: no duplicate configured OPENMUSIC_ACCESS_TOKEN values were detected.")
    return "\n".join(lines) + "\n", 0


def run(env: dict | None = None, summary_path: str | None = None, log=None) -> int:
    env = os.environ if env is None else env
    log = sys.stdout if log is None else log
    tokens = configured_tokens(env)
    pairs = duplicate_pairs(tokens)
    report, code = format_report(len(tokens), pairs)

    if summary_path:
        with open(summary_path, "a", encoding="utf-8") as handle:
            handle.write(report)

    log.write("Configured OPENMUSIC_ACCESS_TOKEN secret count: "
              f"{len(tokens)}.\n")
    if len(tokens) == 0:
        log.write("::error::No OPENMUSIC_ACCESS_TOKEN secrets are configured.\n")
    for name, first in pairs:
        log.write(f"::error::{name} has the same value as {first}.\n")
    if pairs:
        log.write(
            f"::error::Found {len(pairs)} duplicate OPENMUSIC_ACCESS_TOKEN secret value(s).\n"
        )
    elif len(tokens) > 0:
        log.write("No duplicate configured OPENMUSIC_ACCESS_TOKEN values were detected.\n")
    return code


def main(argv: list[str] | None = None) -> int:
    del argv
    return run(summary_path=os.environ.get("GITHUB_STEP_SUMMARY") or None)


if __name__ == "__main__":
    sys.exit(main())
