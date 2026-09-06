"""OpenMusic AI daily auto sign-in + reward claim via public HTTP API.

Pure stdlib (urllib). No third-party dependencies.

Auth (GitHub Secrets):
  * OPENMUSIC_ACCESS_TOKEN     account 1 cookie value from the browser (preferred)
  * OPENMUSIC_ACCESS_TOKEN2 … OPENMUSIC_ACCESS_TOKEN33
                               extra accounts; the workflow maps each to this env var
  * OPENMUSIC_COOKIES          optional full Cookie header (account 1)
  * OPENMUSIC_EMAIL + OPENMUSIC_PASSWORD(_MD5)  optional email-account fallback

Session refresh (the API has no refresh-token endpoint):
  * Set-Cookie renewals sent back by the backend are adopted for the rest of
    the run instead of replaying the value we started with.
  * If the cookie is dead and OPENMUSIC_EMAIL + OPENMUSIC_PASSWORD(_MD5) are
    set, autosign re-logins once to mint a new session and retries; without
    credentials it still exits 2 and asks for the secret to be updated.

Flow (reverse-engineered from the production web client, chunk 13471):
  1. Session cookie OPENMUSIC_ACCESS_TOKEN on .openmusic.ai
     (or POST {BASE}/common-api/v1/login {email, password: md5_hex} to mint it)
  2. GET  {BASE}/common-api/v1/user             -> profile + credit balances
  3. GET  {BASE}/api/activity/check-in/status?entry=refresh
     -> activity_id, today_checked_in, reward_rules
  4. POST {BASE}/api/activity/check-in/claim    {activity_id}
     -> data.alreadyCheckedIn / data.rewardGranted

CHECKIN_ENDPOINT still overrides step 4 if you need a one-off path.

Exit codes: 0 claimed/already-claimed, 1 config/runtime error,
            2 login failed, 3 claim failed.
"""

from __future__ import annotations

import argparse
import hashlib
import http.cookiejar
import json
import os
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

SUCCESS_CODE = 200
CHECKIN_STATUS_PATH = "api/activity/check-in/status?entry=refresh"
CHECKIN_CLAIM_PATH = "api/activity/check-in/claim"

# Frontend error catalogue (module 16094 in the web client).
ERROR_NAMES = {
    400002: "EMAIL_NOT_ACTIVATED",
    400004: "EMAIL_NOT_REGISTERED",
    400005: "INCORRECT_PASSWORD",
    400009: "RESET_PASSWORD_NO_PASSWORD_SET",
    400010: "REQUEST_TOO_FREQUENT",
    401001: "ACCOUNT_DISABLED",
    500000: "CREDIT_NOT_ENOUGH",
}


class ApiError(Exception):
    def __init__(self, code, message, path=""):
        super().__init__(f"API {path} -> code={code} message={message!r}")
        self.code = code
        self.message = message
        self.path = path


class ClaimNotConfigured(Exception):
    pass


def md5_hex(password: str) -> str:
    """The web client MD5-hashes the password (CryptoJS MD5, hex) before login."""
    return hashlib.md5(password.encode("utf-8")).hexdigest()


def parse_cookie_header(raw):
    """Parse a Cookie header, JSON object, or Playwright storage-state JSON."""
    raw = (raw or "").strip()
    if not raw:
        return {}
    if raw[0] in "{[":
        try:
            data = json.loads(raw)
        except ValueError:
            data = None
        if isinstance(data, dict):
            items = data.get("cookies") if isinstance(data.get("cookies"), list) else None
            if items is not None:
                return {
                    str(item.get("name")): str(item.get("value", ""))
                    for item in items
                    if isinstance(item, dict) and item.get("name")
                }
            return {str(key): str(value) for key, value in data.items() if value is not None}
        if isinstance(data, list):
            return {
                str(item.get("name")): str(item.get("value", ""))
                for item in data
                if isinstance(item, dict) and item.get("name")
            }
    cookies = {}
    skip_prefixes = (
        "path=", "domain=", "expires=", "max-age=", "secure", "httponly", "samesite=",
    )
    for part in raw.split(";"):
        part = part.strip()
        if not part or part.lower().startswith(skip_prefixes):
            continue
        if "=" not in part:
            continue
        name, value = part.split("=", 1)
        name, value = name.strip(), value.strip().strip('"')
        if name:
            cookies[name] = value
    return cookies


