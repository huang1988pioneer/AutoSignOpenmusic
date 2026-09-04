using System.Diagnostics;
using System.Text.Json;

namespace OpenMusicFlow;

internal sealed class GitHubActionsService
{
    public const string WorkflowFile = "autosign.yml";
    public const string WorkflowName = "OpenMusic daily autosign";

    public async Task<string> ResolveRepositoryAsync()
    {
        try
        {
            var json = await RunGhAsync("repo", "view", "--json", "nameWithOwner");
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("nameWithOwner", out var name) &&
                name.GetString() is { Length: > 0 } repo)
                return repo;
        }
        catch (InvalidOperationException)
        {
            // Fall through to git remote.
        }

        var remote = await TryGitRemoteAsync();
        if (!string.IsNullOrWhiteSpace(remote)) return remote;
        throw new InvalidOperationException(
            "找不到 GitHub 儲存庫。請先設定 git remote，並安裝並登入 GitHub CLI（gh auth login）。");
    }

    public async Task TriggerClaimAsync()
    {
        var repo = await ResolveRepositoryAsync();
        await RunGhAsync("workflow", "run", WorkflowFile, "--repo", repo);
    }

    public async Task<DashboardSnapshot> GetSnapshotAsync(decimal pointsPerClaim)
    {
        var repo = await ResolveRepositoryAsync();
        var runsJson = await RunGhAsync(
            "run", "list", "--workflow", WorkflowFile, "--repo", repo, "--limit", "100",
            "--json", "databaseId,createdAt,conclusion,status,url,event,displayTitle,name");
        var runs = JsonSerializer.Deserialize<List<WorkflowRun>>(runsJson, JsonOptions) ?? [];
        var latest = runs.OrderByDescending(run => run.CreatedAt).FirstOrDefault();

        var timeZone = GetTaipeiTimeZone();
        var successfulRuns = runs
            .Where(run => run.IsSuccessful)
            .OrderByDescending(run => run.CreatedAt)
            .ToArray();
        var failedRuns = runs
            .Where(run => run.IsFailed)
            .OrderByDescending(run => run.CreatedAt)
            .ToArray();
        var successfulDates = successfulRuns
            .Select(run => TimeZoneInfo.ConvertTime(run.CreatedAt, timeZone).Date)
            .ToHashSet();
        var today = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, timeZone).Date;
        var monthStart = new DateTime(today.Year, today.Month, 1);

        var consecutiveSuccessActionDays = 0;
        while (successfulDates.Contains(today.AddDays(-consecutiveSuccessActionDays)))
            consecutiveSuccessActionDays++;

        var lastSuccessfulActionTime = successfulRuns.FirstOrDefault() is { } successfulRun
            ? TimeZoneInfo.ConvertTime(successfulRun.CreatedAt, timeZone)
            : (DateTimeOffset?)null;
        var lastFailedActionTime = failedRuns.FirstOrDefault() is { } failedRun
            ? TimeZoneInfo.ConvertTime(failedRun.CreatedAt, timeZone)
            : (DateTimeOffset?)null;

        var monthlySuccessfulClaims = successfulRuns
            .Select(run => TimeZoneInfo.ConvertTime(run.CreatedAt, timeZone).Date)
            .Where(date => date >= monthStart)
            .Distinct()
            .Count();

        var accounts = latest is null ? [] : await GetJobResultsAsync(repo, latest.DatabaseId);

        return new DashboardSnapshot(
            repo,
            latest,
            accounts,
            accounts.Where(account => account.IsSuccessful).ToArray(),
            accounts.Where(account => account.IsFailed).ToArray(),
            lastSuccessfulActionTime,
            lastFailedActionTime,
            consecutiveSuccessActionDays,
            pointsPerClaim,
            monthlySuccessfulClaims * pointsPerClaim);
    }

    private static async Task<AccountResult[]> GetJobResultsAsync(string repo, long runId)
    {
        var detailsJson = await RunGhAsync("run", "view", runId.ToString(), "--repo", repo, "--json", "jobs");
        using var document = JsonDocument.Parse(detailsJson);
        if (!document.RootElement.TryGetProperty("jobs", out var jobs)) return [];

        var results = new List<AccountResult>();
        var index = 1;
        foreach (var job in jobs.EnumerateArray())
        {
            var name = GetString(job, "name");
            if (!name.StartsWith("account ", StringComparison.OrdinalIgnoreCase)) continue;
            var status = GetString(job, "status");
            var conclusion = GetString(job, "conclusion");
            var number = ParseAccountNumber(name, index);
            results.Add(new AccountResult(number, name, status, conclusion));
            index++;
        }
        return results.OrderBy(account => account.Number).ToArray();
    }

    private static async Task<string?> TryGitRemoteAsync()
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    ArgumentList = { "remote", "get-url", "origin" },
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                },
            };
            if (!process.Start()) return null;
            var output = (await process.StandardOutput.ReadToEndAsync()).Trim();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output)) return null;
            return ParseGitHubRepo(output);
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static string? ParseGitHubRepo(string remote)
    {
        remote = remote.Trim();
        const string httpsPrefix = "https://github.com/";
        const string sshPrefix = "git@github.com:";
        if (remote.StartsWith(httpsPrefix, StringComparison.OrdinalIgnoreCase))
            remote = remote[httpsPrefix.Length..];
        else if (remote.StartsWith(sshPrefix, StringComparison.OrdinalIgnoreCase))
            remote = remote[sshPrefix.Length..];
        else
            return null;
        if (remote.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            remote = remote[..^4];
        return remote.Trim('/');
    }

    private static async Task<string> RunGhAsync(params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "gh",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        if (!process.Start()) throw new InvalidOperationException("無法啟動 GitHub CLI（gh）。請先安裝：https://cli.github.com/");

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode == 0) return output;

        var reason = string.IsNullOrWhiteSpace(error) ? output : error;
        reason = reason.Trim();
        if (reason.Length > 1_000) reason = reason[..1_000] + "…";
        throw new InvalidOperationException($"GitHub CLI 執行失敗：{reason}");
    }

    private static TimeZoneInfo GetTaipeiTimeZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Taipei Standard Time"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Taipei"); }
    }

    internal static int ParseAccountNumber(string jobName, int fallback)
    {
        var suffix = jobName.Trim();
        const string prefix = "account ";
        if (suffix.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            suffix = suffix[prefix.Length..];
        var digits = new string(suffix.TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var number) && number > 0 ? number : fallback;
    }

    private static string GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind != JsonValueKind.Null
            ? property.GetString() ?? string.Empty
            : string.Empty;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
}

