"""Playwright e2e: OpenMusic login form + (optional) live claim flow."""

from __future__ import annotations

import json
import unittest

from autosign import md5_hex
from playwright_flow import (
    BASE_URL,
    FAKE_EMAIL,
    FAKE_PASSWORD,
    NetworkCapture,
    credentials_from_env,
    launch_browser,
    live_login_and_claim,
    login_page_state,
    new_page,
    submit_login,
)

from playwright.sync_api import sync_playwright


class PlaywrightLoginFlowTests(unittest.TestCase):
    """Hits the real OpenMusic site. No account required."""

    @classmethod
    def setUpClass(cls):
        cls._pw = sync_playwright().start()
        cls.browser = launch_browser(cls._pw, headed=False)

    @classmethod
    def tearDownClass(cls):
        cls.browser.close()
        cls._pw.stop()

    def setUp(self):
        self.capture = NetworkCapture()
        self.context, self.page = new_page(self.browser)
        self.capture.attach(self.page)

    def tearDown(self):
        self.context.close()

    def test_login_page_renders_email_form(self):
        state = login_page_state(self.page)
        self.assertTrue(state["has_email"], state)
        self.assertTrue(state["has_password"], state)
        self.assertTrue(state["has_submit"], state)
        self.assertTrue(state["has_google"], state)
        self.assertTrue(state["has_apple"], state)
        self.assertIn("/login", state["url"])

    def test_sign_in_posts_md5_password_to_login_api(self):
        result = submit_login(self.page, FAKE_EMAIL, FAKE_PASSWORD, capture=self.capture)
        body = result["request"]
        self.assertEqual(body.get("email"), FAKE_EMAIL)
        self.assertEqual(body.get("password"), md5_hex(FAKE_PASSWORD))
        self.assertIn("utm", body)
        self.assertIn("source", body)
        env = result["response"]
        self.assertIn("code", env)
        # 400004 EMAIL_NOT_REGISTERED is the expected probe result.
        # 400010 REQUEST_TOO_FREQUENT can appear if the login API is rate-limited.
        self.assertIn(env.get("code"), (400004, 400010), env)
        self.assertEqual(result["status"], 200)

    def test_checkin_status_route_exists(self):
        resp = self.page.request.get(
            f"{BASE_URL}/api/activity/check-in/status?entry=refresh"
        )
        self.assertEqual(resp.status, 200, resp.text()[:300])
        env = resp.json()
        self.assertEqual(env.get("code"), 200, env)
        data = env.get("data") or {}
        self.assertIn("activity_available", data)
        self.assertIn("today_checked_in", data)


@unittest.skipUnless(
    bool(credentials_from_env()),
    "set OPENMUSIC_EMAIL and OPENMUSIC_PASSWORD to run the live claim flow",
)
class PlaywrightLiveClaimTests(unittest.TestCase):
    """Real account: login, then click check-in / claim if the dialog appears."""

    @classmethod
    def setUpClass(cls):
        cls._pw = sync_playwright().start()
        cls.browser = launch_browser(cls._pw, headed=False)

    @classmethod
    def tearDownClass(cls):
        cls.browser.close()
        cls._pw.stop()

    def setUp(self):
        self.capture = NetworkCapture()
        self.context, self.page = new_page(self.browser)
        self.capture.attach(self.page)

    def tearDown(self):
        self.context.close()

    def test_live_login_and_capture_claim(self):
        email, password = credentials_from_env()
        result = live_login_and_claim(
            self.page, self.context, self.capture, email, password
        )
        self.assertTrue(result["ok"], json.dumps(result.get("login"), default=str)[:800])
        cookies = result["cookies"]
        self.assertTrue(
            any("ACCESS_TOKEN" in name or "SESSION" in name for name in cookies),
            cookies,
        )
        login_calls = [
            c for c in result["api_calls"] if "common-api/v1/login" in c["path"]
        ]
        self.assertTrue(login_calls, result["api_calls"])
        # Claim path is deployment-specific; dump captured APIs so it can be pinned.
        print("LIVE API CALLS", json.dumps(result["api_calls"], ensure_ascii=False)[:2000])
        print("CLAIM UI", result.get("claim"))


if __name__ == "__main__":
    unittest.main()