def session_cookies_from_env(env):
    cookies = {}
    raw = env.get("OPENMUSIC_COOKIES", "").strip()
    if raw:
        cookies.update(parse_cookie_header(raw))
    token = env.get("OPENMUSIC_ACCESS_TOKEN", "").strip()
    if token:
        if "OPENMUSIC_ACCESS_TOKEN=" in token or "OPENMUSIC_SESSION_ID=" in token:
            cookies.update(parse_cookie_header(token))
        else:
            cookies["OPENMUSIC_ACCESS_TOKEN"] = token
    session_id = env.get("OPENMUSIC_SESSION_ID", "").strip()
    if session_id:
        cookies["OPENMUSIC_SESSION_ID"] = session_id
    return {name: value for name, value in cookies.items() if name and value}


class OpenMusicClient:
    def __init__(self, base_url="https://www.openmusic.ai", timeout=30):
        self.base_url = base_url.rstrip("/")
        self.timeout = timeout
        self.jar = http.cookiejar.CookieJar()
        self.opener = urllib.request.build_opener(
            urllib.request.HTTPCookieProcessor(self.jar)
        )
        self.cookies = {}
        self.renewed = []

    @property
    def _cookie_header(self):
        return "; ".join(f"{name}={value}" for name, value in self.cookies.items())

    def apply_cookies(self, cookies):
        """Reuse a browser session instead of posting email/password."""
        cookies = {str(name): str(value) for name, value in (cookies or {}).items() if name and value}
        self.cookies.update(cookies)
        host = urllib.parse.urlparse(self.base_url).hostname or "www.openmusic.ai"
        domain = host[4:] if host.startswith("www.") else host
        if domain and not domain.startswith("."):
            domain = "." + domain
        for name, value in cookies.items():
            self.jar.set_cookie(http.cookiejar.Cookie(
                version=0,
                name=name,
                value=value,
                port=None,
                port_specified=False,
                domain=domain,
                domain_specified=True,
                domain_initial_dot=True,
                path="/",
                path_specified=True,
                secure=True,
                expires=None,
                discard=True,
                comment=None,
                comment_url=None,
                rest={"HttpOnly": None},
                rfc2109=False,
            ))

    def clear_cookies(self):
        """Drop the current session, e.g. right before a re-login refresh."""
        self.cookies.clear()
        self.jar.clear()

    def _absorb_set_cookie(self, headers):
        """Adopt session cookies the backend rotates mid-flight.

        The Cookie header is rendered from self.cookies, so without this the
        value we started with would be replayed forever and any renewal the
        server hands back in Set-Cookie would be thrown away.
        """
        get_all = getattr(headers, "get_all", None)
        if get_all is None:
            return
        for raw in get_all("Set-Cookie") or []:
            name, _, value = raw.split(";", 1)[0].strip().partition("=")
            name, value = name.strip(), value.strip().strip('"')
            if not name:
                continue
            if not value:  # server clearing the cookie
                self.cookies.pop(name, None)
                continue
            if self.cookies.get(name) != value:
                if name in self.cookies and name not in self.renewed:
                    self.renewed.append(name)
                self.cookies[name] = value

    def _request(self, method, path, body=None):
        url = self.base_url + "/" + path.lstrip("/")
        data = None
        headers = {
            "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) autosign/1.0",
            "Accept": "application/json",
            "Origin": self.base_url,
            "Referer": self.base_url + "/",
        }
        if self._cookie_header:
            headers["Cookie"] = self._cookie_header
        if body is not None:
            data = json.dumps(body).encode("utf-8")
            headers["Content-Type"] = "application/json"
        req = urllib.request.Request(url, data=data, headers=headers, method=method)
        try:
            with self.opener.open(req, timeout=self.timeout) as resp:
                self._absorb_set_cookie(resp.headers)
                raw = resp.read().decode("utf-8", errors="replace")
        except urllib.error.HTTPError as e:
            self._absorb_set_cookie(e.headers)
            raw = e.read().decode("utf-8", errors="replace") or "{}"
            try:
                env = json.loads(raw)
                raise ApiError(env.get("code", e.code), env.get("message", raw[:200]), path)
            except (ValueError, ApiError) as exc:
                if isinstance(exc, ApiError):
                    raise
                raise ApiError(e.code, f"HTTP {e.code}: {raw[:200]}", path)
        try:
            return json.loads(raw)
        except ValueError:
            raise ApiError(-1, f"non-JSON response: {raw[:200]!r}", path)

    @staticmethod
    def _check(envelope, path):
        code = envelope.get("code")
        if code != SUCCESS_CODE:
            raise ApiError(code, envelope.get("message", ""), path)
        return envelope.get("data")

    def login(self, email, password_or_md5, password_is_md5=False):
        body = {
            "email": email,
            "password": password_or_md5 if password_is_md5 else md5_hex(password_or_md5),
            "utm": None,
            "source": None,
        }
        env = self._request("POST", "common-api/v1/login", body)
        return self._check(env, "common-api/v1/login")

    def get_user(self):
        env = self._request("GET", "common-api/v1/user")
        return self._check(env, "common-api/v1/user")

    def get_activities_current(self):
        # Next.js proxy route; returns {"code":200,"data":None} when logged out.
        env = self._request("GET", "api/activities/current")
        return self._check(env, "api/activities/current")

    def get_checkin_status(self, entry="refresh"):
        path = (
            CHECKIN_STATUS_PATH
            if entry == "refresh"
            else f"api/activity/check-in/status?entry={entry}"
        )
        env = self._request("GET", path)
        return self._check(env, path)

    def get_credit_log(self, per_page=5):
        env = self._request(
            "GET",
            f"common-api/v1/user-credit-change-log?per_page={per_page}"
            "&order_by=occurred_at&sort_direction=desc",
        )
        return self._check(env, "common-api/v1/user-credit-change-log")

    def claim(self, method, path, body=None, already_claimed_codes=()):
        env = self._request(method, path, body)
        code = env.get("code")
        data = env.get("data")
        already = code in already_claimed_codes or (
            isinstance(data, dict) and bool(data.get("alreadyCheckedIn"))
        )
        if code == SUCCESS_CODE or already:
            return {"already": already, "data": data, "message": env.get("message", "")}
        raise ApiError(code, env.get("message", ""), path)


