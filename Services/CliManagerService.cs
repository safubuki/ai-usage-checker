using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AIUsageChecker.Models;

namespace AIUsageChecker.Services;

public class CliManagerService
{
    public event Action<string>? LogOutputReceived;

    private readonly string _userProfile;
    private readonly string _npmGlobalPath;
    private readonly string _grokBinPath;

    public CliManagerService()
    {
        _userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _npmGlobalPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm");
        _grokBinPath = Path.Combine(_userProfile, ".grok", "bin");
    }

    private void Log(string message)
    {
        LogOutputReceived?.Invoke($"[{DateTime.Now:HH:mm:ss}] {message}");
    }

    public async Task CheckCliStatusAsync(CliInfo cli)
    {
        cli.IsBusy = true;
        cli.StatusMessage = "状態を確認中...";
        Log($"{cli.Name} の状態を確認中...");

        try
        {
            // 1. 実行ファイルの探索
            var exePath = FindExecutable(cli.CommandName);
            if (!string.IsNullOrEmpty(exePath))
            {
                cli.IsInstalled = true;
                cli.ExecutablePath = exePath;

                // バージョンの取得
                var version = await GetInstalledVersionAsync(cli.CommandName);
                cli.InstalledVersion = version;
                Log($"{cli.Name} インストール済み: {version} ({exePath})");

                // 最新バージョンの取得
                var latest = await GetLatestVersionAsync(cli);
                if (!string.IsNullOrEmpty(latest))
                {
                    cli.LatestVersion = latest;
                    cli.HasUpdate = CompareVersions(cli.InstalledVersion, latest);
                    if (cli.HasUpdate)
                    {
                        cli.StatusMessage = $"更新があります (最新: v{latest})";
                        Log($"{cli.Name} に新しいバージョンがあります: {latest}");
                    }
                    else
                    {
                        cli.StatusMessage = "最新バージョンです";
                    }
                }
                else
                {
                    cli.StatusMessage = "最新バージョン確認完了";
                }

                // 各AIツールのログイン状態の確認
                if (cli.CommandName.Equals("codex", StringComparison.OrdinalIgnoreCase))
                {
                    cli.IsLoggedIn = CodexQuotaClient.IsAuthFileExists();
                    if (!cli.IsLoggedIn)
                    {
                        cli.StatusMessage = "未ログイン ('codex login' が必要)";
                        Log($"{cli.Name} は未ログイン状態です (~/.codex/auth.json 未検出)");
                    }
                }
                else if (cli.CommandName.Equals("claude", StringComparison.OrdinalIgnoreCase))
                {
                    cli.IsLoggedIn = ClaudeQuotaClient.IsConfigExists();
                    if (!cli.IsLoggedIn)
                    {
                        cli.StatusMessage = "未ログイン ('claude login' が必要)";
                        Log($"{cli.Name} は未ログイン状態です (~/.claude.json 未検出)");
                    }
                }
                else if (cli.CommandName.Equals("grok", StringComparison.OrdinalIgnoreCase))
                {
                    cli.IsLoggedIn = GrokQuotaClient.IsAuthFileExists();
                    if (!cli.IsLoggedIn)
                    {
                        cli.StatusMessage = "未ログイン ('grok' 認証が必要)";
                        Log($"{cli.Name} は未ログイン状態です (~/.grok/auth.json 未検出)");
                    }
                }
                else if (cli.CommandName.Equals("copilot", StringComparison.OrdinalIgnoreCase))
                {
                    cli.IsLoggedIn = CopilotQuotaClient.IsGhAuth();
                    if (!cli.IsLoggedIn)
                    {
                        cli.StatusMessage = "未ログイン ('gh auth login' が必要)";
                        Log($"{cli.Name} は未ログイン状態です ('gh auth status' 未認証)");
                    }
                }
                else if (cli.CommandName.Equals("gemini", StringComparison.OrdinalIgnoreCase))
                {
                    cli.IsLoggedIn = true;
                }
            }
            else
            {
                cli.IsInstalled = false;
                cli.IsLoggedIn = false;
                cli.InstalledVersion = "";
                cli.ExecutablePath = "";
                cli.HasUpdate = false;
                cli.StatusMessage = "未インストール";
                Log($"{cli.Name} はインストールされていません");

                // 未インストールでも最新バージョン情報を取得
                var latest = await GetLatestVersionAsync(cli);
                if (!string.IsNullOrEmpty(latest))
                {
                    cli.LatestVersion = latest;
                }
            }
        }
        catch (Exception ex)
        {
            cli.StatusMessage = $"確認失敗: {ex.Message}";
            Log($"{cli.Name} の確認中にエラー: {ex.Message}");
        }
        finally
        {
            cli.IsBusy = false;
        }
    }

