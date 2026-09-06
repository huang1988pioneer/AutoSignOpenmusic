"""Unit tests for autosign.py (stdlib only, mocked transport)."""

import email.message
import unittest

import autosign
from autosign import (
    ApiError,
    OpenMusicClient,
    activity_id_from_status,
    md5_hex,
    parse_cookie_header,
    run,
    session_cookies_from_env,
)


class FakeClient(OpenMusicClient):
    def __init__(self, script):
        super().__init__()
        self.script = script  # {(method, path-prefix): envelope or ApiError}
        self.calls = []

    def _request(self, method, path, body=None):
        self.calls.append((method, path, body))
        for (m, prefix), resp in self.script.items():
            if m == method and path.startswith(prefix):
                if isinstance(resp, Exception):
                    raise resp
                return resp
        raise AssertionError(f"unexpected call {method} {path}")


class ExpiringClient(FakeClient):
    """Cookie session reads as logged out until a login mints a new one."""

    def __init__(self, script):
        super().__init__(script)
        self.logged_in = False

    def _request(self, method, path, body=None):
        if path.startswith("common-api/v1/login"):
            envelope = super()._request(method, path, body)
            self.logged_in = True
            return envelope
        if not self.logged_in and path.startswith("api/activity/check-in/status"):
            self.calls.append((method, path, body))
            return {"code": 200, "message": "ok",
                    "data": {"authenticated": False, "login_required": True,
                             "activity_available": True, "today_checked_in": False}}
        return super()._request(method, path, body)


def fake_headers(set_cookie_values):
    headers = email.message.Message()
    for value in set_cookie_values:
        headers["Set-Cookie"] = value
    return headers


def run_with(monkey_client, env, argv):
    import argparse
    orig = OpenMusicClient
    autosign.OpenMusicClient = lambda base_url="", timeout=30: monkey_client
    try:
        return run(argparse.Namespace(**argv), env=dict(env))
    finally:
        autosign.OpenMusicClient = orig


BASE_ENV = {"OPENMUSIC_EMAIL": "a@b.c", "OPENMUSIC_PASSWORD": "s3cret"}
COOKIE_ENV = {
    "OPENMUSIC_COOKIES": "OPENMUSIC_ACCESS_TOKEN=tok; OPENMUSIC_SESSION_ID=sid",
}
OVERRIDE_ENV = dict(BASE_ENV, CHECKIN_ENDPOINT="POST common-api/v1/checkin")


def ok_user():
    return {"code": 200, "message": "success",
            "data": {"user": {"email": "a@b.c", "id": 1},
                     "user_credit_balances": [{"balance": 10}, {"balance": 5}]}}


def ok_checkin(today=False, activity_id=42):
    data = {
        "authenticated": True,
        "activity_available": True,
        "today_checked_in": today,
        "can_check_in": not today,
        "activity_id": activity_id,
        "activity": {"activity_id": activity_id, "reward_rules": [{"day": 1, "credits": 10}]},
    }
    return {"code": 200, "message": "ok", "data": data}


def base_script(extra=None):
    script = {
        ("POST", "common-api/v1/login"): {"code": 200, "message": "ok", "data": {}},
        ("GET", "common-api/v1/user"): ok_user(),
        ("GET", "api/activities/current"): {"code": 200, "message": "ok", "data": None},
        ("GET", "api/activity/check-in/status"): ok_checkin(),
        ("GET", "common-api/v1/user-credit-change-log"):
            {"code": 200, "message": "ok", "data": {"list": []}},
    }
    if extra:
        script.update(extra)
    return script


