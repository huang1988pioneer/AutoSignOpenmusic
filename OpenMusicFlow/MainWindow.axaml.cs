using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace OpenMusicFlow;

public partial class MainWindow : Window
{
    private readonly IGitHubActionsService _github;
    private readonly ILoginCaptureService _login;
    private readonly LocalSettingsStore _store;
    private readonly Dictionary<int, AccountProfile> _accounts;
    private readonly Dictionary<int, Control> _accountRows = new();
    private readonly Dictionary<int, TextBox> _emailInputs = new();
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _operationCancellation;
    private CapturedLogin? _captured;
    private DashboardSnapshot? _snapshot;
    private bool _ready;
    private bool _busy;
    private bool _closed;
    private bool _updatingChoices;

    internal Task CurrentOperation { get; private set; } = Task.CompletedTask;

    public MainWindow() : this(new GitHubActionsService(), new PlaywrightLoginService(), new LocalSettingsStore()) { }

    internal MainWindow(IGitHubActionsService github, ILoginCaptureService login, LocalSettingsStore store)
    {
        _github = github;
        _login = login;
        _store = store;
        InitializeComponent();
        var warnings = new List<string>();
        var settings = _store.LoadSettings(warnings.Add);
        _accounts = _store.LoadAccounts(warnings.Add);
        RepositoryTextBox.Text = settings.Repository;
        PointsPerClaimInput.Value = Math.Clamp(settings.PointsPerClaim, 0, 100_000);
        BrowserComboBox.ItemsSource = BrowserChoices;
        BrowserComboBox.SelectedItem = BrowserChoices.FirstOrDefault(choice => choice.Key == settings.Browser) ?? BrowserChoices[0];
        BuildAliasList();
        UpdateAccountChoices();
        _ready = true;
        UpdateLoginTarget();
        if (warnings.Count > 0) SetStatus(string.Join(Environment.NewLine, warnings), isError: true);
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _captured = null;
        _lifetime.Cancel();
        _lifetime.Dispose();
        base.OnClosed(e);
    }

    private int SelectedAccount => (AccountComboBox.SelectedItem as AccountChoice)?.Number ?? 1;
    private string SelectedBrowser => (BrowserComboBox.SelectedItem as BrowserChoice)?.Key ?? "chrome";
    private decimal PointsPerClaim => PointsPerClaimInput.Value ?? 10;

    private void DashboardNavButton_OnClick(object? sender, RoutedEventArgs e) => ShowView(DashboardView);
    private void AccountNavButton_OnClick(object? sender, RoutedEventArgs e) => ShowView(AccountView);
    private void LoginNavButton_OnClick(object? sender, RoutedEventArgs e) => ShowView(LoginView);

    private void ShowView(Control view)
    {
        DashboardView.IsVisible = view == DashboardView;
        AccountView.IsVisible = view == AccountView;
        LoginView.IsVisible = view == LoginView;
        DashboardNavButton.Classes.Set("selected", view == DashboardView);
        AccountNavButton.Classes.Set("selected", view == AccountView);
        LoginNavButton.Classes.Set("selected", view == LoginView);
        ContentScrollViewer.Offset = default;
    }

    private void DetectRepositoryButton_OnClick(object? sender, RoutedEventArgs e) =>
        RunOperation("正在偵測儲存庫…", async cancellation =>
        {
            RepositoryTextBox.Text = await _github.ResolveRepositoryAsync(null, cancellation);
            SetStatus("已偵測儲存庫；確認後按「儲存設定」。");
        });

    private void CheckConnectionButton_OnClick(object? sender, RoutedEventArgs e) =>
        RunOperation("正在檢查 GitHub 連線…", async cancellation =>
        {
            var repository = await ResolveRepositoryAsync(cancellation);
            await _github.CheckConnectionAsync(repository, cancellation);
            SetStatus($"已連線至 {repository}。寫入 Secret 需要此儲存庫的 Actions Secrets 管理權限。");
        });

