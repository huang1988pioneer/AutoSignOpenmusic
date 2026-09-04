# AutoSignOpenmusic

每天自動登入 [OpenMusic AI](https://www.openmusic.ai/) 並領取簽到獎賞點數。
簽到與領獎**只在 GitHub Actions 執行**（官方 HTTP API，非瀏覽器模擬）。
本機不登入、不存密碼；GitHub Secrets 只放 cookie 值：帳號 1 用 `OPENMUSIC_ACCESS_TOKEN`，帳號 2–33 用 `OPENMUSIC_ACCESS_TOKEN2` … `OPENMUSIC_ACCESS_TOKEN33`。沒設的編號會自動略過。

## 原理（已逆向驗證）

| 步驟 | 方法與路徑 | 狀態 |
|---|---|---|
| 身分 | 帶 `OPENMUSIC_ACCESS_TOKEN` cookie（瀏覽器登入後複製這一個值） | ✅ 與官網相同 session |
| 讀點數 | `GET /common-api/v1/user`（`user_credit_balances`） | ✅ |
| 簽到狀態 | `GET /api/activity/check-in/status?entry=refresh` | ✅（未登入回 `activity_available: true`, `login_required: true`） |
| 領獎 | `POST /api/activity/check-in/claim`，body `{activity_id}` | ✅ 來自懶載入 chunk `MobileAccountActions` / `CheckInRewardsDialog` |

Google / Apple 帳號也可以：在瀏覽器登入後複製 cookie 即可，不必 Email + 密碼。

領獎回應看 `data.rewardGranted`（剛領到）或 `data.alreadyCheckedIn`（今天已領）。
狀態裡 `today_checked_in: true` 時腳本會直接當成功、不再 POST。

## 快速開始

1. 用瀏覽器登入 [openmusic.ai](https://www.openmusic.ai/)。
2. `F12` → **Application** → **Cookies** → `https://www.openmusic.ai` → 只複製 **`OPENMUSIC_ACCESS_TOKEN` 的 Value**（一個值）。
3. 到 repo **Settings → Secrets and variables → Actions** 新增 Secret（最多 33 個帳號）：

   | Secret | 貼什麼 |
   |---|---|
   | `OPENMUSIC_ACCESS_TOKEN` | 帳號 1 剛才複製的 token 值 |
   | `OPENMUSIC_ACCESS_TOKEN2` … `OPENMUSIC_ACCESS_TOKEN33` | 帳號 2–33 各自的 token 值；沒設的編號會自動略過 |

   選填（一般不用設，僅帳號 1）：`OPENMUSIC_COOKIES`、`CHECKIN_ENDPOINT`、`CHECKIN_BODY_JSON`、`ALREADY_CLAIMED_CODES`

4. 到 **Actions → OpenMusic daily autosign → Run workflow** 手動跑一次。

排程預設每天 `01:00 UTC`（台北 09:00），改 `.github/workflows/autosign.yml` 的 cron 即可。
workflow 只會對有設 Secret 的編號開 job（帳號 1 = `OPENMUSIC_ACCESS_TOKEN`，其餘 = `OPENMUSIC_ACCESS_TOKEN{N}`），一次最多並行 5 個帳號。Cookie 過期時 Actions 會失敗，重新複製貼上對應 Secret 即可。

請勿把 token 提交到 Git。過期後再從瀏覽器複製一次、更新對應編號的 Secret 即可。

## 桌面工具（Windows、macOS、Linux）

參考 [AutoSignOiiOii](https://github.com/huang1988pioneer/AutoSignOiiOii) 的 Avalonia 桌面工具，專案內含 **OpenMusic Flow**：

- GitHub Actions 儀表板：手動觸發每日簽到、看最近成功／失敗與連續天數
- 帳號設定：本機只存別名與 Email 標籤；複製 `OPENMUSIC_ACCESS_TOKEN` … `OPENMUSIC_ACCESS_TOKEN33`，把瀏覽器 cookie 貼到 GitHub

```bash
dotnet run --project OpenMusicFlow/OpenMusicFlow.csproj
```

儀表板需要 [GitHub CLI](https://cli.github.com/)（`gh auth login`），且此 repo 已 push 到 GitHub。

## 單元測試

`autosign.py` 只給 GitHub Actions 用。本機可跑 mock 測試（不碰真站、不登入）：

```bash
python -m unittest test_autosign
```

全 stdlib，無第三方依賴，Python 3.10+ 即可。

## Playwright（GitHub Actions 驗證 API 形狀）

工作流 `Playwright login flow` 在 GitHub Actions 跑官方前端，用來確認登入 MD5 與簽到 status 路由。
本機不需要帶帳密或 cookie 跑 Playwright。

## 風險

- 自動簽到可能違反站台 ToS，有鎖號風險，建議先用小號、確認點數有入帳再長期跑。
- 站台改版（換路徑、加驗證碼/Cloudflare）會讓腳本失效，Actions 紅了就代表要跟著改。
- Session cookie 會過期；過期後更新 GitHub Secrets 即可，不必改程式。