class TestAutosign(unittest.TestCase):
    def test_md5_vector(self):
        self.assertEqual(md5_hex("password"), "5f4dcc3b5aa765d61d8327deb882cf99")

    def test_parse_cookie_header(self):
        cookies = parse_cookie_header(
            "OPENMUSIC_ACCESS_TOKEN=abc; OPENMUSIC_SESSION_ID=xyz; Path=/"
        )
        self.assertEqual(cookies["OPENMUSIC_ACCESS_TOKEN"], "abc")
        self.assertEqual(cookies["OPENMUSIC_SESSION_ID"], "xyz")
        self.assertNotIn("Path", cookies)

    def test_session_cookies_from_token_secret(self):
        cookies = session_cookies_from_env({
            "OPENMUSIC_ACCESS_TOKEN": "jwt-token",
            "OPENMUSIC_SESSION_ID": "sid",
        })
        self.assertEqual(cookies["OPENMUSIC_ACCESS_TOKEN"], "jwt-token")
        self.assertEqual(cookies["OPENMUSIC_SESSION_ID"], "sid")

    def test_cookie_session_skips_login(self):
        c = FakeClient(base_script({
            ("POST", "api/activity/check-in/claim"):
                {"code": 200, "message": "claimed", "data": {"rewardGranted": True}},
        }))
        rc = run_with(c, COOKIE_ENV, {"probe": False})
        self.assertEqual(rc, 0)
        self.assertFalse(any(p.endswith("login") for _, p, _ in c.calls))
        self.assertTrue(any("claim" in p for _, p, _ in c.calls))
        self.assertIn("OPENMUSIC_ACCESS_TOKEN=tok", c._cookie_header)

    def test_set_cookie_renewal_replaces_stale_value(self):
        c = OpenMusicClient()
        c.apply_cookies({"OPENMUSIC_ACCESS_TOKEN": "old", "OPENMUSIC_SESSION_ID": "sid"})
        c._absorb_set_cookie(fake_headers([
            "OPENMUSIC_ACCESS_TOKEN=new; Path=/; HttpOnly; Secure",
        ]))
        self.assertEqual(c.cookies["OPENMUSIC_ACCESS_TOKEN"], "new")
        self.assertEqual(c.cookies["OPENMUSIC_SESSION_ID"], "sid")
        self.assertEqual(c.renewed, ["OPENMUSIC_ACCESS_TOKEN"])
        self.assertIn("OPENMUSIC_ACCESS_TOKEN=new", c._cookie_header)
        self.assertNotIn("=old", c._cookie_header)

    def test_set_cookie_clear_drops_cookie(self):
        c = OpenMusicClient()
        c.apply_cookies({"OPENMUSIC_ACCESS_TOKEN": "old"})
        c._absorb_set_cookie(fake_headers(["OPENMUSIC_ACCESS_TOKEN=; Max-Age=0"]))
        self.assertNotIn("OPENMUSIC_ACCESS_TOKEN", c.cookies)
        self.assertEqual(c._cookie_header, "")

    def test_clear_cookies_empties_session(self):
        c = OpenMusicClient()
        c.apply_cookies({"OPENMUSIC_ACCESS_TOKEN": "old"})
        c.clear_cookies()
        self.assertEqual(c.cookies, {})
        self.assertEqual(c._cookie_header, "")
        self.assertEqual(list(c.jar), [])

    def test_expired_cookie_relogins_and_claims(self):
        env = dict(COOKIE_ENV, **BASE_ENV)
        c = ExpiringClient(base_script({
            ("POST", "api/activity/check-in/claim"):
                {"code": 200, "message": "claimed", "data": {"rewardGranted": True}},
        }))
        self.assertEqual(run_with(c, env, {"probe": False}), 0)
        self.assertTrue(any(p.endswith("common-api/v1/login") for _, p, _ in c.calls))
        self.assertTrue(any("claim" in p for _, p, _ in c.calls))

    def test_expired_cookie_without_credentials_still_exit_2(self):
        c = ExpiringClient(base_script())
        self.assertEqual(run_with(c, COOKIE_ENV, {"probe": False}), 2)
        self.assertFalse(any(p.endswith("common-api/v1/login") for _, p, _ in c.calls))

    def test_relogin_failure_exit_2(self):
        env = dict(COOKIE_ENV, **BASE_ENV)
        c = ExpiringClient(base_script({
            ("POST", "common-api/v1/login"): ApiError(400005, "wrong password", "login"),
        }))
        self.assertEqual(run_with(c, env, {"probe": False}), 2)
        self.assertFalse(any("claim" in p for _, p, _ in c.calls))

    def test_expired_cookie_login_required_exit_2(self):
        c = FakeClient(base_script({
            ("GET", "api/activity/check-in/status"):
                {"code": 200, "message": "ok",
                 "data": {"authenticated": False, "login_required": True,
                          "activity_available": True, "today_checked_in": False}},
        }))
        self.assertEqual(run_with(c, COOKIE_ENV, {"probe": False}), 2)
        self.assertFalse(any("claim" in p for _, p, _ in c.calls))

    def test_login_body_uses_md5(self):
        c = FakeClient({("POST", "common-api/v1/login"):
                        {"code": 200, "message": "ok", "data": {}}})
        c.login("a@b.c", "s3cret")
        _, _, body = c.calls[0]
        self.assertEqual(body["password"], md5_hex("s3cret"))
        self.assertEqual(body["email"], "a@b.c")

    def test_activity_id_from_nested_status(self):
        self.assertEqual(activity_id_from_status({"activity_id": 7}), 7)
        self.assertEqual(
            activity_id_from_status({"activity": {"activity_id": 9}}), 9
        )
        self.assertIsNone(activity_id_from_status({"authenticated": False}))

    def test_full_claim_flow(self):
        c = FakeClient(base_script({
            ("POST", "api/activity/check-in/claim"):
                {"code": 200, "message": "claimed", "data": {"rewardGranted": True}},
        }))
        rc = run_with(c, BASE_ENV, {"probe": False})
        self.assertEqual(rc, 0)
        claim = [call for call in c.calls if call[1].startswith("api/activity/check-in/claim")]
        self.assertEqual(claim[0][2], {"activity_id": 42})

    def test_override_endpoint_still_works(self):
        c = FakeClient(base_script({
            ("POST", "common-api/v1/checkin"):
                {"code": 200, "message": "claimed", "data": {"day": 3}},
        }))
        rc = run_with(c, OVERRIDE_ENV, {"probe": False})
        self.assertEqual(rc, 0)
        self.assertTrue(any(p == "common-api/v1/checkin" for _, p, _ in c.calls))

    def test_already_claimed_counts_as_success(self):
        env = dict(OVERRIDE_ENV, ALREADY_CLAIMED_CODES="400099")
        c = FakeClient(base_script({
            ("POST", "common-api/v1/checkin"):
                {"code": 400099, "message": "already claimed today", "data": {}},
        }))
        self.assertEqual(run_with(c, env, {"probe": False}), 0)

    def test_already_checked_in_status_skips_claim(self):
        c = FakeClient(base_script({
            ("GET", "api/activity/check-in/status"): ok_checkin(today=True),
        }))
        self.assertEqual(run_with(c, BASE_ENV, {"probe": False}), 0)
        self.assertFalse(any("claim" in p for _, p, _ in c.calls))

    def test_claim_already_checked_in_flag(self):
        c = FakeClient(base_script({
            ("POST", "api/activity/check-in/claim"):
                {"code": 200, "message": "ok",
                 "data": {"alreadyCheckedIn": True, "rewardGranted": False}},
        }))
        self.assertEqual(run_with(c, BASE_ENV, {"probe": False}), 0)

    def test_wrong_password_exit_2(self):
        c = FakeClient({("POST", "common-api/v1/login"):
                        ApiError(400005, "wrong password", "login")})
        self.assertEqual(run_with(c, BASE_ENV, {"probe": False}), 2)

    def test_probe_skips_claim(self):
        c = FakeClient(base_script())
        self.assertEqual(run_with(c, BASE_ENV, {"probe": True}), 0)
        self.assertFalse(any("claim" in p for _, p, _ in c.calls))

    def test_missing_activity_id_exit_3(self):
        c = FakeClient(base_script({
            ("GET", "api/activity/check-in/status"):
                {"code": 200, "message": "ok",
                 "data": {"authenticated": True, "activity_available": False,
                          "today_checked_in": False}},
        }))
        self.assertEqual(run_with(c, BASE_ENV, {"probe": False}), 3)

    def test_missing_credentials_exit_1(self):
        c = FakeClient({})
        self.assertEqual(run_with(c, {}, {"probe": False}), 1)


if __name__ == "__main__":
    unittest.main()
