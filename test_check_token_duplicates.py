"""Unit tests for check_token_duplicates.py (stdlib only, no real secrets)."""

import io
import os
import tempfile
import unittest

import check_token_duplicates as checker


def env_tokens(*values, prefix="TOKEN_"):
    mapping = {}
    for index, value in enumerate(values, start=1):
        if value is None:
            continue
        if prefix == "TOKEN_":
            mapping[f"TOKEN_{index}"] = value
        elif index == 1:
            mapping["OPENMUSIC_ACCESS_TOKEN"] = value
        else:
            mapping[f"OPENMUSIC_ACCESS_TOKEN{index}"] = value
    return mapping


class TestCheckTokenDuplicates(unittest.TestCase):
    def test_secret_names(self):
        self.assertEqual(checker.secret_name(1), "OPENMUSIC_ACCESS_TOKEN")
        self.assertEqual(checker.secret_name(2), "OPENMUSIC_ACCESS_TOKEN2")
        self.assertEqual(checker.secret_name(33), "OPENMUSIC_ACCESS_TOKEN33")

    def test_skips_empty_and_whitespace(self):
        tokens = checker.configured_tokens(env_tokens("alpha", "", "  ", None, "beta"))
        self.assertEqual(
            [name for name, _ in tokens],
            ["OPENMUSIC_ACCESS_TOKEN", "OPENMUSIC_ACCESS_TOKEN5"],
        )

    def test_prefers_mapped_token_env(self):
        env = {
            "TOKEN_1": "mapped",
            "OPENMUSIC_ACCESS_TOKEN": "direct",
            "OPENMUSIC_ACCESS_TOKEN2": "second",
        }
        tokens = checker.configured_tokens(env)
        self.assertEqual(tokens[0], ("OPENMUSIC_ACCESS_TOKEN", "mapped"))
        self.assertEqual(tokens[1], ("OPENMUSIC_ACCESS_TOKEN2", "second"))

    def test_falls_back_to_real_secret_names(self):
        tokens = checker.configured_tokens(env_tokens("one", "two", prefix="OPENMUSIC_"))
        self.assertEqual(
            tokens,
            [
                ("OPENMUSIC_ACCESS_TOKEN", "one"),
                ("OPENMUSIC_ACCESS_TOKEN2", "two"),
            ],
        )

    def test_unique_tokens_pass(self):
        log = io.StringIO()
        code = checker.run(env_tokens("tok-a", "tok-b", "tok-c"), log=log)
        self.assertEqual(code, 0)
        self.assertIn("Configured OPENMUSIC_ACCESS_TOKEN secret count: 3.", log.getvalue())
        self.assertIn("No duplicate configured OPENMUSIC_ACCESS_TOKEN values were detected.", log.getvalue())
        self.assertNotIn("tok-a", log.getvalue())

    def test_duplicate_tokens_fail_without_leaking_values(self):
        secret = "super-secret-token-value"
        log = io.StringIO()
        code = checker.run(env_tokens("alpha", secret, "beta", secret), log=log)
        self.assertEqual(code, 1)
        output = log.getvalue()
        self.assertIn(
            "::error::OPENMUSIC_ACCESS_TOKEN4 has the same value as OPENMUSIC_ACCESS_TOKEN2.",
            output,
        )
        self.assertIn("Found 1 duplicate OPENMUSIC_ACCESS_TOKEN secret value(s).", output)
        self.assertNotIn(secret, output)
        self.assertNotIn("alpha", output)

    def test_three_way_duplicate_reports_against_first(self):
        pairs = checker.duplicate_pairs([
            ("OPENMUSIC_ACCESS_TOKEN", "same"),
            ("OPENMUSIC_ACCESS_TOKEN2", "other"),
            ("OPENMUSIC_ACCESS_TOKEN5", "same"),
            ("OPENMUSIC_ACCESS_TOKEN9", "same"),
        ])
        self.assertEqual(
            pairs,
            [
                ("OPENMUSIC_ACCESS_TOKEN5", "OPENMUSIC_ACCESS_TOKEN"),
                ("OPENMUSIC_ACCESS_TOKEN9", "OPENMUSIC_ACCESS_TOKEN"),
            ],
        )

    def test_none_configured_fails(self):
        log = io.StringIO()
        code = checker.run({"TOKEN_1": "  ", "UNRELATED": "x"}, log=log)
        self.assertEqual(code, 1)
        self.assertIn("::error::No OPENMUSIC_ACCESS_TOKEN secrets are configured.", log.getvalue())

    def test_whitespace_normalized_counts_as_duplicate(self):
        pairs = checker.duplicate_pairs(checker.configured_tokens(env_tokens(" abc ", "abc")))
        self.assertEqual(
            pairs,
            [("OPENMUSIC_ACCESS_TOKEN2", "OPENMUSIC_ACCESS_TOKEN")],
        )

    def test_case_sensitive_values_are_unique(self):
        self.assertEqual(
            checker.duplicate_pairs(checker.configured_tokens(env_tokens("abc", "ABC"))),
            [],
        )

    def test_writes_github_step_summary(self):
        with tempfile.NamedTemporaryFile("w+", encoding="utf-8", delete=False) as handle:
            path = handle.name
        try:
            log = io.StringIO()
            code = checker.run(env_tokens("one", "one"), log=log, summary_path=path)
            self.assertEqual(code, 1)
            with open(path, encoding="utf-8") as summary:
                text = summary.read()
            self.assertIn("### Token secret duplicate check", text)
            self.assertIn("- Duplicate: OPENMUSIC_ACCESS_TOKEN2 matches OPENMUSIC_ACCESS_TOKEN.", text)
            self.assertIn("duplicate token secret values were found (1).", text)
            self.assertNotIn("one", text)
        finally:
            os.remove(path)


if __name__ == "__main__":
    unittest.main()
