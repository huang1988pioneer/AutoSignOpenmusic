# 排程與桌面介面參考

核對日期：2026-09-04。以五個儲存庫當時的 workflow 原始碼為準，包含稽核排程；不以可能過期的 README 時間說明為準。

| 專案 | 簽到 cron 分鐘 | 其他已排程 workflow 分鐘 | 參考來源 |
| --- | --- | --- | --- |
| AutoSignDigen | 0、5 | 35 | [簽到](https://github.com/huang1988pioneer/AutoSignDigen/blob/main/.github/workflows/digen-daily-reward.yml)、[Token 稽核](https://github.com/huang1988pioneer/AutoSignDigen/blob/main/.github/workflows/check-token-secret-duplicates.yml) |
| AutoSignOiiOii | 0、18、19 | 無 | [簽到](https://github.com/huang1988pioneer/AutoSignOiiOii/blob/main/.github/workflows/claim-oiioii-lunch.yml) |
| AutoSignMindVideo | 9 | 無 | [簽到](https://github.com/huang1988pioneer/AutoSignMindVideo/blob/main/.github/workflows/mindvideo-daily-checkin.yml) |
| AutoSignLitVideo | 0 | 50 | [簽到](https://github.com/huang1988pioneer/AutoSignLitVideo/blob/main/.github/workflows/daily-checkin.yml)、[Secrets 檢查](https://github.com/huang1988pioneer/AutoSignLitVideo/blob/main/.github/workflows/check-secrets.yml) |
| AutoSignMusicful | 0 | 30 | [簽到](https://github.com/huang1988pioneer/AutoSignMusicful/blob/main/.github/workflows/musicful-auto-sign.yml)、[Secrets 檢查](https://github.com/huang1988pioneer/AutoSignMusicful/blob/main/.github/workflows/check-musicful-secrets.yml) |

已使用分鐘聯集：`0, 5, 9, 18, 19, 30, 35, 50`。OpenMusic 選擇 **27**，沿用多個參考專案共同的台北 **05、13、21 點**時段：

- 每日簽到：`27 21,5,13 * * *`（UTC），即台北每日 **05:27、13:27、21:27**。
- Token 重複稽核：`27 20 * * *`（UTC），即台北每日 **04:27**。與 Digen `20:35`、Musicful `20:30`、LitVideo `20:50` 錯開。
- 每週 Playwright API 形狀檢查：`27 2 * * 1`（UTC），即台北週一 **10:27**。

這是 cron 分鐘的錯開；其他專案若有執行中的隨機延遲，或 GitHub Actions 排隊，不代表實際執行時間永遠不重疊。

介面沿用三頁側邊導覽、帳號槽位與 GitHub Actions 指標卡。登入以 Playwright .NET 開啟獨立瀏覽器，從 [OpenMusic 登入頁](https://www.openmusic.ai/login) 取得該站 `OPENMUSIC_ACCESS_TOKEN`。GitHub 上傳採用 MindVideo 參考實作的 `gh secret set` 標準輸入方式；每次寫入使用畫面上的明確儲存庫及槽位。