    public async Task<bool> LoginCliAsync(CliInfo cli)
    {
        cli.IsBusy = true;
        cli.StatusMessage = "ログイン認証を開始中...";
        Log($"=== {cli.Name} のログイン認証を開始します ===");

        try
        {
            if (cli.CommandName.Equals("codex", StringComparison.OrdinalIgnoreCase))
            {
                Log("[Codex] ブラウザでChatGPT認証画面を開きます。承認を完了してください...");
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c start \"Codex Login\" cmd.exe /k \"codex login\"",
                    UseShellExecute = true
                };
                Process.Start(psi);

                // 最大60秒間、auth.json の生成をポーリング検知
                for (int i = 0; i < 60; i++)
                {
                    await Task.Delay(1000);
                    if (CodexQuotaClient.IsAuthFileExists())
                    {
                        Log("[Codex] ログイン認証の完了を検知しました！");
                        cli.IsLoggedIn = true;
                        cli.StatusMessage = "ログイン完了";
                        return true;
                    }
                }

                Log("[Codex] ログイン待機がタイムアウトしました。ブラウザまたは開いたコンソールウィンドウで認証を完了してください。");
                cli.StatusMessage = "ログイン待機タイムアウト (要確認)";
                return false;
            }
            else if (cli.CommandName.Equals("claude", StringComparison.OrdinalIgnoreCase))
            {
                Log("[Claude] ブラウザでAnthropic認証画面を開きます。承認を完了してください...");
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c start \"Claude Login\" cmd.exe /k \"claude login\"",
                    UseShellExecute = true
                };
                Process.Start(psi);

                for (int i = 0; i < 60; i++)
                {
                    await Task.Delay(1000);
                    if (ClaudeQuotaClient.IsConfigExists())
                    {
                        Log("[Claude] ログイン認証の完了を検知しました！");
                        cli.IsLoggedIn = true;
                        cli.StatusMessage = "ログイン完了";
                        return true;
                    }
                }
                return true;
            }
            else if (cli.CommandName.Equals("grok", StringComparison.OrdinalIgnoreCase))
            {
                Log("[Grok] xAI認証コンソールを起動します...");
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c start \"Grok Login\" cmd.exe /k \"grok auth login\"",
                    UseShellExecute = true
                };
                Process.Start(psi);