    private void SaveSettingsButton_OnClick(object? sender, RoutedEventArgs e) =>
        RunOperation("正在儲存設定…", async _ =>
        {
            var text = RepositoryTextBox.Text?.Trim();
            var repository = string.IsNullOrWhiteSpace(text) ? "" : GitHubActionsService.ParseGitHubRepo(text)
                ?? throw new InvalidOperationException("儲存庫格式不正確，請輸入 owner/repository 或 GitHub 網址。");
            await _store.SaveSettingsAsync(new AppSettings(repository, PointsPerClaim, SelectedBrowser));
            RepositoryTextBox.Text = repository;
            SetStatus("已儲存儲存庫、瀏覽器與點數估算設定。");
        });

    private async Task<string> ResolveRepositoryAsync(CancellationToken cancellationToken)
    {
        var repository = await _github.ResolveRepositoryAsync(RepositoryTextBox.Text, cancellationToken);
        RepositoryTextBox.Text = repository;
        return repository;
    }

    private void TriggerClaimButton_OnClick(object? sender, RoutedEventArgs e) =>
        RunOperation("正在送出簽到執行請求…", async cancellation =>
        {
            var repository = await ResolveRepositoryAsync(cancellation);
            await _github.TriggerClaimAsync(repository, cancellation);
            SetStatus($"已送出 {repository} 的簽到請求。稍後按「更新執行結果」查看進度。", DashboardStatusText);
        }, DashboardStatusText);

    private void RefreshDashboardButton_OnClick(object? sender, RoutedEventArgs e) =>
        RunOperation("正在讀取 GitHub Actions…", async cancellation =>
        {
            var repository = await ResolveRepositoryAsync(cancellation);
            _snapshot = await _github.GetSnapshotAsync(repository, PointsPerClaim, cancellation);
            RenderDashboard(_snapshot);
            SetStatus("執行紀錄已更新。");
        }, DashboardStatusText);

