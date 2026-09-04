# AutoSignOpenmusic

每天自動登入 [OpenMusic AI](https://www.openmusic.ai/) 並領取簽到獎賞點數，
走官方 HTTP API（非瀏覽器模擬），跑在 GitHub Actions 排程上。

## 原理（已逆向驗證）

| 步驟 | 方法與路徑 | 狀態 |
|---|---|---|
| 登入 | `POST /common-api/v1/login`，body `{email, password: md5_hex, utm: null, source: null}` | ✅ Playwright 真站驗證（未註冊帳號回 `400004`） |
| 讀點數 | `GET /common-api/v1/user`（`user_credit_balances`） | ✅ |
| 簽到狀態 | `GET /api/activity/check-in/status?entry=refresh` | ✅（未登入回 `activity_available: true`, `login_required: true`） |
| 領獎 | `POST /api/activity/check-in/claim`，body `{activity_id}` | ✅ 來自懶載入 chunk `MobileAccountActions` / `CheckInRewardsDialog` |

登入只支援 **Email + 密碼**帳號。Google / Apple 走 OAuth，前端打的是
`auth-google/login` / `auth-apple/login`，不適合 API 自動化。

領獎回應看 `data.rewardGranted`（剛領到）或 `data.alreadyCheckedIn`（今天已領）。
狀態裡 `today_checked_in: true` 時腳本會直接當成功、不再 POST。

## 快速開始

1. 建 repo 並推上 GitHub（本目錄已 `git init` 就緒，直接 `add/commit/push`）。
2. 到 repo **Settings → Secrets and variables → Actions** 新增：
   - `OPENMUSIC_EMAIL`：登入 email
   - `OPENMUSIC_PASSWORD`：登入密碼（只存 GitHub Secrets，不進程式碼；
     腳本送出前會先做前端同款 MD5）
   - `CHECKIN_ENDPOINT`（選填）：覆寫領獎路徑，預設已是 `POST api/activity/check-in/claim`
   - `CHECKIN_BODY_JSON`（選填）：覆寫 body；預設會帶 status 回的 `activity_id`
   - `ALREADY_CLAIMED_CODES`（選填）：視為「已領過」的 code
3. 到 **Actions → OpenMusic daily autosign → Run workflow** 手動跑一次。

排程預設每天 `01:00 UTC`（台北 09:00），改 `.github/workflows/autosign.yml` 的 cron 即可。

## 桌面工具（Windows、macOS、Linux）

參考 [AutoSignOiiOii](https://github.com/huang1988pioneer/AutoSignOiiOii) 的 Avalonia 桌面工具，專案內含 **OpenMusic Flow**：

- GitHub Actions 儀表板：手動觸發每日簽到、看最近成功／失敗與連續天數
- 帳號設定：本機只存別名與 Email，不存密碼
- 本機登入與領獎：Email + 密碼走官方 API（MD5），可先測試登入再領獎

```bash
dotnet run --project OpenMusicFlow/OpenMusicFlow.csproj
```

儀表板需要 [GitHub CLI](https://cli.github.com/)（`gh auth login`），且此 repo 已 push 到 GitHub。本機領獎不需要 `gh`。

## 本機使用

```bash
python -m unittest test_autosign          # mock 單元測試（不碰真站）
OPENMUSIC_EMAIL=... OPENMUSIC_PASSWORD=... python autosign.py --probe
OPENMUSIC_EMAIL=... OPENMUSIC_PASSWORD=... python autosign.py
```

全 stdlib，無第三方依賴，Python 3.10+ 即可。

## Playwright 測真實流程

用瀏覽器跑官方前端（登入表單 → 攔截 API → 有帳號時點簽到／領獎）。
用來確認登入 MD5、簽到 status 路由，以及有帳號時的 Claim 點擊。

```bash
pip install -r requirements-dev.txt
python -m playwright install chromium

python -m unittest test_playwright_flow -v     # 無帳號：登入頁 + MD5 登入 API
python playwright_flow.py                      # 同上，JSON 輸出
OPENMUSIC_EMAIL=... OPENMUSIC_PASSWORD=... python playwright_flow.py --live
OPENMUSIC_EMAIL=... OPENMUSIC_PASSWORD=... python playwright_flow.py --live --headed
```

無帳號時會對未註冊 email 打一次登入，確認前端把密碼做成 MD5 再 POST `/common-api/v1/login`。
有帳號時會進站、點 header 的 **Earn Credits**、再點 Claim，
並把攔截到的 API 寫到 `artifacts/playwright-capture.json`。

GitHub Actions 工作流 `Playwright login flow` 可手動或每週跑；若 repo secrets 裡有帳密，live claim 測試也會一併執行。

## 風險

- 自動簽到可能違反站台 ToS，有鎖號風險，建議先用小號、確認點數有入帳再長期跑。
- 站台改版（換路徑、加驗證碼/Cloudflare）會讓腳本失效，Actions 紅了就代表要跟著改。
