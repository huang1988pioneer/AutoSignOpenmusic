# AutoSignOpenmusic

使用 **Avalonia + Playwright .NET** 管理 [OpenMusic AI](https://www.openmusic.ai/) 登入狀態，並透過 GitHub Actions 自動簽到領取獎賞點數。
簽到與領獎**只在 GitHub Actions 執行**（官方 HTTP API，非瀏覽器模擬）。
本機桌面工具會開啟獨立瀏覽器，讓使用者自行登入；驗證取得的 Cookie 後，可直接寫入 GitHub Actions Secrets。工具不儲存密碼或 Token 到設定檔。帳號 1 用 `OPENMUSIC_ACCESS_TOKEN`，帳號 2–33 用 `OPENMUSIC_ACCESS_TOKEN2` … `OPENMUSIC_ACCESS_TOKEN33`。沒設的編號會自動略過。

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

## 快速開始（手動設定 Cookie）

也可以使用下方桌面工具，自動擷取 Cookie 並寫入 Secret。

1. 用瀏覽器登入 [openmusic.ai](https://www.openmusic.ai/)。
2. `F12` → **Application** → **Cookies** → `https://www.openmusic.ai` → 只複製 **`OPENMUSIC_ACCESS_TOKEN` 的 Value**（一個值）。
3. 到 repo **Settings → Secrets and variables → Actions** 新增 Secret（最多 33 個帳號）：

   | Secret | 貼什麼 |
   |---|---|
   | `OPENMUSIC_ACCESS_TOKEN` | 帳號 1 剛才複製的 token 值 |
   | `OPENMUSIC_ACCESS_TOKEN2` … `OPENMUSIC_ACCESS_TOKEN33` | 帳號 2–33 各自的 token 值；沒設的編號會自動略過 |

   選填（一般不用設，僅帳號 1）：`OPENMUSIC_COOKIES`、`CHECKIN_ENDPOINT`、`CHECKIN_BODY_JSON`、`ALREADY_CLAIMED_CODES`

4. 到 **Actions → OpenMusic daily autosign → Run workflow** 手動跑一次。

排程每天台北時間 **05:27、13:27、21:27**（UTC `21:27、05:27、13:27`）。同一天已領取時會回報成功，不會重複領獎。
分鐘 `27` 已於 2026-09-04 核對，不與五個參考專案當時的 cron 分鐘重複；完整對照見 [排程參考](docs/reference-schedules.md)。GitHub 實際啟動時間可能因排隊而延後。
新排程需將 `.github/workflows/autosign.yml` 推送到目標儲存庫的預設分支，並啟用 Actions 才會生效。
workflow 只會對有設 Secret 的編號開 job（帳號 1 = `OPENMUSIC_ACCESS_TOKEN`，其餘 = `OPENMUSIC_ACCESS_TOKEN{N}`），一次最多並行 5 個帳號。Cookie 過期時 Actions 會失敗，重新複製貼上對應 Secret 即可。

請勿把 token 提交到 Git。過期後再從瀏覽器複製一次、更新對應編號的 Secret 即可。

## 桌面工具（Windows、macOS、Linux）

**OpenMusic Flow** 使用 Avalonia 11.3.6、.NET 8 與 Microsoft.Playwright 1.58.0。介面參考 [Digen](https://github.com/huang1988pioneer/AutoSignDigen)、[OiiOii](https://github.com/huang1988pioneer/AutoSignOiiOii)、[MindVideo](https://github.com/huang1988pioneer/AutoSignMindVideo)、[LitVideo](https://github.com/huang1988pioneer/AutoSignLitVideo)、[Musicful](https://github.com/huang1988pioneer/AutoSignMusicful)，分為三頁：

- **簽到總覽**：手動觸發 workflow、查詢各帳號結果、連續成功日與成功／失敗時間。以台北日期統計最近 100 次執行，點數為「成功日數 × 設定點數」的估算，並非各帳號的實際餘額。
- **帳號設定**：管理、搜尋 33 個槽位的別名與 Email，並直接跳至對應帳號登入。
- **更新登入狀態**：Playwright 開啟 Chrome、Edge、Chromium 或 Firefox；登入後自動取得 `OPENMUSIC_ACCESS_TOKEN`，用這一個 Cookie 呼叫官網的 user API 驗證身分，再寫入所選 GitHub Secret。

已知帳號對應：

| 帳號 | 預設別名 | GitHub Secret |
| --- | --- | --- |
| 01 | `goldshoot0720` | `OPENMUSIC_ACCESS_TOKEN` |
| 02 | `abuhg17` | `OPENMUSIC_ACCESS_TOKEN2` |
| 03 | `huang1988pioneer` | `OPENMUSIC_ACCESS_TOKEN3` |
| 04 | `samafengtu` | `OPENMUSIC_ACCESS_TOKEN4` |
| 05 | `fengtusama` | `OPENMUSIC_ACCESS_TOKEN5` |
| 06 | `tushenbyfengbro` | `OPENMUSIC_ACCESS_TOKEN6` |
| 07 | `fengwithting0831` | `OPENMUSIC_ACCESS_TOKEN7` |
| 08 | `fengwithfeng1127` | `OPENMUSIC_ACCESS_TOKEN8` |
| 09 | `fengwithtu1127` | `OPENMUSIC_ACCESS_TOKEN9` |
| 10 | `akaonda333` | `OPENMUSIC_ACCESS_TOKEN10` |
| 11 | `engdictatorf` | `OPENMUSIC_ACCESS_TOKEN11` |
| 12 | `fengtuprinfo` | `OPENMUSIC_ACCESS_TOKEN12` |
| 13 | `fbussinesseng` | `OPENMUSIC_ACCESS_TOKEN13` |
| 14 | `flottojackpoteng` | `OPENMUSIC_ACCESS_TOKEN14` |
| 15 | `feng33feng35feng3` | `OPENMUSIC_ACCESS_TOKEN15` |
| 16 | `chbondg2` | `OPENMUSIC_ACCESS_TOKEN16` |

桌面工具會預先顯示上述別名，workflow 的 Job 名稱也會顯示對應帳號名稱。

開發執行需 .NET 8 SDK。GitHub 操作需安裝 [GitHub CLI](https://cli.github.com/)，並先執行 `gh auth login`；寫入 Secrets 的 GitHub 身分需有目標儲存庫的 Actions Secrets 寫入權限。

```bash
dotnet run --project OpenMusicFlow/OpenMusicFlow.csproj -c Release
```

1. 上方輸入自己的 `owner/repository` 或 GitHub 儲存庫網址，按「檢查連線」及「儲存設定」。從 checkout 啟動時也可按「自動偵測」；發布後的程式可使用已儲存的儲存庫設定。
2. 在「帳號設定」編輯標籤並儲存，再到「更新登入狀態」選擇帳號槽位及瀏覽器。
3. Chrome／Edge 使用電腦既有版本；Chromium／Firefox 首次使用時按「安裝瀏覽器」。不需要另裝 Node.js 或 Python 來使用桌面登入功能。
4. 核對畫面上的儲存庫與 Secret 名稱。預設勾選「登入成功後自動寫入 GitHub Secret」，同名 Secret 會被更新；取消勾選可先擷取，再手動按「寫入 GitHub Secret」。
5. 按「開始登入並擷取」，在開啟的官網視窗自行完成 Email、Google 或 Apple 登入與驗證。工具等待最多 10 分鐘，驗證成功後會關閉這次登入視窗。登入供應商若拒絕某個自動化瀏覽器，可切換瀏覽器或改用官網 Email 登入。
6. 畫面顯示登入 Email 與 Secret 寫入成功後，可到「簽到總覽」立即執行，或等候 GitHub 排程。

每次登入建立全新的瀏覽器工作階段，帳號之間不共用 Cookie，也不讀取日常瀏覽器的個人設定檔。Token 僅暫存在記憶體；切換帳號／瀏覽器、取消或關閉工具會清除。寫入 Secrets 透過 `gh secret set` 的標準輸入傳遞，不把 Token 放在命令列參數或操作紀錄。上傳失敗時可以重試；請勿在 Secret 更新成功前關閉工具。

本機設定檔位於系統 ApplicationData 下的 `OpenMusicFlow/settings.json` 與 `accounts.json`（Windows：`%APPDATA%\OpenMusicFlow`），只包含儲存庫、瀏覽器選擇、估算參數、別名與 Email。舊版帳號標籤可直接沿用。

發布 Windows x64（目標電腦需 .NET 8 Runtime）：

```powershell
dotnet publish OpenMusicFlow/OpenMusicFlow.csproj -c Release -r win-x64 --self-contained false -o artifacts/OpenMusicFlow-win-x64
```

發布時需保留整個輸出資料夾，包含 Playwright 的 `.playwright` 驅動目錄。macOS／Linux 可分別改用對應 RID；Chromium／Firefox 仍需在目標電腦安裝。

## 單元測試

`autosign.py` 只給 GitHub Actions 用。本機可跑 mock 測試（不碰真站、不登入）：

```bash
python -m unittest test_autosign
dotnet test OpenMusicFlow.Tests/OpenMusicFlow.Tests.csproj -c Release --filter "Category!=BrowserSmoke"
```

Python 簽到測試使用 stdlib，Python 3.10+ 即可。桌面測試使用假的登入與 GitHub 傳輸，以及 Avalonia Headless，驗證 33 個槽位、Secret 標準輸入、登入身分驗證、取消、儲存設定及畫面操作，不會更新真實 Secret。

## Playwright（GitHub Actions 驗證 API 形狀）

工作流 `Playwright login flow` 在 GitHub Actions 跑官方前端，用來確認登入 MD5 與簽到 status 路由。
另外提供可選的 .NET 瀏覽器冒煙測試，只開啟官網登入頁、檢查表單，不輸入帳密、不領獎、不更新 GitHub：

```powershell
$env:OPENMUSIC_BROWSER_SMOKE = '1'
dotnet test OpenMusicFlow.Tests/OpenMusicFlow.Tests.csproj -c Release --filter "Category=BrowserSmoke"
```

預設使用已安裝的 Chrome，可透過 `OPENMUSIC_SMOKE_CHANNEL=msedge` 選擇 Edge。選填 `OPENMUSIC_UI_ARTIFACTS` 可指定測試截圖輸出目錄。

## 風險

- 自動簽到可能違反站台 ToS，有鎖號風險，建議先用小號、確認點數有入帳再長期跑。
- 站台改版（換路徑、加驗證碼/Cloudflare）會讓腳本失效，Actions 紅了就代表要跟著改。
- Session cookie 會過期；過期後更新 GitHub Secrets 即可，不必改程式。