                for (int i = 0; i < 60; i++)
                {
                    await Task.Delay(1000);
                    if (GrokQuotaClient.IsAuthFileExists())
                    {
                        Log("[Grok] ログイン認証の完了を検知しました！");
                        cli.IsLoggedIn = true;
                        cli.StatusMessage = "ログイン完了";
                        return true;
                    }
                }
                return true;
            }
            else if (cli.CommandName.Equals("copilot", StringComparison.OrdinalIgnoreCase))
            {
                Log("[Copilot] GitHub認証コンソールを起動します (ブラウザで認証)...");
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c start \"GitHub Login\" cmd.exe /k \"gh auth login -w -p https\"",
                    UseShellExecute = true
                };
                Process.Start(psi);

                for (int i = 0; i < 60; i++)
                {
                    await Task.Delay(1500);
                    if (CopilotQuotaClient.IsGhAuth())
                    {
                        Log("[Copilot] GitHubログイン認証の完了を検知しました！");
                        cli.IsLoggedIn = true;
                        cli.StatusMessage = "ログイン完了";
                        return true;
                    }
                }
                return true;
            }
            else if (cli.CommandName.Equals("gemini", StringComparison.OrdinalIgnoreCase))
            {
                Log("[Gemini] Gemini認証コンソールを起動します...");
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c start \"Gemini Login\" cmd.exe /k \"gemini login\"",
                    UseShellExecute = true
                };
                Process.Start(psi);
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            cli.StatusMessage = $"ログイン起動失敗: {ex.Message}";
            Log($"[エラー] {cli.Name} のログイン起動中にエラー: {ex.Message}");
            return false;
        }
        finally
        {
            cli.IsBusy = false;
        }
    }

    public async Task<bool> InstallCliAsync(CliInfo cli)
    {
        cli.IsBusy = true;
        cli.StatusMessage = "インストール中...";
        Log($"=== {cli.Name} のインストールを開始します ({cli.PackageName}) ===");

        try
        {
            string command;
            string args;

            if (cli.CommandName == "grok")
            {
                // grokのインストーラ
                command = "powershell.exe";
                args = "-NoProfile -ExecutionPolicy Bypass -Command \"irm https://grok.x.ai/install.ps1 | iex\"";
            }
            else
            {
                // npm パッケージ
                command = "cmd.exe";
                args = $"/c npm install -g {cli.PackageName}@latest";
            }

            var result = await RunProcessAsync(command, args);
            if (result.ExitCode == 0)
            {
                Log($"{cli.Name} のインストールが完了しました！");
                await CheckCliStatusAsync(cli);
                return true;
            }
            else
            {
                cli.StatusMessage = "インストールに失敗しました";
                Log($"[エラー] {cli.Name} のインストールに失敗しました (終了コード: {result.ExitCode}): {result.Error}");
                return false;
            }
        }
        catch (Exception ex)
        {
            cli.StatusMessage = $"インストール例外: {ex.Message}";
            Log($"[例外] {cli.Name} のインストール中に例外: {ex.Message}");
            return false;
        }
        finally
        {
            cli.IsBusy = false;
        }
    }

    public async Task<bool> UpdateCliAsync(CliInfo cli)
    {
        cli.IsBusy = true;
        cli.StatusMessage = "アップデート中...";
        Log($"=== {cli.Name} のアップデートを開始します ===");

        try
        {
            string command;
            string args;

            if (cli.CommandName == "grok")
            {
                command = "cmd.exe";
                args = "/c grok update";
            }
            else if (cli.CommandName == "copilot")
            {
                command = "cmd.exe";
                args = "/c copilot update";
            }
            else
            {
                command = "cmd.exe";
                args = $"/c npm install -g {cli.PackageName}@latest";
            }

            var result = await RunProcessAsync(command, args);
            if (result.ExitCode == 0)
            {
                Log($"{cli.Name} のアップデートが完了しました！");
                await CheckCliStatusAsync(cli);
                return true;
            }
            else
            {
                cli.StatusMessage = "アップデート失敗";
                Log($"[エラー] {cli.Name} のアップデートに失敗 (終了コード: {result.ExitCode}): {result.Error}");
                return false;
            }
        }
        catch (Exception ex)
        {
            cli.StatusMessage = $"アップデート例外: {ex.Message}";
            Log($"[例外] {cli.Name} のアップデート中に例外: {ex.Message}");
            return false;
        }
        finally
        {
            cli.IsBusy = false;
        }
    }

    private string? FindExecutable(string commandName)
    {
        // 1. 典型的なパスの直接チェック (高速: 数ミリ秒)
        var possiblePaths = new[]
        {
            Path.Combine(_npmGlobalPath, $"{commandName}.cmd"),
            Path.Combine(_npmGlobalPath, $"{commandName}.ps1"),
            Path.Combine(_npmGlobalPath, $"{commandName}"),
            Path.Combine(_grokBinPath, $"{commandName}.exe"),
            Path.Combine(_grokBinPath, $"{commandName}")
        };

        foreach (var path in possiblePaths)
        {
            if (File.Exists(path))
            {
                return path;
            }
        }

        // 2. PATHからの探索 (where.exe)
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "where.exe",
                Arguments = commandName,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p != null)
            {
                var stdout = p.StandardOutput.ReadToEnd();
                p.WaitForExit(1000);
                if (p.ExitCode == 0)
                {
                    var lines = stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    if (lines.Length > 0 && File.Exists(lines[0]))
                    {
                        return lines[0];
                    }
                }
            }
        }
        catch { }

        return null;
    }

    private async Task<string> GetInstalledVersionAsync(string commandName)
    {
        try
        {
            var result = await RunProcessAsync("cmd.exe", $"/c {commandName} --version");
            if (result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.Output))
            {
                var match = Regex.Match(result.Output, @"\d+\.\d+(\.\d+)?(-[a-zA-Z0-9.]+)?");
                if (match.Success)
                {
                    return match.Value;
                }
                return result.Output.Trim().Split('\n')[0];
            }
        }
        catch { }
        return "導入済";
    }

    private async Task<string> GetLatestVersionAsync(CliInfo cli)
    {
        if (cli.CommandName == "grok")
        {
            return ""; // grok update で自己判定
        }

        try
        {
            var result = await RunProcessAsync("cmd.exe", $"/c npm view {cli.PackageName} version");
            if (result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.Output))
            {
                return result.Output.Trim();
            }
        }
        catch { }
        return "";
    }

    private bool CompareVersions(string installed, string latest)
    {
        if (string.IsNullOrWhiteSpace(installed) || string.IsNullOrWhiteSpace(latest))
            return false;

        var v1Match = Regex.Match(installed, @"^\d+(\.\d+)*");
        var v2Match = Regex.Match(latest, @"^\d+(\.\d+)*");

        if (v1Match.Success && v2Match.Success)
        {
            if (Version.TryParse(v1Match.Value, out var v1) && Version.TryParse(v2Match.Value, out var v2))
            {
                return v2 > v1;
            }
        }
        return false;
    }

    private async Task<(int ExitCode, string Output, string Error)> RunProcessAsync(string fileName, string args)
    {
        var tcs = new TaskCompletionSource<(int, string, string)>();

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                stdout.AppendLine(e.Data);
                Log(e.Data);
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                stderr.AppendLine(e.Data);
                Log($"[stderr] {e.Data}");
            }
        };

        process.Exited += (_, _) =>
        {
            tcs.TrySetResult((process.ExitCode, stdout.ToString(), stderr.ToString()));
            process.Dispose();
        };

        if (!process.Start())
        {
            return (-1, "", "Failed to start process");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        return await tcs.Task;
    }
}
