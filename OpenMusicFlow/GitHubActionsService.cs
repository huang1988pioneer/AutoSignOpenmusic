using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenMusicFlow;

internal interface IGitHubActionsService
{
    Task<string> ResolveRepositoryAsync(string? repository, CancellationToken cancellationToken);
    Task CheckConnectionAsync(string repository, CancellationToken cancellationToken);
    Task TriggerClaimAsync(string repository, CancellationToken cancellationToken);
    Task SetAccessTokenAsync(string repository, int accountNumber, string token, CancellationToken cancellationToken);
    Task<DashboardSnapshot> GetSnapshotAsync(string repository, decimal pointsPerClaim, CancellationToken cancellationToken);
}

internal sealed class GitHubActionsService(ICommandRunner? runner = null) : IGitHubActionsService
{
    public const string WorkflowFile = "autosign.yml";
    public const int HistoryLimit = 100;
    private readonly ICommandRunner _runner = runner ?? new CommandRunner();

    public async Task<string> ResolveRepositoryAsync(string? repository, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(repository))
            return ParseGitHubRepo(repository) ?? throw new InvalidOperationException(
                "請輸入 owner/repository 或完整 GitHub 儲存庫網址。");

        foreach (var directory in RepositoryDirectories())
        {
            try
            {
                var remote = await _runner.RunAsync("git", ["remote", "get-url", "origin"], directory, cancellationToken);
                if (ParseGitHubRepo(remote) is { } detected) return detected;
            }
            catch (InvalidOperationException) { }
        }
        throw new InvalidOperationException("無法自動偵測 GitHub 儲存庫。請填入你的 owner/repository，並儲存設定。");
    }

    public async Task CheckConnectionAsync(string repository, CancellationToken cancellationToken)
    {
        var repo = await ResolveRepositoryAsync(repository, cancellationToken);
        await RunGhAsync(cancellationToken, "repo", "view", repo, "--json", "nameWithOwner");
    }

    public async Task TriggerClaimAsync(string repository, CancellationToken cancellationToken)
    {
        var repo = await ResolveRepositoryAsync(repository, cancellationToken);
        await RunGhAsync(cancellationToken, "workflow", "run", WorkflowFile, "--repo", repo);
    }

    public async Task SetAccessTokenAsync(string repository, int accountNumber, string token, CancellationToken cancellationToken)
    {
        var repo = await ResolveRepositoryAsync(repository, cancellationToken);
        OpenMusicTokenValidator.ValidateCookieValue(token);
        await _runner.RunAsync("gh", ["secret", "set", AccountProfile.SecretName(accountNumber), "--app", "actions", "--repo", repo],
            null, cancellationToken, standardInput: token, sensitive: true);
    }

    public async Task<DashboardSnapshot> GetSnapshotAsync(string repository, decimal pointsPerClaim, CancellationToken cancellationToken)
    {
        var repo = await ResolveRepositoryAsync(repository, cancellationToken);
        var json = await RunGhAsync(cancellationToken, "run", "list", "--workflow", WorkflowFile, "--repo", repo,
            "--limit", HistoryLimit.ToString(CultureInfo.InvariantCulture),
            "--json", "databaseId,createdAt,conclusion,status,url,event");
        var runs = JsonSerializer.Deserialize<List<WorkflowRun>>(json, JsonOptions) ?? [];
        var latest = runs.OrderByDescending(run => run.CreatedAt).FirstOrDefault();
        var accounts = latest is null ? [] : ParseJobResults(await RunGhAsync(cancellationToken, "run", "view",
            latest.DatabaseId.ToString(CultureInfo.InvariantCulture), "--repo", repo, "--json", "jobs"));
        return CreateSnapshot(repo, runs, accounts, pointsPerClaim, DateTimeOffset.UtcNow);
    }

    internal static DashboardSnapshot CreateSnapshot(string repository, IReadOnlyList<WorkflowRun> runs,
        AccountResult[] accounts, decimal pointsPerClaim, DateTimeOffset now)
    {
        var today = TaipeiTime(now).Date;
        var monthStart = new DateTime(today.Year, today.Month, 1);
        var successes = runs.Where(run => run.IsSuccessful).OrderByDescending(run => run.CreatedAt).ToArray();
        var dates = successes.Select(run => TaipeiTime(run.CreatedAt).Date).ToHashSet();
        var streakEnd = dates.Contains(today) ? today : today.AddDays(-1);
        var consecutiveDays = 0;
        while (dates.Contains(streakEnd.AddDays(-consecutiveDays))) consecutiveDays++;
        var lastSuccess = successes.FirstOrDefault();
        var lastFailure = runs.Where(run => run.IsFailed).OrderByDescending(run => run.CreatedAt).FirstOrDefault();
        return new DashboardSnapshot(repository, runs.OrderByDescending(run => run.CreatedAt).FirstOrDefault(), accounts,
            lastSuccess is null ? null : TaipeiTime(lastSuccess.CreatedAt),
            lastFailure is null ? null : TaipeiTime(lastFailure.CreatedAt), consecutiveDays,
            dates.Count(date => date >= monthStart && date <= today), Math.Max(0, pointsPerClaim), runs.Count);
    }

    internal static AccountResult[] ParseJobResults(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("jobs", out var jobs) || jobs.ValueKind != JsonValueKind.Array) return [];
        var results = new List<AccountResult>();
        foreach (var job in jobs.EnumerateArray())
        {
            var name = GetString(job, "name");
            var match = Regex.Match(name, @"^account\s+(\d+)(?:\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success || !int.TryParse(match.Groups[1].Value, out var number) || number is < 1 or > AccountProfile.Count)
                continue;
            results.Add(new AccountResult(number, GetString(job, "status"), GetString(job, "conclusion")));
        }
        return results.OrderBy(account => account.Number).ToArray();
    }

    internal static string? ParseGitHubRepo(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var repository = value.Trim().TrimEnd('/');
        if (repository.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase))
            repository = repository["git@github.com:".Length..];
        else if (Uri.TryCreate(repository, UriKind.Absolute, out var uri))
        {
            if (!string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
                uri.Scheme is not ("https" or "ssh") || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
                (uri.Scheme == "https" && (uri.UserInfo.Length != 0 || !uri.IsDefaultPort)) ||
                (uri.Scheme == "ssh" && uri.UserInfo is not ("" or "git"))) return null;
            repository = uri.AbsolutePath.Trim('/');
        }
        if (repository.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) repository = repository[..^4];
        if (!Regex.IsMatch(repository, @"^[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?/[A-Za-z0-9_.-]+$",
                RegexOptions.CultureInvariant)) return null;
        return repository.Split('/')[1] is "." or ".." ? null : repository;
    }

    private static IEnumerable<string> RepositoryDirectories()
    {
        var visited = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                if (!visited.Add(directory.FullName)) break;
                var git = Path.Combine(directory.FullName, ".git");
                if (Directory.Exists(git) || File.Exists(git))
                {
                    yield return directory.FullName;
                    break;
                }
            }
    }

    private Task<string> RunGhAsync(CancellationToken cancellationToken, params string[] arguments) =>
        _runner.RunAsync("gh", arguments, null, cancellationToken);

    internal static DateTimeOffset TaipeiTime(DateTimeOffset value) => value.ToOffset(TimeSpan.FromHours(8));

    internal static string StatusLabel(string? status, string? conclusion) =>
        (string.IsNullOrWhiteSpace(conclusion) ? status : conclusion)?.ToLowerInvariant() switch
        {
            "success" => "成功", "failure" => "失敗", "cancelled" => "已取消", "skipped" => "已略過",
            "timed_out" => "逾時", "in_progress" => "執行中", "queued" => "排隊中", "waiting" => "等待中",
            "pending" => "等待中", "requested" => "已送出", "action_required" => "需要處理",
            "startup_failure" => "啟動失敗", "neutral" => "中性結果", "completed" => "已完成",
            { Length: > 0 } value => value, _ => "等待結果",
        };

    private static string GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty : string.Empty;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
}