internal sealed record WorkflowRun(
    long DatabaseId,
    DateTimeOffset CreatedAt,
    string Conclusion,
    string Status,
    string Url,
    string Event)
{
    public bool IsSuccessful => string.Equals(Conclusion, "success", StringComparison.OrdinalIgnoreCase);
    public bool IsCompleted => string.Equals(Status, "completed", StringComparison.OrdinalIgnoreCase);
    public bool IsFailed => IsCompleted && !string.IsNullOrWhiteSpace(Conclusion) && !IsSuccessful;
}

internal sealed record AccountResult(int Number, string Alias, string Status, string Conclusion)
{
    public bool IsSuccessful => string.Equals(Conclusion, "success", StringComparison.OrdinalIgnoreCase);
    public bool IsCompleted => string.Equals(Status, "completed", StringComparison.OrdinalIgnoreCase);
    public bool IsSkipped =>
        string.Equals(Conclusion, "skipped", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Conclusion, "cancelled", StringComparison.OrdinalIgnoreCase);
    public bool IsFailed => IsCompleted && !IsSuccessful && !IsSkipped;
}

internal sealed record DashboardSnapshot(
    string Repository,
    WorkflowRun? LatestRun,
    AccountResult[] Accounts,
    AccountResult[] SuccessfulAccounts,
    AccountResult[] FailedAccounts,
    DateTimeOffset? LastSuccessfulActionTime,
    DateTimeOffset? LastFailedActionTime,
    int ConsecutiveSuccessActionDays,
    decimal PointsPerClaim,
    decimal MonthlyClaimedPoints);
