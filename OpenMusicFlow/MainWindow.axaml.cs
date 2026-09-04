using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace OpenMusicFlow;

public partial class MainWindow : Window
{
    private const int AccountCount = 10;
    private readonly GitHubActionsService _githubActions = new();
    private readonly Dictionary<int, TextBox> _aliasInputs = new();
    private readonly Dictionary<int, TextBox> _emailInputs = new();
    private readonly Dictionary<int, AccountProfile> _accounts = LoadAccounts();

    public MainWindow()
    {
        InitializeComponent();
        AccountComboBox.ItemsSource = Enumerable.Range(1, AccountCount).Select(number => $"帳號 {number:00}").ToArray();
        AccountComboBox.SelectionChanged += (_, _) => UpdateSelectedAccount();
        BuildAliasList();
        UpdateSelectedAccount();
    }

    private int AccountNumber => Math.Max(1, AccountComboBox.SelectedIndex + 1);
    private AccountProfile Profile => _accounts.TryGetValue(AccountNumber, out var profile)
        ? profile
        : _accounts[AccountNumber] = new AccountProfile();

    private string EmailSecretName => AccountNumber == 1 ? "OPENMUSIC_EMAIL" : $"OPENMUSIC_EMAIL_{AccountNumber}";
    private string PasswordSecretName => AccountNumber == 1 ? "OPENMUSIC_PASSWORD" : $"OPENMUSIC_PASSWORD_{AccountNumber}";

    private void UpdateSelectedAccount()
    {
        SecretNameText.Text = $"{EmailSecretName} / {PasswordSecretName}";
        var alias = Profile.Alias;
        AccountAliasText.Text = alias;
        AccountAliasText.IsVisible = !string.IsNullOrWhiteSpace(alias);
        if (!string.IsNullOrWhiteSpace(Profile.Email))
            EmailTextBox.Text = Profile.Email;
        ResultText.Text = AccountNumber == 1
            ? "帳號 01 對應 GitHub Actions 目前使用的 OPENMUSIC_EMAIL / OPENMUSIC_PASSWORD。"
            : $"帳號 {AccountNumber:00} 的 Secret 名稱為 {EmailSecretName} / {PasswordSecretName}。目前 workflow 只讀帳號 01。";
    }

    private void DashboardNavButton_OnClick(object? sender, RoutedEventArgs e) => ShowView(DashboardView);

    private void AccountNavButton_OnClick(object? sender, RoutedEventArgs e) => ShowView(AccountView);

    private void LoginNavButton_OnClick(object? sender, RoutedEventArgs e) => ShowView(LoginView);

    private void ShowView(Control view)
    {
        DashboardView.IsVisible = view == DashboardView;
        AccountView.IsVisible = view == AccountView;
        LoginView.IsVisible = view == LoginView;
        view.BringIntoView();
    }

    private async void TestLoginButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (!TryReadCredentials(out var email, out var password)) return;
        SetBusy(true);
        StatusText.Text = "正在測試登入…";
        try
        {
            using var client = new OpenMusicClient();
            await client.LoginAsync(email, password);
            var user = await client.GetUserAsync();
            var status = await client.GetCheckInStatusAsync();
            var already = status.TryGetProperty("today_checked_in", out var checkedIn) &&
                          checkedIn.ValueKind == JsonValueKind.True;
            StatusText.Text =
                $"登入成功。{OpenMusicClient.SummarizeUser(user)}" +
                (already ? " 今天已簽到。" : " 今天尚未領獎。");
            RememberEmail(email);
        }
        catch (Exception exception)
        {
            StatusText.Text = $"登入失敗：{exception.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void ClaimButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (!TryReadCredentials(out var email, out var password)) return;
        SetBusy(true);
        StatusText.Text = "正在登入並領取簽到獎賞…";
        try
        {
            using var client = new OpenMusicClient();
            var result = await client.RunClaimAsync(email, password);
            StatusText.Text = result.AlreadyClaimed
                ? $"今天已領過。{result.Before}"
                : $"領獎完成。{result.After}";
            ResultText.Text = result.Log.Trim();
            RememberEmail(email);
        }
        catch (Exception exception)
        {
            StatusText.Text = $"領獎失敗：{exception.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void CopyEmailSecretButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (Clipboard is { } clipboard) await clipboard.SetTextAsync(EmailSecretName);
        StatusText.Text = $"已複製 {EmailSecretName}。";
    }

    private async void CopyPasswordSecretButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (Clipboard is { } clipboard) await clipboard.SetTextAsync(PasswordSecretName);
        StatusText.Text = $"已複製 {PasswordSecretName}。請到 GitHub Secrets 貼上密碼，不要把密碼提交到 Git。";
    }

