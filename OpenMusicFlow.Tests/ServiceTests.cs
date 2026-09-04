using System.Net;
using System.Text.Json;
using Xunit;

namespace OpenMusicFlow.Tests;

public class ServiceTests
{
    [Theory]
    [InlineData("demo/music", "demo/music")]
    [InlineData(" https://github.com/demo/music.git/ ", "demo/music")]
    [InlineData("git@github.com:demo/music.git", "demo/music")]
    [InlineData("ssh://git@github.com/demo/music.git", "demo/music")]
    [InlineData("https://github.com/demo/music/actions", null)]
    [InlineData("https://github.com.evil.test/demo/music", null)]
    [InlineData("https://github.com/demo/music?token=example", null)]
    [InlineData("https://credentials@github.com/demo/music", null)]
    [InlineData("--repo=demo/music", null)]
    [InlineData("demo/../music", null)]
    public void RepositoryInputIsLimitedToOneGitHubRepository(string input, string? expected) =>
        Assert.Equal(expected, GitHubActionsService.ParseGitHubRepo(input));

    [Theory]
    [InlineData(1, "OPENMUSIC_ACCESS_TOKEN")]
    [InlineData(2, "OPENMUSIC_ACCESS_TOKEN2")]
    [InlineData(33, "OPENMUSIC_ACCESS_TOKEN33")]
    public async Task SecretUploadUsesTheSelectedSlotAndStdin(int number, string name)
    {
        var runner = new RecordingRunner();
        await new GitHubActionsService(runner).SetAccessTokenAsync("https://github.com/demo/music", number,
            "test-only-cookie", CancellationToken.None);
        Assert.Equal("gh", runner.Executable);
        Assert.Equal(new[] { "secret", "set", name, "--app", "actions", "--repo", "demo/music" }, runner.Arguments);
        Assert.Equal("test-only-cookie", runner.Input);
        Assert.True(runner.Sensitive);
        Assert.DoesNotContain("test-only-cookie", runner.Arguments);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(34)]
    public async Task InvalidAccountNeverWritesASecret(int number)
    {
        var runner = new RecordingRunner();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new GitHubActionsService(runner)
            .SetAccessTokenAsync("demo/music", number, "test-only-cookie", CancellationToken.None));
        Assert.Null(runner.Executable);
    }

    [Fact]
    public void StatisticsDeduplicateTaipeiDatesAndKeepYesterdayStreak()
    {
        WorkflowRun Run(int id, string time, string conclusion = "success") =>
            new(id, DateTimeOffset.Parse(time), conclusion, "completed", "", "schedule");
        var runs = new[]
        {
            Run(1, "2026-08-31T17:00:00Z"), // September 1 in Taipei.
            Run(2, "2026-09-02T01:27:00Z"),
            Run(3, "2026-09-02T13:27:00Z"), // Same day, not another claim day.
            Run(4, "2026-09-03T13:27:00Z"),
            Run(5, "2026-09-03T21:27:00Z", "failure"), // Today has not succeeded.
            Run(6, "2026-09-03T22:00:00Z", "cancelled"),
        };
        var snapshot = GitHubActionsService.CreateSnapshot("demo/music", runs, [], 10,
            DateTimeOffset.Parse("2026-09-04T00:00:00Z"));
        Assert.Equal(3, snapshot.MonthlySuccessfulDays);
        Assert.Equal(30m, snapshot.EstimatedMonthlyPoints);
        Assert.Equal(3, snapshot.ConsecutiveSuccessActionDays);
        Assert.Equal(runs[4].CreatedAt.ToOffset(TimeSpan.FromHours(8)), snapshot.LastFailedActionTime);
    }

    [Fact]
    public void JobParsingPreservesSparseSlotsAndDistinctStatuses()
    {
        var jobs = GitHubActionsService.ParseJobResults("""
            {"jobs":[
              {"name":"select accounts","status":"completed","conclusion":"success"},
              {"name":"account 33","status":"completed","conclusion":"cancelled"},
              {"name":"account 1 - goldshoot0720","status":"completed","conclusion":"success"},
              {"name":"account 16 - chbondg2","status":"completed","conclusion":"failure"},
              {"name":"account 2","status":"completed","conclusion":"skipped"},
              {"name":"account 7","status":"in_progress","conclusion":null},
              {"name":"account 34","status":"completed","conclusion":"success"}
            ]}
            """);
        Assert.Equal(new[] { 1, 2, 7, 16, 33 }, jobs.Select(job => job.Number));
        Assert.Equal(new[] { "成功", "已略過", "執行中", "失敗", "已取消" }, jobs.Select(job => job.StatusText));
        Assert.True(jobs[0].IsSuccessful);
        Assert.True(jobs[3].IsFailed);
        Assert.All(jobs.Skip(1), job => Assert.False(job.IsSuccessful));
    }

    [Fact]
    public async Task CookieIsVerifiedUsingOnlyTheOpenMusicUserEndpoint()
    {
        var handler = new UserHandler("""{"code":200,"data":{"user":{"id":1,"email":"test@example.test"}}}""");
        using var validator = new OpenMusicTokenValidator(handler);
        Assert.Equal("test@example.test", await validator.ValidateAsync("test-only-cookie", CancellationToken.None));
        Assert.Equal("https://www.openmusic.ai/common-api/v1/user", handler.Url);
        Assert.Equal("OPENMUSIC_ACCESS_TOKEN=test-only-cookie", handler.Cookie);
    }

    [Theory]
    [InlineData("{\"code\":401,\"data\":null}")]
    [InlineData("{\"code\":200,\"data\":{\"user\":null}}")]
    [InlineData("{\"code\":200,\"data\":{\"user\":{}}}")]
    [InlineData("<html>test-only-secret-value</html>")]
    public async Task UnauthenticatedOrInvalidResponsesDoNotProduceAToken(string json)
    {
        using var validator = new OpenMusicTokenValidator(new UserHandler(json));
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => validator.ValidateAsync("test-only-cookie", CancellationToken.None));
        Assert.DoesNotContain("test-only-secret-value", exception.Message);
    }

    [Theory]
    [InlineData("token; other=cookie")]
    [InlineData("token\r\nHeader: value")]
    [InlineData("")]
    public void InvalidCookieHeadersAreRejected(string token) =>
        Assert.Throws<InvalidOperationException>(() => OpenMusicTokenValidator.ValidateCookieValue(token));

    [Fact]
    public async Task SettingsRemainCompatibleWithExistingAccountLabels()
    {
        var path = Path.Combine(Path.GetTempPath(), "OpenMusicFlow.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(path, "accounts.json"), """{"1":{"Alias":"My music","Email":"label@example.test","IsEmpty":false},"33":{"Alias":"Last"}}""");
            var store = new LocalSettingsStore(path);
            var warnings = new List<string>();
            var accounts = store.LoadAccounts(warnings.Add);
            Assert.Equal("Last", accounts[33].Alias);
            await store.SaveAccountsAsync(accounts);
            Assert.DoesNotContain("IsEmpty", await File.ReadAllTextAsync(Path.Combine(path, "accounts.json")));
            var settingsPath = Path.Combine(path, "settings.json");
            await File.WriteAllTextAsync(settingsPath, "corrupt");
            Assert.Equal(new AppSettings(), store.LoadSettings(warnings.Add));
            Assert.Single(warnings);
            Assert.Equal("corrupt", await File.ReadAllTextAsync(settingsPath));
        }
        finally { Directory.Delete(path, recursive: true); }
    }

    [Fact]
    public void KnownAccountsGetDefaultsWithoutOverwritingAnExistingAlias()
    {
        var emptyPath = Path.Combine(Path.GetTempPath(), "OpenMusicFlow.Tests", Guid.NewGuid().ToString("N"));
        var customPath = Path.Combine(Path.GetTempPath(), "OpenMusicFlow.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(customPath);
        try
        {
            var warnings = new List<string>();
            var defaults = new LocalSettingsStore(emptyPath).LoadAccounts(warnings.Add);
            Assert.Equal("goldshoot0720", defaults[1].Alias);
            Assert.Equal("abuhg17", defaults[2].Alias);
            Assert.Equal("huang1988pioneer", defaults[3].Alias);
            Assert.Equal("samafengtu", defaults[4].Alias);
            Assert.Equal("fengtusama", defaults[5].Alias);
            Assert.Equal("tushenbyfengbro", defaults[6].Alias);
            Assert.Equal("fengwithting0831", defaults[7].Alias);
            Assert.Equal("fengwithfeng1127", defaults[8].Alias);
            Assert.Equal("fengwithtu1127", defaults[9].Alias);
            Assert.Equal("akaonda333", defaults[10].Alias);
            Assert.Equal("engdictatorf", defaults[11].Alias);
            Assert.Equal("fengtuprinfo", defaults[12].Alias);
            Assert.Equal("fbussinesseng", defaults[13].Alias);
            Assert.Equal("flottojackpoteng", defaults[14].Alias);
            Assert.Equal("feng33feng35feng3", defaults[15].Alias);
            Assert.Equal("chbondg2", defaults[16].Alias);
            Assert.False(defaults.ContainsKey(17));
            File.WriteAllText(Path.Combine(customPath, "accounts.json"), """{"1":{"Alias":"custom-name","Email":""}}""");
            Assert.Equal("custom-name", new LocalSettingsStore(customPath).LoadAccounts(warnings.Add)[1].Alias);
            Assert.Empty(warnings);
        }
        finally
        {
            if (Directory.Exists(emptyPath)) Directory.Delete(emptyPath, recursive: true);
            Directory.Delete(customPath, recursive: true);
        }
    }

    private sealed class RecordingRunner : ICommandRunner
    {
        public string? Executable;
        public string[] Arguments = [];
        public string? Input;
        public bool Sensitive;
        public Task<string> RunAsync(string executable, IReadOnlyList<string> arguments, string? workingDirectory,
            CancellationToken cancellationToken, string? standardInput = null, bool sensitive = false, TimeSpan? timeLimit = null)
        {
            Executable = executable;
            Arguments = arguments.ToArray();
            Input = standardInput;
            Sensitive = sensitive;
            return Task.FromResult("");
        }
    }

    private sealed class UserHandler(string json) : HttpMessageHandler
    {
        public string? Url;
        public string? Cookie;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Url = request.RequestUri?.ToString();
            Cookie = request.Headers.GetValues("Cookie").Single();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }
}
