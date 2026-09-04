using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace OpenMusicFlow;

public partial class MainWindow : Window
{
    private const int AccountCount = 10;
    private const string CookiesSecretName = "OPENMUSIC_COOKIES";
    private const string TokenSecretName = "OPENMUSIC_ACCESS_TOKEN";

    private readonly GitHubActionsService _githubActions = new();
    private readonly Dictionary<int, TextBox> _aliasInputs = new();
    private readonly Dictionary<int, TextBox> _emailInputs = new();
    private readonly Dictionary<int, AccountProfile> _accounts = LoadAccounts();

    public MainWindow()
    {
        InitializeComponent();
        BuildAliasList();
    }

    private void DashboardNavButton_OnClick(object? sender, RoutedEventArgs e) => ShowView(DashboardView);

    private void AccountNavButton_OnClick(object? sender, RoutedEventArgs e) => ShowView(AccountView);

    private void ShowView(Control view)
    {
        DashboardView.IsVisible = view == DashboardView;
        AccountView.IsVisible = view == AccountView;
        view.BringIntoView();
    }

    private async void CopyCookiesSecretButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (Clipboard is { } clipboard) await clipboard.SetTextAsync(CookiesSecretName);
        AccountStatusText.Text =
            $"已複製 {CookiesSecretName}。請貼 OPENMUSIC_ACCESS_TOKEN=...; OPENMUSIC_SESSION_ID=...，不要提交到 Git。";
    }

    private async void CopyTokenSecretButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (Clipboard is { } clipboard) await clipboard.SetTextAsync(TokenSecretName);
        AccountStatusText.Text =
            $"已複製 {TokenSecretName}。可再新增 OPENMUSIC_SESSION_ID。Cookie 過期時請重新從瀏覽器複製。";
    }

    private async void TriggerClaimButton_OnClick(object? sender, RoutedEventArgs e)
    {
        TriggerClaimButton.IsEnabled = false;
        RefreshDashboardButton.IsEnabled = false;
        DashboardStatusText.Text = "正在送出 GitHub Actions 手動執行請求…";
        try
        {
            await _githubActions.TriggerClaimAsync();
            DashboardStatusText.Text = "已觸發每日簽到 workflow。GitHub Actions 會負責登入、簽到與領獎；啟動後按「更新執行結果」即可查看進度。";
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
        AccountStatusText.Text = "帳號別名與 Email 標籤已儲存在這台電腦。簽到身分仍是 GitHub Secrets 裡的 cookie。";
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