internal sealed record WorkflowRun(long DatabaseId, DateTimeOffset CreatedAt, string Conclusion, string Status, string Url, string Event)
{
    public bool IsSuccessful => string.Equals(Conclusion, "success", StringComparison.OrdinalIgnoreCase);
    public bool IsFailed => string.Equals(Status, "completed", StringComparison.OrdinalIgnoreCase) &&
        Conclusion?.ToLowerInvariant() is "failure" or "timed_out" or "startup_failure" or "action_required";
}

internal sealed record AccountResult(int Number, string Status, string Conclusion)
{
    public bool IsSuccessful => string.Equals(Conclusion, "success", StringComparison.OrdinalIgnoreCase);
    public bool IsFailed => Conclusion.ToLowerInvariant() is "failure" or "timed_out" or "startup_failure" or "action_required";
    public string StatusText => GitHubActionsService.StatusLabel(Status, Conclusion);
}

internal sealed record DashboardSnapshot(string Repository, WorkflowRun? LatestRun, AccountResult[] Accounts,
    DateTimeOffset? LastSuccessfulActionTime, DateTimeOffset? LastFailedActionTime, int ConsecutiveSuccessActionDays,
    int MonthlySuccessfulDays, decimal PointsPerClaim, int LoadedRunCount)
{
    public decimal EstimatedMonthlyPoints => MonthlySuccessfulDays * PointsPerClaim;
}
