using Microsoft.Playwright;
using Xunit;

namespace OpenMusicFlow.Tests;

public class BrowserSmokeTests
{
    [BrowserSmokeFact]
    [Trait("Category", "BrowserSmoke")]
    public async Task InstalledBrowserCanOpenTheRealLoginPageWithoutSubmittingCredentials()
    {
        using var playwright = await Playwright.CreateAsync();
        var channel = Environment.GetEnvironmentVariable("OPENMUSIC_SMOKE_CHANNEL") ?? "chrome";
        Assert.Contains(channel, new[] { "chrome", "msedge" });
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Channel = channel, Headless = true });
        await using var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync("https://www.openmusic.ai/login", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.Locator("input[name=email]").WaitForAsync();
        Assert.Equal(1, await page.Locator("input[name=password]").CountAsync());
        Assert.Equal(1, await page.Locator("form button[type=submit]").CountAsync());
        Assert.DoesNotContain(await context.CookiesAsync(["https://www.openmusic.ai"]), cookie => cookie.Name == "OPENMUSIC_ACCESS_TOKEN");
        var directory = Environment.GetEnvironmentVariable("OPENMUSIC_UI_ARTIFACTS");
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(directory, "openmusic-login-page.png") });
        }
    }
}

public sealed class BrowserSmokeFactAttribute : FactAttribute
{
    public BrowserSmokeFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("OPENMUSIC_BROWSER_SMOKE") != "1")
            Skip = "Set OPENMUSIC_BROWSER_SMOKE=1 to open the real login page without signing in.";
    }
}