    private async void CopyEmailValueButton_OnClick(object? sender, RoutedEventArgs e)
    {
        var email = EmailTextBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(email))
        {
            StatusText.Text = "請先輸入 Email。";
            return;
        }
        if (Clipboard is { } clipboard) await clipboard.SetTextAsync(email);
        StatusText.Text = "已複製 Email。";
    }

    private async void TriggerClaimButton_OnClick(object? sender, RoutedEventArgs e)
    {
        TriggerClaimButton.IsEnabled = false;
        RefreshDashboardButton.IsEnabled = false;
        DashboardStatusText.Text = "正在送出 GitHub Actions 手動執行請求…";
        try
        {
            await _githubActions.TriggerClaimAsync();
            DashboardStatusText.Text = "已觸發每日簽到 workflow。啟動後按「更新執行結果」即可查看進度。";
        }
        catch (Exception exception)
        {
            DashboardStatusText.Text = $"無法觸發 workflow：{exception.Message}";
        }
        finally
        {
            TriggerClaimButton.IsEnabled = true;
            RefreshDashboardButton.IsEnabled = true;
        }
    }

    private async void RefreshDashboardButton_OnClick(object? sender, RoutedEventArgs e)
    {
        TriggerClaimButton.IsEnabled = false;
        RefreshDashboardButton.IsEnabled = false;
        DashboardStatusText.Text = "正在讀取 GitHub Actions 執行紀錄…";
        try
        {
            var snapshot = await _githubActions.GetSnapshotAsync(ParsePointsPerClaim());
            RenderDashboard(snapshot);
        }
        catch (Exception exception)
        {
            DashboardStatusText.Text = $"無法讀取 GitHub Actions：{exception.Message}";
        }
        finally
        {
            TriggerClaimButton.IsEnabled = true;
            RefreshDashboardButton.IsEnabled = true;
        }
    }

    private decimal ParsePointsPerClaim()
    {
        if (decimal.TryParse(PointsPerClaimTextBox.Text, out var points) && points >= 0) return points;
        PointsPerClaimTextBox.Text = "10";
        return 10;
    }

    private void RenderDashboard(DashboardSnapshot snapshot)
    {
        SuccessfulAccountsMetric.Text = $"{snapshot.SuccessfulAccounts.Length} 個";
        ConsecutiveSuccessActionDaysMetric.Text = $"{snapshot.ConsecutiveSuccessActionDays} 天";
        MonthlyPointsMetric.Text = snapshot.MonthlyClaimedPoints.ToString("0.##");
        LastSuccessfulActionMetric.Text = FormatActionTime(snapshot.LastSuccessfulActionTime);
        LastFailedActionMetric.Text = FormatActionTime(snapshot.LastFailedActionTime);

        if (snapshot.LatestRun is null)
        {
            DashboardStatusText.Text = $"儲存庫 {snapshot.Repository} 尚無 {GitHubActionsService.WorkflowName} 執行紀錄。";
            ActionAccountResultsPanel.Children.Clear();
            return;
        }

        var runStatus = string.IsNullOrWhiteSpace(snapshot.LatestRun.Conclusion)
            ? snapshot.LatestRun.Status
            : snapshot.LatestRun.Conclusion;
        DashboardStatusText.Text =
            $"{snapshot.Repository} · 最近執行：{snapshot.LatestRun.CreatedAt.ToLocalTime():g} · {runStatus} · " +
            $"成功 {snapshot.SuccessfulAccounts.Length}、失敗 {snapshot.FailedAccounts.Length}。";

        ActionAccountResultsPanel.Children.Clear();
        foreach (var account in snapshot.Accounts)
        {
            var result = account.IsSuccessful
                ? "成功"
                : account.IsCompleted ? "失敗" : account.Status;
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("78,160,*") };
            row.Children.Add(new TextBlock { Text = $"Job {account.Number:00}", FontWeight = FontWeight.SemiBold });
            var aliasText = new TextBlock { Text = account.Alias, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(aliasText, 1);
            row.Children.Add(aliasText);
            var stateText = new TextBlock { Text = result };
            Grid.SetColumn(stateText, 2);
            row.Children.Add(stateText);
            ActionAccountResultsPanel.Children.Add(row);
        }

        if (snapshot.Accounts.Length == 0)
            ActionAccountResultsPanel.Children.Add(new TextBlock { Text = "最新 run 尚未建立 job；請稍後再更新。" });
    }

    private static string FormatActionTime(DateTimeOffset? actionTime) =>
        actionTime is { } value ? value.ToString("yyyy/MM/dd HH:mm") : "—";

    private void BuildAliasList()
    {
        AliasListPanel.Children.Clear();
        for (var number = 1; number <= AccountCount; number++)
        {
            var profile = _accounts.TryGetValue(number, out var existing) ? existing : new AccountProfile();
            var aliasInput = new TextBox
            {
                Width = 160,
                Watermark = "別名（可留白）",
                Text = profile.Alias,
            };
            var emailInput = new TextBox
            {
                Width = 240,
                Watermark = "Email",
                Text = profile.Email,
            };
            var accountNumber = number;
            void Sync()
            {
                _accounts[accountNumber] = new AccountProfile(
                    aliasInput.Text?.Trim() ?? string.Empty,
                    emailInput.Text?.Trim() ?? string.Empty);
                if (accountNumber == AccountNumber) UpdateSelectedAccount();
            }
            aliasInput.TextChanged += (_, _) => Sync();
            emailInput.TextChanged += (_, _) => Sync();
            _aliasInputs[number] = aliasInput;
            _emailInputs[number] = emailInput;

            var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 10 };
            row.Children.Add(new TextBlock
            {
                Text = $"帳號 {number:00}",
                Width = 68,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            });
            row.Children.Add(aliasInput);
            row.Children.Add(emailInput);
            AliasListPanel.Children.Add(row);
        }
    }

    private async void SaveAliasesButton_OnClick(object? sender, RoutedEventArgs e)
    {
        foreach (var number in Enumerable.Range(1, AccountCount))
        {
            _accounts[number] = new AccountProfile(
                _aliasInputs[number].Text?.Trim() ?? string.Empty,
                _emailInputs[number].Text?.Trim() ?? string.Empty);
        }
        var directory = Path.GetDirectoryName(AccountsFile);
        if (directory is not null) Directory.CreateDirectory(directory);
        var payload = _accounts
            .Where(pair => !pair.Value.IsEmpty)
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        await File.WriteAllTextAsync(AccountsFile, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        UpdateSelectedAccount();
        StatusText.Text = "帳號別名與 Email 已儲存在這台電腦（不含密碼）。";
    }

    private bool TryReadCredentials(out string email, out string password)
    {
        email = EmailTextBox.Text?.Trim() ?? string.Empty;
        password = PasswordTextBox.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            StatusText.Text = "請輸入 Email 與密碼。";
            return false;
        }
        return true;
    }

    private void RememberEmail(string email)
    {
        Profile.Email = email;
        if (_emailInputs.TryGetValue(AccountNumber, out var input))
            input.Text = email;
    }

    private void SetBusy(bool busy)
    {
        TestLoginButton.IsEnabled = !busy;
        ClaimButton.IsEnabled = !busy;
    }

    private static string AccountsFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "OpenMusicFlow",
        "accounts.json");

    private static Dictionary<int, AccountProfile> LoadAccounts()
    {
        if (!File.Exists(AccountsFile)) return new Dictionary<int, AccountProfile>();
        try
        {
            return JsonSerializer.Deserialize<Dictionary<int, AccountProfile>>(File.ReadAllText(AccountsFile))
                   ?? new Dictionary<int, AccountProfile>();
        }
        catch (JsonException)
        {
            return new Dictionary<int, AccountProfile>();
        }
    }
}

internal sealed class AccountProfile
{
    public AccountProfile() { }

    public AccountProfile(string alias, string email)
    {
        Alias = alias;
        Email = email;
    }

    public string Alias { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public bool IsEmpty => string.IsNullOrWhiteSpace(Alias) && string.IsNullOrWhiteSpace(Email);
}