def activity_id_from_status(data):
    """activity_id is nested on the activity object when logged in."""
    if not isinstance(data, dict):
        return None
    if data.get("activity_id") is not None:
        return data["activity_id"]
    for key in ("activity", "check_in", "checkin"):
        nested = data.get(key)
        if isinstance(nested, dict) and nested.get("activity_id") is not None:
            return nested["activity_id"]
    for value in data.values():
        if isinstance(value, dict) and value.get("activity_id") is not None:
            return value["activity_id"]
    return None


def summarize_user(data):
    if not isinstance(data, dict):
        return f"user data: {json.dumps(data)[:200]}"
    user = data.get("user") or {}
    balances = data.get("user_credit_balances") or []
    total = sum(b.get("balance", 0) for b in balances if isinstance(b, dict))
    return (f"email={user.get('email')} id={user.get('id')} "
            f"credits={total} buckets={len(balances)}")


EXPIRED_HINT = ("hint: cookie/session expired — update OPENMUSIC_ACCESS_TOKEN "
                "in GitHub Secrets")
RELOGIN_HINT = ("hint: set OPENMUSIC_EMAIL + OPENMUSIC_PASSWORD (or "
                "OPENMUSIC_PASSWORD_MD5) so autosign can re-login by itself "
                "when the cookie expires")


