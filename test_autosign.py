"""Unit tests for autosign.py (stdlib only, mocked transport)."""

import unittest

import autosign
from autosign import ApiError, OpenMusicClient, activity_id_from_status, md5_hex, run


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


def run_with(monkey_client, env, argv):
    import argparse
    orig = OpenMusicClient
    autosign.OpenMusicClient = lambda base_url="", timeout=30: monkey_client
    try:
        return run(argparse.Namespace(**argv), env=dict(env))
    finally:
        autosign.OpenMusicClient = orig


BASE_ENV = {"OPENMUSIC_EMAIL": "a@b.c", "OPENMUSIC_PASSWORD": "s3cret"}
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
