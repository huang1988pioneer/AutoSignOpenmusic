using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(OpenMusicFlow.Tests.TestAppBuilder))]

namespace OpenMusicFlow.Tests;

public class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia().WithInterFont().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public class WindowTests
{
    [AvaloniaFact]
    public async Task LoginUploadsToSelectedSlotAndClearsWhenAccountChanges()
    {
        using var fixture = new WindowFixture();
        var window = fixture.Window;
        fixture.Get<TextBox>("RepositoryTextBox").Text = "demo/music";
        fixture.Click("LoginNavButton");
        fixture.Get<ComboBox>("AccountComboBox").SelectedIndex = 32;
        Assert.Equal("OPENMUSIC_ACCESS_TOKEN33", fixture.Get<TextBlock>("SecretNameText").Text);
        fixture.Click("StartLoginButton");
        await window.CurrentOperation;
        Assert.Equal(("demo/music", 33, "test-only-cookie"), fixture.GitHub.Upload);
        Assert.Equal("帳號 33 · test33@example.test", fixture.Get<TextBlock>("CapturedAccountText").Text);
        Assert.DoesNotContain("test-only-cookie", fixture.Get<TextBlock>("LoginStatusText").Text);
        Assert.True(fixture.Get<Button>("CopyTokenButton").IsEnabled);
        fixture.Get<ComboBox>("AccountComboBox").SelectedIndex = 0;
        Assert.False(fixture.Get<Button>("CopyTokenButton").IsEnabled);
        Assert.False(fixture.Get<Button>("UploadSecretButton").IsEnabled);
    }

    [AvaloniaFact]
    public async Task CancellingLoginUnlocksControlsWithoutUploading()
    {
        using var fixture = new WindowFixture();
        fixture.Login.WaitForCancellation = true;
        fixture.Get<TextBox>("RepositoryTextBox").Text = "demo/music";
        fixture.Click("StartLoginButton");
        Assert.False(fixture.Get<ComboBox>("AccountComboBox").IsEnabled);
        Assert.False(fixture.Get<TextBox>("RepositoryTextBox").IsEnabled);
        fixture.Click("CancelOperationButton");
        await fixture.Window.CurrentOperation.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Null(fixture.GitHub.Upload);
        Assert.True(fixture.Get<ComboBox>("AccountComboBox").IsEnabled);
        Assert.False(fixture.Get<Button>("UploadSecretButton").IsEnabled);
    }

    [AvaloniaFact]
    public async Task UploadFailureKeepsCapturedLoginAvailableForRetry()
    {
        using var fixture = new WindowFixture();
        fixture.GitHub.FailUpload = true;
        fixture.Get<TextBox>("RepositoryTextBox").Text = "demo/music";
        fixture.Click("StartLoginButton");
        await fixture.Window.CurrentOperation;
        Assert.Contains("測試：寫入失敗", fixture.Get<TextBlock>("LoginStatusText").Text);
        Assert.True(fixture.Get<Button>("UploadSecretButton").IsEnabled);
        fixture.GitHub.FailUpload = false;
        fixture.Click("UploadSecretButton");
        await fixture.Window.CurrentOperation;
        Assert.Contains("已寫入 demo/music", fixture.Get<TextBlock>("LoginStatusText").Text);
    }

    [AvaloniaFact]
    public async Task CaptureOnlyDoesNotRequireRepositoryOrUpload()
    {
        using var fixture = new WindowFixture();
        fixture.Get<CheckBox>("AutoUploadCheckBox").IsChecked = false;
        fixture.Click("StartLoginButton");
        await fixture.Window.CurrentOperation;
        Assert.Null(fixture.GitHub.Upload);
        Assert.Equal(0, fixture.GitHub.ConnectionChecks);
        Assert.True(fixture.Get<Button>("CopyTokenButton").IsEnabled);
    }

