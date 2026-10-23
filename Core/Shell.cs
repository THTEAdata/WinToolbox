using System.Diagnostics;
using System.IO;
using System.Text;

namespace WinToolbox.Core;

/// <summary>命令行执行封装：统一 UTF-8 输出，支持超时与逐行流式回调。</summary>
public static class Shell
{
    public sealed record RunResult(int ExitCode, string Output, bool TimedOut = false);

    /// <summary>把控制台代码页切到 UTF-8 再执行，避免中文输出乱码。</summary>
    private static ProcessStartInfo BuildPsi(string exe, string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c chcp 65001>nul & \"{exe}\" {args}",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        return psi;
    }

    public static async Task<RunResult> RunAsync(string exe, string args, int timeoutMs = 30000,
        CancellationToken ct = default)
    {
        try
        {
            using var p = new Process { StartInfo = BuildPsi(exe, args) };
            p.Start();
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(timeoutMs);
            bool timedOut = false;
            try { await p.WaitForExitAsync(linked.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                timedOut = true;
                try { p.Kill(entireProcessTree: true); } catch { }
            }

            string o = await stdout.ConfigureAwait(false);
            string e = await stderr.ConfigureAwait(false);
            string text = string.IsNullOrWhiteSpace(e) ? o : (o + (o.EndsWith('\n') ? "" : Environment.NewLine) + e);
            return new RunResult(timedOut ? -1 : p.ExitCode, text.TrimEnd(), timedOut);
        }
        catch (Exception ex)
        {
            return new RunResult(-1, "执行失败：" + ex.Message);
        }
    }

    /// <summary>逐行流式执行（用于 tracert / ping 等长时间命令）。返回退出码。</summary>
    public static async Task<int> RunStreamingAsync(string exe, string args, Action<string> onLine,
        CancellationToken ct = default)
    {
        try
        {
            using var p = new Process
            {
                StartInfo = BuildPsi(exe, args),
                EnableRaisingEvents = true
            };
            p.OutputDataReceived += (_, e) => { if (e.Data != null) onLine(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) onLine(e.Data); };
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            using var reg = ct.Register(() => { try { p.Kill(entireProcessTree: true); } catch { } });
            await p.WaitForExitAsync(ct == default ? CancellationToken.None : ct).ConfigureAwait(false);
            return p.ExitCode;
        }
        catch (OperationCanceledException) { return -1; }
        catch (Exception ex)
        {
            onLine("执行失败：" + ex.Message);
            return -1;
        }
    }

    public static void OpenPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (Directory.Exists(path))
            {
                if (Native.OpenFolderShell(path)) return;
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                return;
            }
            if (File.Exists(path))
            {
                if (Native.RevealFileShell(path)) return;
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
        }
        catch { }
    }

    public static void OpenFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
        // Shell API 比直接 Process.Start("explorer.exe") 稳，不会触发 0x142
        if (Native.OpenFolderShell(path)) return;
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch { }
    }

    public static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    /// <summary>只允许安全字符，用于拼接进命令行的主机名 / 域名。</summary>
    public static bool IsSafeHost(string? host)
        => !string.IsNullOrWhiteSpace(host)
           && host.Length <= 253
           && host.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or ':' or '_' or '[' or ']');
}
