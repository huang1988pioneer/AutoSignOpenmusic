using System.ComponentModel;
using System.Diagnostics;

namespace OpenMusicFlow;

internal interface ICommandRunner
{
    Task<string> RunAsync(string executable, IReadOnlyList<string> arguments,
        string? workingDirectory, CancellationToken cancellationToken, string? standardInput = null,
        bool sensitive = false, TimeSpan? timeLimit = null);
}

internal sealed class CommandRunner : ICommandRunner
{
    public async Task<string> RunAsync(string executable, IReadOnlyList<string> arguments,
        string? workingDirectory, CancellationToken cancellationToken, string? standardInput = null,
        bool sensitive = false, TimeSpan? timeLimit = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                CreateNoWindow = true,
            },
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.StartInfo.Environment["GH_PROMPT_DISABLED"] = "1";
        process.StartInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        try
        {
            if (!process.Start()) throw new InvalidOperationException($"無法啟動 {executable}。");
        }
        catch (Win32Exception exception)
        {
            var message = executable == "gh"
                ? "找不到 GitHub CLI（gh）。請安裝後執行 gh auth login，再重新啟動 OpenMusic Flow。"
                : $"無法啟動 {executable}。請確認已安裝並加入 PATH。";
            throw new InvalidOperationException(message, exception);
        }

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeLimit ?? TimeSpan.FromSeconds(45));
        try
        {
            if (standardInput is not null)
                await process.StandardInput.WriteAsync(standardInput.AsMemory(), timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            TryStop(process);
            if (cancellationToken.IsCancellationRequested) throw;
            throw new InvalidOperationException(
                $"{executable} 回應逾時。請檢查網路與 gh auth status；若正在觸發簽到，請先到 Actions 確認是否已送出，再決定是否重試。");
        }
        catch (IOException)
        {
            TryStop(process);
            throw new InvalidOperationException(sensitive
                ? "GitHub Secret 傳輸中斷，請確認 GitHub CLI 登入狀態與寫入權限，再重試。"
                : $"與 {executable} 的通訊中斷，請重新執行。");
        }

        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode == 0) return sensitive ? string.Empty : output;
        if (sensitive)
            throw new InvalidOperationException("GitHub Secret 寫入失敗。請確認 gh auth status、儲存庫名稱，以及此登入身分的 Actions Secrets 寫入權限，再重試。");
        var reason = (string.IsNullOrWhiteSpace(error) ? output : error).Trim();
        if (reason.Length > 1_000) reason = reason[..1_000] + "…";
        throw new InvalidOperationException($"{executable} 執行失敗：{reason}");
    }

    private static void TryStop(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }
    }
}
