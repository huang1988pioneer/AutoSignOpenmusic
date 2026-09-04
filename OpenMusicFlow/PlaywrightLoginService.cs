using System.Net;
using System.Text.Json;
using Microsoft.Playwright;

namespace OpenMusicFlow;

internal interface ILoginCaptureService
{
    Task<CapturedLogin> CaptureAsync(int accountNumber, string browser, IProgress<string> progress, CancellationToken cancellationToken);
    Task InstallBrowserAsync(string browser, CancellationToken cancellationToken);
}

internal sealed class CapturedLogin(int accountNumber, string token, string email)
{
    public int AccountNumber { get; } = accountNumber;
    public string AccessToken { get; } = token;
    public string Email { get; } = email;
    public DateTimeOffset CapturedAt { get; } = DateTimeOffset.Now;
    public override string ToString() => $"帳號 {AccountNumber:00}（已取得登入狀態）";
}

internal sealed class PlaywrightLoginService : ILoginCaptureService
{
    internal const string BaseUrl = "https://www.openmusic.ai";

    public async Task<CapturedLogin> CaptureAsync(int accountNumber, string browser, IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        _ = AccountProfile.SecretName(accountNumber);
        if (browser is not ("chrome" or "msedge" or "chromium" or "firefox"))
            throw new InvalidOperationException("請選擇支援的瀏覽器。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        var token = timeout.Token;
        using var playwright = await Playwright.CreateAsync();
        IBrowser? session = null;
        try
        {
            token.ThrowIfCancellationRequested();
            progress.Report("正在開啟獨立瀏覽器。請在 OpenMusic 官網完成登入與驗證。");
            var browserType = browser == "firefox" ? playwright.Firefox : playwright.Chromium;
            session = await browserType.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = false,
                Channel = browser is "chrome" or "msedge" ? browser : null,
                Timeout = 30_000,
            });
            token.ThrowIfCancellationRequested();
            await using var context = await session.NewContextAsync(new BrowserNewContextOptions
            {
                Locale = "zh-TW",
                ViewportSize = new ViewportSize { Width = 1120, Height = 800 },
                AcceptDownloads = false,
            });
            var page = await context.NewPageAsync();
            try
            {
                await page.GotoAsync(BaseUrl + "/login", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30_000 }).WaitAsync(token);
            }
            catch (TimeoutException)
            {
                progress.Report("官網載入較慢；你可以在瀏覽器重新整理並完成登入。");
            }

            using var validator = new OpenMusicTokenValidator();
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (!session.IsConnected || context.Pages.Count == 0)
                    throw new InvalidOperationException("瀏覽器已關閉，尚未取得有效登入狀態。請重新開始登入。");
                // The context is new for every account. Never read the user's normal browser profile.
                var cookies = await context.CookiesAsync([BaseUrl]).WaitAsync(token);
                var accessToken = cookies.FirstOrDefault(cookie => cookie.Name == "OPENMUSIC_ACCESS_TOKEN" &&
                    (cookie.Domain.TrimStart('.') is "openmusic.ai" or "www.openmusic.ai"))?.Value;
                if (!string.IsNullOrWhiteSpace(accessToken))
                {
                    progress.Report("已偵測登入 Cookie，正在確認 OpenMusic 帳號身分…");
                    try
                    {
                        var email = await validator.ValidateAsync(accessToken, token);
                        return new CapturedLogin(accountNumber, accessToken, email);
                    }
                    catch (InvalidOperationException)
                    {
                        progress.Report("已偵測 Cookie，但尚未通過帳號驗證。請完成登入並停留在 OpenMusic 網站。");
                    }
                    catch (HttpRequestException)
                    {
                        progress.Report("暫時無法連線驗證帳號，正在重試…");
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested)
                    {
                        progress.Report("帳號驗證連線逾時，正在重試…");
                    }
                    await Task.Delay(TimeSpan.FromSeconds(4), token);
                }
                await Task.Delay(TimeSpan.FromSeconds(1), token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("登入等待超過 10 分鐘，請重新開始。");
        }
        catch (PlaywrightException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("瀏覽器無法啟動或已關閉。Chrome／Edge 需先安裝；Chromium／Firefox 請先按「安裝瀏覽器」。若登入供應商拒絕此瀏覽器，可改用其他瀏覽器或官網 Email 登入。");
        }
        finally
        {
            if (session is not null)
                try { await session.CloseAsync(); }
                catch (PlaywrightException) { }
        }
    }

    public async Task InstallBrowserAsync(string browser, CancellationToken cancellationToken)
    {
        if (browser is not ("chromium" or "firefox"))
            throw new InvalidOperationException("Chrome／Edge 使用電腦上已安裝的版本；如需下載，請選 Chromium 或 Firefox。");
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("無法找到目前程式執行路徑。");
        var arguments = new List<string>();
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            arguments.Add(typeof(Program).Assembly.Location);
        arguments.AddRange(["--install-browser", browser]);
        await new CommandRunner().RunAsync(executable, arguments, AppContext.BaseDirectory, cancellationToken,
            timeLimit: TimeSpan.FromMinutes(10));
    }
}

internal sealed class OpenMusicTokenValidator : IDisposable
{
    private readonly HttpClient _client;

    public OpenMusicTokenValidator(HttpMessageHandler? handler = null) => _client = new HttpClient(handler ??
        new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };

    public async Task<string> ValidateAsync(string token, CancellationToken cancellationToken)
    {
        ValidateCookieValue(token);
        using var request = new HttpRequestMessage(HttpMethod.Get, PlaywrightLoginService.BaseUrl + "/common-api/v1/user");
        request.Headers.Add("Cookie", "OPENMUSIC_ACCESS_TOKEN=" + token);
        request.Headers.Add("Accept", "application/json");
        request.Headers.UserAgent.ParseAdd("OpenMusicFlow/1.0");
        request.Headers.Add("Origin", PlaywrightLoginService.BaseUrl);
        request.Headers.Referrer = new Uri(PlaywrightLoginService.BaseUrl + "/");
        using var response = await _client.SendAsync(request, cancellationToken);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new InvalidOperationException("OpenMusic 尚未確認此登入狀態。");
        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            return ReadVerifiedEmail(document.RootElement);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("OpenMusic 回傳的資料不是有效的帳號資訊。");
        }
    }

    internal static string ReadVerifiedEmail(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("code", out var code) ||
            code.ValueKind != JsonValueKind.Number || !code.TryGetInt32(out var number) || number != 200 ||
            !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("user", out var user) || user.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("OpenMusic 尚未確認此登入狀態。");
        var email = user.TryGetProperty("email", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (string.IsNullOrWhiteSpace(email)) throw new InvalidOperationException("無法確認登入帳號的 Email，請完成官網登入。");
        return email;
    }

    internal static void ValidateCookieValue(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 48_000 ||
            token.Any(character => character < 33 || character > 126 || character is ';' or ',' or '"' or '\\'))
            throw new InvalidOperationException("登入 Cookie 值無效，請重新登入。");
    }

    public void Dispose() => _client.Dispose();
}
