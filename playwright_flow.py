"""Playwright e2e for OpenMusic login + daily claim.

Drives the real web client (not the stdlib HTTP script) so we can:
  * confirm the login form posts MD5 passwords to /common-api/v1/login
  * after a real login, capture the check-in / claim request the UI fires

Usage:
  python playwright_flow.py              # login page + unregistered-account API
  python playwright_flow.py --live       # real login (OPENMUSIC_EMAIL / PASSWORD)
  python playwright_flow.py --live --headed
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import sys
from pathlib import Path
from urllib.parse import urlparse

from playwright.sync_api import TimeoutError as PlaywrightTimeout
from playwright.sync_api import sync_playwright

BASE_URL = os.environ.get("OPENMUSIC_BASE_URL", "https://www.openmusic.ai").rstrip("/")
ARTIFACT_DIR = Path(__file__).resolve().parent / "artifacts"

CLAIM_NAME_RE = re.compile(
    r"(claim|check[\s-]?in|earn credits|簽到|签到|領取|领取|collect|reward|bonus)",
    re.I,
)
FAKE_EMAIL = "playwright.nosuchuser@example.com"
FAKE_PASSWORD = "PlaywrightTest1!"


def md5_hex(password: str) -> str:
    return hashlib.md5(password.encode("utf-8")).hexdigest()


def _rel_path(url: str) -> str:
    parsed = urlparse(url)
    path = parsed.path or "/"
    if parsed.query:
        path += "?" + parsed.query
    return path


class NetworkCapture:
    def __init__(self):
        self.exchanges = []
        self._attached = False

    def attach(self, page):
        # Fetch bodies are often missing on request/response events; route
        # interception can still read JSON post_data.
        if self._attached:
            return
        self._attached = True

        def handle(route):
            request = route.request
            rec = {
                "method": request.method,
                "url": request.url,
                "path": _rel_path(request.url),
                "post_data": request.post_data,
            }
            try:
                response = route.fetch()
            except Exception as exc:
                rec["error"] = str(exc)
                self.exchanges.append(rec)
                route.continue_()
                return
            rec["status"] = response.status
            try:
                rec["body"] = response.text()[:4000]
            except Exception:
                rec["body"] = ""
            self.exchanges.append(rec)
            route.fulfill(response=response)

        page.route("**/common-api/**", handle)
        page.route("**/api/**", handle)

    def find(self, path_substr, method=None):
        out = []
        for rec in self.exchanges:
            if path_substr not in rec.get("path", ""):
                continue
            if method and rec.get("method") != method:
                continue
            out.append(rec)
        return out

    def dump(self, path: Path):
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(self.exchanges, indent=2, ensure_ascii=False), encoding="utf-8")


def launch_browser(playwright, headed=False):
    return playwright.chromium.launch(headless=not headed)


def new_page(browser):
    context = browser.new_context(
        locale="en-US",
        viewport={"width": 1280, "height": 800},
        user_agent=(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
            "AppleWebKit/537.36 (KHTML, like Gecko) "
            "Chrome/120.0.0.0 Safari/537.36"
        ),
    )
    page = context.new_page()
    page.set_default_timeout(20000)
    return context, page


def screenshot(page, name: str) -> Path:
    ARTIFACT_DIR.mkdir(parents=True, exist_ok=True)
    path = ARTIFACT_DIR / name
    page.screenshot(path=str(path), full_page=True)
    return path


def open_login(page):
    page.goto(f"{BASE_URL}/login", wait_until="domcontentloaded")
    page.locator('input[name="email"]').wait_for(state="visible")
    page.locator('input[name="password"]').wait_for(state="visible")
    page.locator('form button[type="submit"]').wait_for(state="visible")


def login_page_state(page) -> dict:
    open_login(page)
    return {
        "url": page.url,
        "title": page.title(),
        "has_email": page.locator('input[name="email"]').count() > 0,
        "has_password": page.locator('input[name="password"]').count() > 0,
        "has_submit": page.locator('form button[type="submit"]').count() > 0,
        "has_google": page.get_by_text("Continue with Google", exact=False).count() > 0,
        "has_apple": page.get_by_text("Continue with Apple", exact=False).count() > 0,
    }


def submit_login(page, email: str, password: str, capture=None, timeout=25000):
    capture = capture or NetworkCapture()
    capture.attach(page)
    open_login(page)
    page.locator('input[name="email"]').fill(email)
    page.locator('input[name="password"]').fill(password)
    with page.expect_response(
        lambda r: "/common-api/v1/login" in r.url and r.request.method == "POST",
        timeout=timeout,
    ) as pending:
        page.locator('form button[type="submit"]').click()
    response = pending.value
    recs = capture.find("/common-api/v1/login", method="POST")
    raw_body = (recs[-1].get("post_data") if recs else None) or response.request.post_data or "{}"
    try:
        request_json = json.loads(raw_body)
    except ValueError:
        request_json = {"_raw": raw_body}
    try:
        response_json = response.json()
    except Exception:
        response_json = {"_raw": response.text()[:1000]}
    return {
        "status": response.status,
        "request": request_json,
        "response": response_json,
        "url": page.url,
    }


def _dialog_visible(page) -> bool:
    locators = [
        page.get_by_role("dialog"),
        page.locator('[role="dialog"]'),
        page.locator("[data-radix-dialog-content]"),
    ]
    return any(loc.count() > 0 and loc.first.is_visible() for loc in locators)


def try_claim(page) -> dict:
    """Open the Earn Credits check-in dialog and click Claim if it appears."""
    page.wait_for_timeout(2500)
    clicked = []
    earn = page.get_by_role("button", name="Earn Credits")
    if earn.count() and earn.first.is_visible():
        try:
            earn.first.click(timeout=5000)
            clicked.append("Earn Credits")
            page.wait_for_timeout(2000)
        except Exception:
            pass
    buttons = page.get_by_role("button")
    n = min(buttons.count(), 40)
    for i in range(n):
        btn = buttons.nth(i)
        try:
            if not btn.is_visible():
                continue
            label = (btn.inner_text() or btn.get_attribute("aria-label") or "").strip()
        except Exception:
            continue
        if not label or not CLAIM_NAME_RE.search(label):
            continue
        if label.strip().lower() == "earn credits":
            continue
        try:
            btn.click(timeout=5000)
            clicked.append(label)
            page.wait_for_timeout(1500)
        except Exception:
            continue
    return {
        "clicked": clicked,
        "dialog_visible": _dialog_visible(page),
        "url": page.url,
    }


def session_cookies(context) -> dict:
    names = {}
    for cookie in context.cookies():
        names[cookie["name"]] = cookie.get("value", "")[:24] + ("…" if len(cookie.get("value", "")) > 24 else "")
    return names


def live_login_and_claim(page, context, capture, email, password) -> dict:
    login = submit_login(page, email, password, capture=capture)
    code = (login.get("response") or {}).get("code")
    if code != 200:
        screenshot(page, "live-login-failed.png")
        return {"ok": False, "stage": "login", "login": login, "cookies": session_cookies(context)}

    # Dashboard / generator usually loads after a successful session cookie.
    try:
        page.wait_for_function(
            "() => !location.pathname.includes('/login')",
            timeout=15000,
        )
    except PlaywrightTimeout:
        page.goto(f"{BASE_URL}/ai-music-generator", wait_until="domcontentloaded")

    page.wait_for_timeout(4000)
    screenshot(page, "after-login.png")
    claim = try_claim(page)
    screenshot(page, "after-claim-attempt.png")
    capture.dump(ARTIFACT_DIR / "playwright-capture.json")
    return {
        "ok": True,
        "stage": "claimed" if claim["clicked"] else "logged-in",
        "login": login,
        "claim": claim,
        "cookies": session_cookies(context),
        "api_calls": [
            {"method": e["method"], "path": e["path"], "status": e.get("status")}
            for e in capture.exchanges
        ],
        "claim_candidates": claim_candidates(capture.exchanges),
    }


def claim_candidates(exchanges):
    """POST/PUT/PATCH calls that are not login/read-only — likely CHECKIN_ENDPOINT."""
    skip = (
        "/common-api/v1/login",
        "/common-api/v1/user",
        "/common-api/v1/price",
        "/api/activities/current",
        "user-credit-change-log",
    )
    out = []
    for rec in exchanges:
        if rec.get("method") not in ("POST", "PUT", "PATCH"):
            continue
        path = rec.get("path") or ""
        if any(s in path for s in skip):
            continue
        out.append({
            "endpoint": f"{rec['method']} {path.split('?')[0].lstrip('/')}",
            "status": rec.get("status"),
            "post_data": rec.get("post_data"),
            "body": (rec.get("body") or "")[:300],
        })
    return out


def credentials_from_env(env=os.environ):
    email = env.get("OPENMUSIC_EMAIL", "").strip()
    password = env.get("OPENMUSIC_PASSWORD", "")
    if email and password:
        return email, password
    return None


def run_smoke(headed=False) -> dict:
    capture = NetworkCapture()
    with sync_playwright() as pw:
        browser = launch_browser(pw, headed=headed)
        try:
            context, page = new_page(browser)
            capture.attach(page)
            state = login_page_state(page)
            screenshot(page, "login-page.png")
            fake = submit_login(page, FAKE_EMAIL, FAKE_PASSWORD, capture=capture)
            screenshot(page, "login-unregistered.png")
            context.close()
        finally:
            browser.close()
    return {"login_page": state, "fake_login": fake, "api_calls": capture.exchanges}


def run_live(headed=False) -> dict:
    creds = credentials_from_env()
    if not creds:
        raise SystemExit("missing OPENMUSIC_EMAIL / OPENMUSIC_PASSWORD")
    email, password = creds
    capture = NetworkCapture()
    with sync_playwright() as pw:
        browser = launch_browser(pw, headed=headed)
        try:
            context, page = new_page(browser)
            capture.attach(page)
            result = live_login_and_claim(page, context, capture, email, password)
            context.close()
        finally:
            browser.close()
    return result


def main(argv=None):
    p = argparse.ArgumentParser(description="Playwright OpenMusic login/claim flow")
    p.add_argument("--live", action="store_true", help="log in with OPENMUSIC_EMAIL / PASSWORD")
    p.add_argument("--headed", action="store_true", help="show the browser window")
    args = p.parse_args(argv)
    if args.live:
        result = run_live(headed=args.headed)
    else:
        result = run_smoke(headed=args.headed)
    print(json.dumps(result, indent=2, ensure_ascii=False)[:8000])
    if args.live:
        candidates = result.get("claim_candidates") or []
        print("--- CHECKIN_ENDPOINT candidates ---")
        if not candidates:
            print("(none; dialog may not have opened — retry with --headed)")
        for c in candidates:
            print(f'  CHECKIN_ENDPOINT="{c["endpoint"]}"')
            if c.get("post_data"):
                print(f'  CHECKIN_BODY_JSON={c["post_data"]}')
        if not result.get("ok"):
            return 2
    return 0


if __name__ == "__main__":
    sys.exit(main())
