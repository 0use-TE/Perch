using System.Diagnostics;
using System.Threading;

namespace Perch.Platform.Linux;

internal static class LinuxProcess
{
    // Arguments never go through a shell. stdin is used for secrets, so they do not enter /proc/<pid>/cmdline.
    internal static async Task<string?> ReadAsync(string executable, string[] args, string? input = null, CancellationToken cancellation = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var process = new Process { StartInfo = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = input != null } };
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        try
        {
            if (!process.Start()) return null;
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            if (input != null) { await process.StandardInput.WriteAsync(input.AsMemory(), timeout.Token).ConfigureAwait(false); process.StandardInput.Close(); }
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await error.ConfigureAwait(false);
            return process.ExitCode == 0 ? (await output.ConfigureAwait(false)).TrimEnd('\r', '\n') : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or System.IO.IOException or OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
            return null;
        }
    }
}