def _login(client, email, password, password_md5):
    """Mint a fresh session cookie. Returns 0, or the exit code to fail with."""
    try:
        client.login(email, password_md5 or password,
                     password_is_md5=bool(password_md5))
    except ApiError as e:
        name = ERROR_NAMES.get(e.code, "")
        print(f"LOGIN FAILED code={e.code} {name} message={e.message!r}")
        if e.code == 400010:
            print("hint: rate limited, retry later (GitHub Actions `retry` handles this)")
        return 2
    print(f"LOGIN OK {email}")
    return 0


def _write_summary(env, account, already, before):
    summary = env.get("GITHUB_STEP_SUMMARY")
    if not summary:
        return
    with open(summary, "a", encoding="utf-8") as f:
        f.write(f"### OpenMusic autosign\n- session: {account} OK\n"
                f"- claim: {'already claimed' if already else 'claimed'}\n"
                f"- before: {before}\n")


def run(args, env=os.environ):
    email = env.get("OPENMUSIC_EMAIL", "").strip()
    password = env.get("OPENMUSIC_PASSWORD", "")
    password_md5 = env.get("OPENMUSIC_PASSWORD_MD5", "").strip()
    base = env.get("OPENMUSIC_BASE_URL", "https://www.openmusic.ai").strip()
    cookies = session_cookies_from_env(env)
    if not cookies and (not email or (not password and not password_md5)):
        print(
            "missing OPENMUSIC_ACCESS_TOKEN "
            "(copy that one cookie value from the browser). "
            "OPENMUSIC_COOKIES is optional if you paste a full Cookie header.",
            file=sys.stderr,
        )
        return 1

    client = OpenMusicClient(base_url=base)
    account = email or "cookie-session"
    # A dead cookie is only refreshable when we also hold the credentials that
    # minted it; on the email/password path we have just logged in anyway.
    can_relogin = bool(cookies and email and (password or password_md5))
    if cookies:
        client.apply_cookies(cookies)
        print("SESSION COOKIES " + ",".join(sorted(cookies)))
    else:
        rc = _login(client, email, password, password_md5)
        if rc:
            return rc

    state = {}

    def load_state():
        """Read-only state first so --probe / logs always show balances.

        Returns (expired_reason, fatal_rc). A reason means the session is dead
        and a re-login may revive it; fatal_rc is the exit code to use if not.
        """
        state.clear()
        try:
            user = client.get_user()
        except ApiError as e:
            print(f"USER FETCH FAILED {e}")
            return ("user fetch failed", 2) if cookies else (None, 1)
        state["user"] = user
        print("USER", summarize_user(user))
        user_email = (user.get("user") or {}).get("email") if isinstance(user, dict) else None
        state["email"] = user_email
        if not user_email and cookies and not (
                isinstance(user, dict) and (user.get("user") or {}).get("id")):
            print("SESSION EXPIRED: /common-api/v1/user has no logged-in profile")
            return "/common-api/v1/user has no logged-in profile", 2
        try:
            activity = client.get_activities_current()
            print("ACTIVITY", json.dumps(activity)[:1000])
        except ApiError as e:
            print(f"ACTIVITY FETCH FAILED {e}")
        try:
            state["checkin"] = client.get_checkin_status()
            print("CHECKIN_STATUS", json.dumps(state["checkin"])[:1000])
        except ApiError as e:
            print(f"CHECKIN_STATUS FAILED {e}")
        try:
            log = client.get_credit_log()
            items = log.get("list", log) if isinstance(log, dict) else log
            print("CREDIT_LOG", json.dumps(items)[:800])
        except ApiError as e:
            print(f"CREDIT_LOG FAILED {e}")
        if isinstance(state.get("checkin"), dict) and state["checkin"].get("login_required"):
            print("SESSION EXPIRED login_required=true")
            return "login_required=true", 2
        return None, 0

    expired, fatal = load_state()
    if expired and can_relogin:
        print(f"SESSION REFRESH: {expired} — re-logging in as {email}")
        client.clear_cookies()
        rc = _login(client, email, password, password_md5)
        if rc:
            return rc
        expired, fatal = load_state()
        if not expired:
            print("SESSION REFRESHED: cookie renewed by re-login, continuing")
    if expired or fatal:
        if expired and can_relogin:
            print("hint: re-login succeeded but the session still reads as logged "
                  "out; check OPENMUSIC_EMAIL / OPENMUSIC_PASSWORD")
        elif expired:
            print(EXPIRED_HINT)
            print(RELOGIN_HINT)
        return fatal or 2

    if client.renewed:
        print("SESSION COOKIE ROTATED " + ",".join(client.renewed) +
              " (server issued a fresh value; the stored secret still has the old one)")

    user = state.get("user")
    checkin = state.get("checkin")
    if state.get("email"):
        account = state["email"]

    if args.probe:
        print("PROBE MODE: state dumped, no claim attempted")
        return 0

    already = [int(x) for x in env.get("ALREADY_CLAIMED_CODES", "").split(",") if x.strip().isdigit()]
    before = summarize_user(user)

    if isinstance(checkin, dict) and checkin.get("today_checked_in"):
        print(f"ALREADY CLAIMED today_checked_in=true {before}")
        _write_summary(env, account, already=True, before=before)
        return 0

    endpoint = env.get("CHECKIN_ENDPOINT", "").strip()
    if endpoint:
        parts = endpoint.split(None, 1)
        if len(parts) != 2:
            print(f"bad CHECKIN_ENDPOINT {endpoint!r}, want 'METHOD path'", file=sys.stderr)
            return 1
        method, path = parts
        body_raw = env.get("CHECKIN_BODY_JSON", "").strip()
        try:
            body = json.loads(body_raw) if body_raw else None
        except ValueError:
            print(f"bad CHECKIN_BODY_JSON: {body_raw!r}", file=sys.stderr)
            return 1
    else:
        activity_id = activity_id_from_status(checkin)
        if activity_id is None:
            print("CLAIM FAILED: no activity_id in CHECKIN_STATUS; "
                  "is a check-in campaign running?")
            return 3
        method, path = "POST", CHECKIN_CLAIM_PATH
        body = {"activity_id": activity_id}
        print(f"CLAIM {method} {path} activity_id={activity_id}")

    try:
        # Small delay so a same-second credit-log comparison is meaningful.
        time.sleep(1)
        result = client.claim(method.upper(), path, body, tuple(already))
    except ApiError as e:
        name = ERROR_NAMES.get(e.code, "")
        print(f"CLAIM FAILED code={e.code} {name} message={e.message!r}")
        return 3
    if result["already"]:
        print(f"ALREADY CLAIMED message={result['message']!r} {before}")
    else:
        try:
            after = summarize_user(client.get_user())
        except ApiError:
            after = "unknown (re-fetch failed)"
        print(f"CLAIMED message={result['message']!r} data={json.dumps(result['data'])[:300]}")
        print(f"BEFORE {before}")
        print(f"AFTER  {after}")
    _write_summary(env, account, already=result["already"], before=before)
    return 0


def main(argv=None):
    p = argparse.ArgumentParser(description="OpenMusic AI daily auto sign-in")
    p.add_argument("--probe", action="store_true",
                   help="use session cookies + dump user/activity/credit state, skip claim")
    args = p.parse_args(argv)
    return run(args)


if __name__ == "__main__":
    sys.exit(main())