    private void RepositoryTextBox_OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_ready) return;
        UpdateLoginTarget();
        if (_snapshot is not null && !string.Equals(GitHubActionsService.ParseGitHubRepo(RepositoryTextBox.Text),
                _snapshot.Repository, StringComparison.OrdinalIgnoreCase))
        {
            _snapshot = null;
            SuccessfulAccountsMetric.Text = ConsecutiveSuccessActionDaysMetric.Text = MonthlyPointsMetric.Text = "—";
            LastSuccessfulActionMetric.Text = LastFailedActionMetric.Text = "—";
            LatestRunStateText.Text = "尚未讀取";
            AccountResultSummaryText.Text = "等待讀取執行紀錄";
            ActionAccountResultsPanel.Children.Clear();
            DashboardStatusText.Text = "儲存庫已變更，請重新更新執行結果。";
        }
    }

    private void PointsPerClaimInput_OnValueChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (_ready && _snapshot is not null)
        {
            _snapshot = _snapshot with { PointsPerClaim = PointsPerClaim };
            RenderDashboard(_snapshot);
        }
    }

    private void RenderDashboard(DashboardSnapshot snapshot)
    {
        SuccessfulAccountsMetric.Text = snapshot.Accounts.Count(account => account.IsSuccessful).ToString();
        ConsecutiveSuccessActionDaysMetric.Text = $"{snapshot.ConsecutiveSuccessActionDays} 天";
        MonthlyPointsMetric.Text = snapshot.EstimatedMonthlyPoints.ToString("0.##");
        LastSuccessfulActionMetric.Text = FormatTime(snapshot.LastSuccessfulActionTime);
        LastFailedActionMetric.Text = FormatTime(snapshot.LastFailedActionTime);
        AccountResultSummaryText.Text = $"共 {snapshot.Accounts.Length} 個帳號工作";
        HistoryNoteText.Text = $"依最近 {snapshot.LoadedRunCount} 次執行（上限 100 次）統計；本月 {snapshot.MonthlySuccessfulDays} 個成功日。估算點數不代表實際入帳或帳戶餘額。";
        LatestRunStateText.Text = snapshot.LatestRun is { } run
            ? GitHubActionsService.StatusLabel(run.Status, run.Conclusion) : "尚無紀錄";
        DashboardStatusText.Text = snapshot.LatestRun is { } latest
            ? $"{snapshot.Repository} · {FormatTime(GitHubActionsService.TaipeiTime(latest.CreatedAt))}（台北）"
            : $"{snapshot.Repository} 尚無 autosign.yml 的執行紀錄。可先設定帳號，再執行一次簽到。";
        DashboardStatusText.Foreground = Brush.Parse("#657084");
        ActionAccountResultsPanel.Children.Clear();
        foreach (var account in snapshot.Accounts)
        {
            var profile = _accounts.GetValueOrDefault(account.Number, new AccountProfile());
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("64,*,80"), Margin = new Thickness(0, 5) };
            row.Children.Add(new TextBlock { Text = $"#{account.Number:00}", VerticalAlignment = VerticalAlignment.Center });
            var label = new StackPanel { Spacing = 3 };
            label.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(profile.Alias) ? $"帳號 {account.Number:00}" : profile.Alias,
                FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis,
            });
            if (!string.IsNullOrWhiteSpace(profile.Email))
                label.Children.Add(new TextBlock { Text = profile.Email, FontSize = 11, Foreground = Brush.Parse("#657084"),
                    TextTrimming = TextTrimming.CharacterEllipsis });
            Grid.SetColumn(label, 1);
            row.Children.Add(label);
            var status = new TextBlock { Text = account.StatusText, VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                Foreground = Brush.Parse(account.IsSuccessful ? "#187568" : account.IsFailed ? "#B42318" : "#657084") };
            Grid.SetColumn(status, 2);
            row.Children.Add(status);
            ActionAccountResultsPanel.Children.Add(row);
        }
        if (snapshot.LatestRun is not null && snapshot.Accounts.Length == 0)
            ActionAccountResultsPanel.Children.Add(new TextBlock { Text = "尚無帳號工作：可能仍在排隊、前置工作未完成，或未設定任何 Token。",
                TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#657084") });
    }

    private static string FormatTime(DateTimeOffset? value) => value?.ToString("yyyy/MM/dd HH:mm") ?? "—";

    private void BuildAliasList()
    {
        for (var number = 1; number <= AccountProfile.Count; number++)
        {
            var accountNumber = number;
            var profile = _accounts.GetValueOrDefault(number, new AccountProfile());
            var alias = new TextBox { Text = profile.Alias, Watermark = "別名（可留白）" };
            var email = new TextBox { Text = profile.Email, Watermark = "Email 標籤" };
            AutomationProperties.SetName(alias, $"帳號 {number:00} 別名");
            AutomationProperties.SetName(email, $"帳號 {number:00} Email");
            _emailInputs[number] = email;
            void Sync()
            {
                _accounts[accountNumber] = new AccountProfile(alias.Text?.Trim() ?? "", email.Text?.Trim() ?? "");
                UpdateAccountCount();
            }
            alias.TextChanged += (_, _) => Sync();
            email.TextChanged += (_, _) => Sync();
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("50,*,*,Auto"),
                RowDefinitions = new RowDefinitions("Auto,Auto"), ColumnSpacing = 10, RowSpacing = 7 };
            row.Children.Add(new TextBlock { Text = $"{number:00}", VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold });
            Grid.SetColumn(alias, 1);
            Grid.SetColumn(email, 2);
            row.Children.Add(alias);
            row.Children.Add(email);
            var loginButton = new Button { Content = "登入", Padding = new Thickness(12, 7) };
            loginButton.Click += (_, _) =>
            {
                AccountComboBox.SelectedIndex = accountNumber - 1;
                ShowView(LoginView);
            };
            Grid.SetColumn(loginButton, 3);
            row.Children.Add(loginButton);
            var secret = new TextBlock { Text = AccountProfile.SecretName(number), FontSize = 11,
                Foreground = Brush.Parse("#657084"), TextWrapping = TextWrapping.Wrap };
            Grid.SetRow(secret, 1);
            Grid.SetColumn(secret, 1);
            Grid.SetColumnSpan(secret, 3);
            row.Children.Add(secret);
            var border = new Border { Child = row, Background = Brush.Parse("#F8F9FC"),
                CornerRadius = new CornerRadius(8), Padding = new Thickness(12) };
            _accountRows[number] = border;
            AliasListPanel.Children.Add(border);
        }
        UpdateAccountCount();
    }

    private void UpdateAccountCount() => AccountCountText.Text = $"{_accounts.Count(pair => !pair.Value.IsEmpty)} / 33 個標籤";

    private void UpdateAccountChoices()
    {
        var selected = SelectedAccount;
        _updatingChoices = true;
        AccountComboBox.ItemsSource = Enumerable.Range(1, AccountProfile.Count)
            .Select(number => new AccountChoice(number, _accounts.GetValueOrDefault(number, new AccountProfile()).Alias)).ToArray();
        AccountComboBox.SelectedIndex = selected - 1;
        _updatingChoices = false;
    }

    private void AccountSearchTextBox_OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_ready) return;
        var query = AccountSearchTextBox.Text?.Trim() ?? "";
        foreach (var (number, row) in _accountRows)
        {
            var profile = _accounts.GetValueOrDefault(number, new AccountProfile());
            row.IsVisible = $"{number:00} {profile.Alias} {profile.Email} {AccountProfile.SecretName(number)}"
                .Contains(query, StringComparison.OrdinalIgnoreCase);
        }
        NoAccountsMatchText.IsVisible = _accountRows.Values.All(row => !row.IsVisible);
    }

    private void SaveAliasesButton_OnClick(object? sender, RoutedEventArgs e) =>
        RunOperation("正在儲存帳號標籤…", async _ =>
        {
            await _store.SaveAccountsAsync(_accounts);
            UpdateAccountChoices();
            if (_snapshot is not null) RenderDashboard(_snapshot);
            SetStatus("帳號別名與 Email 已儲存在這台電腦。", AccountStatusText);
        }, AccountStatusText);

    private void AccountComboBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _updatingChoices) return;
        ClearCapturedLogin();
        UpdateLoginTarget();
    }

    private void BrowserComboBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        ClearCapturedLogin();
        UpdateLoginTarget();
    }

    private void ClearCapturedLogin()
    {
        _captured = null;
        CapturedAccountText.Text = "尚未取得登入狀態";
        LoginStatusText.Text = "Token 僅暫存在記憶體。請為目前選定帳號重新登入。";
        UpdateLoginButtons();
    }

    private void UpdateLoginTarget()
    {
        TargetRepositoryText.Text = GitHubActionsService.ParseGitHubRepo(RepositoryTextBox.Text) ?? "請先填入上方 GitHub 儲存庫";
        SecretNameText.Text = AccountProfile.SecretName(SelectedAccount);
        UpdateLoginButtons();
    }

    private void UpdateLoginButtons()
    {
        var captured = _captured is not null && _captured.AccountNumber == SelectedAccount;
        UploadSecretButton.IsEnabled = !_busy && captured;
        CopyTokenButton.IsEnabled = !_busy && captured;
        InstallBrowserButton.IsEnabled = !_busy && SelectedBrowser is "chromium" or "firefox";
    }

    private void StartLoginButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;
        ClearCapturedLogin();
        var account = SelectedAccount;
        var browser = SelectedBrowser;
        var autoUpload = AutoUploadCheckBox.IsChecked == true;
        RunOperation("準備開始登入…", async cancellation =>
        {
            string? repository = null;
            if (autoUpload)
            {
                repository = await ResolveRepositoryAsync(cancellation);
                await _github.CheckConnectionAsync(repository, cancellation);
            }
            var capturing = true;
            var progress = new Progress<string>(message =>
            {
                if (capturing && !_closed && !cancellation.IsCancellationRequested) SetStatus(message, LoginStatusText);
            });
            try { _captured = await _login.CaptureAsync(account, browser, progress, cancellation); }
            finally { capturing = false; }
            cancellation.ThrowIfCancellationRequested();
            CapturedAccountText.Text = $"帳號 {account:00} · {_captured.Email}";
            if (string.IsNullOrWhiteSpace(_emailInputs[account].Text)) _emailInputs[account].Text = _captured.Email;
            SetStatus($"已驗證登入並取得 Token（{_captured.AccessToken.Length} 字元）；登入瀏覽器已關閉。", LoginStatusText);
            if (repository is not null) await UploadCapturedAsync(repository, cancellation);
        }, LoginStatusText);
    }

    private void UploadSecretButton_OnClick(object? sender, RoutedEventArgs e) =>
        RunOperation("正在寫入 GitHub Secret…", async cancellation =>
            await UploadCapturedAsync(await ResolveRepositoryAsync(cancellation), cancellation), LoginStatusText);

    private async Task UploadCapturedAsync(string repository, CancellationToken cancellation)
    {
        var captured = _captured;
        if (captured is null || captured.AccountNumber != SelectedAccount)
            throw new InvalidOperationException("請先完成目前帳號的登入擷取。");
        SetStatus($"正在將帳號 {captured.AccountNumber:00} 寫入 {repository}…", LoginStatusText);
        await _github.SetAccessTokenAsync(repository, captured.AccountNumber, captured.AccessToken, cancellation);
        SetStatus($"已寫入 {repository} → {AccountProfile.SecretName(captured.AccountNumber)}。GitHub 排程可使用此帳號；也可到「簽到總覽」立即執行。", LoginStatusText);
    }

    private void InstallBrowserButton_OnClick(object? sender, RoutedEventArgs e) =>
        RunOperation("正在下載 Playwright 瀏覽器，首次安裝可能需要幾分鐘…", async cancellation =>
        {
            await _login.InstallBrowserAsync(SelectedBrowser, cancellation);
            SetStatus("瀏覽器已安裝，可以開始登入。", LoginStatusText);
        }, LoginStatusText);

    private void CopyTokenButton_OnClick(object? sender, RoutedEventArgs e) =>
        RunOperation("正在複製…", async _ =>
        {
            if (_captured is null || _captured.AccountNumber != SelectedAccount)
                throw new InvalidOperationException("請先登入目前帳號。");
            await CopyAsync(_captured.AccessToken);
            SetStatus($"已複製帳號 {SelectedAccount:00} 的 Token。", LoginStatusText);
        }, LoginStatusText);

    private void CopySecretButton_OnClick(object? sender, RoutedEventArgs e) =>
        RunOperation("正在複製…", async _ =>
        {
            await CopyAsync(AccountProfile.SecretName(SelectedAccount));
            SetStatus($"已複製 {AccountProfile.SecretName(SelectedAccount)}。", LoginStatusText);
        }, LoginStatusText);

    private void CopyAllSecretsButton_OnClick(object? sender, RoutedEventArgs e) =>
        RunOperation("正在複製…", async _ =>
        {
            await CopyAsync(string.Join(Environment.NewLine, Enumerable.Range(1, AccountProfile.Count).Select(AccountProfile.SecretName)));
            SetStatus("已複製 33 個 GitHub Secret 名稱。", AccountStatusText);
        }, AccountStatusText);

    private async Task CopyAsync(string text)
    {
        if (Clipboard is not { } clipboard) throw new InvalidOperationException("目前無法存取剪貼簿。");
        await clipboard.SetTextAsync(text);
    }

    private void OpenActionsButton_OnClick(object? sender, RoutedEventArgs e) => OpenRepositoryPage("/actions/workflows/autosign.yml");
    private void OpenSecretsButton_OnClick(object? sender, RoutedEventArgs e) => OpenRepositoryPage("/settings/secrets/actions");
    private void OpenMusicButton_OnClick(object? sender, RoutedEventArgs e) =>
        RunOperation("正在開啟網站…", async _ => await OpenUrlAsync(PlaywrightLoginService.BaseUrl));

    private void OpenRepositoryPage(string path) =>
        RunOperation("正在開啟 GitHub…", async cancellation =>
            await OpenUrlAsync("https://github.com/" + await ResolveRepositoryAsync(cancellation) + path));

    private async Task OpenUrlAsync(string url)
    {
        if (!await Launcher.LaunchUriAsync(new Uri(url))) throw new InvalidOperationException("無法啟動預設瀏覽器。");
        SetStatus("已在預設瀏覽器開啟。");
    }

    private void CancelOperationButton_OnClick(object? sender, RoutedEventArgs e)
    {
        _operationCancellation?.Cancel();
        CancelOperationButton.IsEnabled = false;
        SetStatus("正在取消並關閉此操作開啟的程序…");
    }

    private void RunOperation(string message, Func<CancellationToken, Task> action, TextBlock? status = null)
    {
        if (_busy || _closed) return;
        CurrentOperation = ExecuteOperationAsync(message, action, status);
    }

    private async Task ExecuteOperationAsync(string message, Func<CancellationToken, Task> action, TextBlock? status)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _operationCancellation = cancellation;
        SetBusy(true);
        SetStatus(message, status);
        try { await action(cancellation.Token); }
        catch (OperationCanceledException)
        {
            _captured = null;
            if (!_closed)
            {
                CapturedAccountText.Text = "操作已取消";
                SetStatus("操作已取消。若已送出 GitHub 寫入或簽到請求，請到 GitHub 確認結果。", status);
            }
        }
        catch (Exception exception)
        {
            if (!_closed) SetStatus(exception.Message, status, isError: true);
        }
        finally
        {
            _operationCancellation = null;
            if (!_closed) SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        foreach (var control in new Control[] { RepositoryTextBox, DetectRepositoryButton, CheckConnectionButton,
                     SaveSettingsButton, TriggerClaimButton, RefreshDashboardButton, OpenActionsButton, OpenSecretsButton,
                     PointsPerClaimInput, SaveAliasesButton, AliasListPanel, AccountComboBox, BrowserComboBox,
                     AutoUploadCheckBox, StartLoginButton })
            control.IsEnabled = !busy;
        BusyIndicator.IsVisible = busy;
        CancelOperationButton.IsVisible = busy;
        CancelOperationButton.IsEnabled = busy;
        UpdateLoginButtons();
    }

    private void SetStatus(string message, TextBlock? status = null, bool isError = false)
    {
        var color = Brush.Parse(isError ? "#B42318" : "#657084");
        OperationStatusText.Text = message;
        OperationStatusText.Foreground = color;
        if (status is not null) { status.Text = message; status.Foreground = color; }
    }

    private static readonly BrowserChoice[] BrowserChoices =
    [
        new("chrome", "Google Chrome（已安裝）"),
        new("msedge", "Microsoft Edge（已安裝）"),
        new("chromium", "Chromium（Playwright）"),
        new("firefox", "Firefox（Playwright）"),
    ];

    private sealed record BrowserChoice(string Key, string Label) { public override string ToString() => Label; }
    private sealed record AccountChoice(int Number, string Alias)
    {
        public override string ToString() => string.IsNullOrWhiteSpace(Alias) ? $"帳號 {Number:00}" : $"{Number:00} · {Alias}";
    }
}
