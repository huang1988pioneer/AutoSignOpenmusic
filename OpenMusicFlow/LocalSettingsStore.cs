using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenMusicFlow;

internal sealed class LocalSettingsStore(string? directory = null)
{
    public string DirectoryPath { get; } = directory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenMusicFlow");

    public AppSettings LoadSettings(Action<string> reportError) =>
        Load("settings.json", new AppSettings(), reportError);

    public Dictionary<int, AccountProfile> LoadAccounts(Action<string> reportError)
    {
        var accounts = Load("accounts.json", new Dictionary<int, AccountProfile>(), reportError)
            .Where(pair => pair.Key is >= 1 and <= AccountProfile.Count && pair.Value is not null)
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        foreach (var number in Enumerable.Range(1, AccountProfile.Count))
        {
            var alias = AccountProfile.DefaultAlias(number);
            if (string.IsNullOrEmpty(alias)) continue;
            if (!accounts.TryGetValue(number, out var profile))
                accounts[number] = new AccountProfile(alias);
            else if (string.IsNullOrWhiteSpace(profile.Alias))
                accounts[number] = profile with { Alias = alias };
        }
        return accounts;
    }

    public Task SaveSettingsAsync(AppSettings settings) => SaveAsync("settings.json", settings);

    public Task SaveAccountsAsync(Dictionary<int, AccountProfile> accounts) =>
        SaveAsync("accounts.json", accounts
            .Where(pair => pair.Key is >= 1 and <= AccountProfile.Count && !pair.Value.IsEmpty)
            .ToDictionary(pair => pair.Key, pair => pair.Value));

    private T Load<T>(string fileName, T fallback, Action<string> reportError)
    {
        var path = Path.Combine(DirectoryPath, fileName);
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path)) ?? fallback;
        }
        catch (FileNotFoundException) { return fallback; }
        catch (DirectoryNotFoundException) { return fallback; }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            reportError($"無法讀取 {fileName}：{exception.Message}。目前使用預設值，原檔案仍保留。");
            return fallback;
        }
    }

    private async Task SaveAsync<T>(string fileName, T value)
    {
        Directory.CreateDirectory(DirectoryPath);
        var destination = Path.Combine(DirectoryPath, fileName);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(value, JsonOptions));
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
}

internal sealed record AppSettings(string Repository = "", decimal PointsPerClaim = 10, string Browser = "chrome");

internal sealed record AccountProfile(string Alias = "", string Email = "")
{
    public const int Count = 33;

    [JsonIgnore]
    public bool IsEmpty => string.IsNullOrWhiteSpace(Alias) && string.IsNullOrWhiteSpace(Email);

    public static string SecretName(int number)
    {
        if (number is < 1 or > Count) throw new ArgumentOutOfRangeException(nameof(number));
        return number == 1 ? "OPENMUSIC_ACCESS_TOKEN" : $"OPENMUSIC_ACCESS_TOKEN{number}";
    }

    public static string DefaultAlias(int number) => number switch
    {
        1 => "goldshoot0720",
        2 => "abuhg17",
        3 => "huang1988pioneer",
        4 => "samafengtu",
        5 => "fengtusama",
        6 => "tushenbyfengbro",
        7 => "fengwithting0831",
        8 => "fengwithfeng1127",
        9 => "fengwithtu1127",
        10 => "akaonda333",
        11 => "fbussinesseng",
        12 => "engdictatorf",
        13 => "fengtuprinfo",
        14 => "flottojackpoteng",
        15 => "feng33feng35feng3",
        16 => "chbondg2",
        _ => string.Empty,
    };
}