    [AvaloniaFact]
    public async Task DashboardUsesSavedAliasAndViewsFitMinimumWindowWidth()
    {
        using var fixture = new WindowFixture(withLabels: true);
        fixture.Get<TextBox>("RepositoryTextBox").Text = "demo/music";
        fixture.Click("RefreshDashboardButton");
        await fixture.Window.CurrentOperation;
        var rows = fixture.Get<StackPanel>("ActionAccountResultsPanel");
        Assert.Contains(rows.GetVisualDescendants().OfType<TextBlock>(), block => block.Text == "創作帳號");
        Assert.Equal("1", fixture.Get<TextBlock>("SuccessfulAccountsMetric").Text);
        SaveFrame(fixture.Window, "dashboard.png");
        fixture.Click("AccountNavButton");
        Assert.Equal(33, fixture.Get<StackPanel>("AliasListPanel").Children.Count);
        SaveFrame(fixture.Window, "accounts.png");
        fixture.Get<TextBox>("AccountSearchTextBox").Text = "TOKEN33";
        Dispatcher.UIThread.RunJobs();
        Assert.Single(fixture.Get<StackPanel>("AliasListPanel").Children, row => row.IsVisible);
        fixture.Click("LoginNavButton");
        SaveFrame(fixture.Window, "login.png");
        fixture.Window.Width = fixture.Window.MinWidth;
        fixture.Window.Height = fixture.Window.MinHeight;
        Dispatcher.UIThread.RunJobs();
        foreach (var name in new[] { "AccountComboBox", "BrowserComboBox", "StartLoginButton", "SaveSettingsButton" })
        {
            var control = fixture.Get<Control>(name);
            var position = control.TranslatePoint(default, fixture.Window);
            Assert.NotNull(position);
            Assert.True(position.Value.X + control.Bounds.Width <= fixture.Window.ClientSize.Width, name);
        }
        SaveFrame(fixture.Window, "login-minimum.png");
    }

    private static void SaveFrame(Window window, string name)
    {
        var directory = Environment.GetEnvironmentVariable("OPENMUSIC_UI_ARTIFACTS");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using var bitmap = window.CaptureRenderedFrame();
        Assert.NotNull(bitmap);
        bitmap.Save(Path.Combine(directory, name));
    }

    private sealed class WindowFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "OpenMusicFlow.Tests", Guid.NewGuid().ToString("N"));
        public readonly FakeGitHub GitHub = new();
        public readonly FakeLogin Login = new();
        public readonly MainWindow Window;

        public WindowFixture(bool withLabels = false)
        {
            Directory.CreateDirectory(_directory);
            if (withLabels)
                File.WriteAllText(Path.Combine(_directory, "accounts.json"), """{"1":{"Alias":"創作帳號","Email":"music@example.test"}}""");
            Window = new MainWindow(GitHub, Login, new LocalSettingsStore(_directory));
            Window.Show();
        }
        public T Get<T>(string name) where T : Control => Window.FindControl<T>(name) ?? throw new Exception(name);
        public void Click(string name) => Get<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        public void Dispose() { Window.Close(); Directory.Delete(_directory, recursive: true); }
    }

    private sealed class FakeLogin : ILoginCaptureService
    {
        public bool WaitForCancellation;
        public async Task<CapturedLogin> CaptureAsync(int accountNumber, string browser, IProgress<string> progress, CancellationToken cancellationToken)
        {
            if (WaitForCancellation) await Task.Delay(Timeout.Infinite, cancellationToken);
            return new CapturedLogin(accountNumber, "test-only-cookie", $"test{accountNumber}@example.test");
        }
        public Task InstallBrowserAsync(string browser, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeGitHub : IGitHubActionsService
    {
        public (string Repository, int Account, string Token)? Upload;
        public bool FailUpload;
        public int ConnectionChecks;
        public Task<string> ResolveRepositoryAsync(string? repository, CancellationToken cancellationToken) => Task.FromResult(
            GitHubActionsService.ParseGitHubRepo(repository) ?? throw new InvalidOperationException("測試：儲存庫無效"));
        public Task CheckConnectionAsync(string repository, CancellationToken cancellationToken) { ConnectionChecks++; return Task.CompletedTask; }
        public Task TriggerClaimAsync(string repository, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SetAccessTokenAsync(string repository, int accountNumber, string token, CancellationToken cancellationToken)
        {
            if (FailUpload) throw new InvalidOperationException("測試：寫入失敗");
            Upload = (repository, accountNumber, token);
            return Task.CompletedTask;
        }
        public Task<DashboardSnapshot> GetSnapshotAsync(string repository, decimal pointsPerClaim, CancellationToken cancellationToken) =>
            Task.FromResult(new DashboardSnapshot(repository,
                new WorkflowRun(1, DateTimeOffset.Parse("2026-09-04T05:27:00Z"), "success", "completed", "", "schedule"),
                [new AccountResult(1, "completed", "success"), new AccountResult(2, "in_progress", "")],
                DateTimeOffset.Parse("2026-09-04T13:27:00+08:00"), null, 4, 4, pointsPerClaim, 12));
    }
}
