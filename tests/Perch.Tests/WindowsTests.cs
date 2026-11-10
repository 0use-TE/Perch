#if WINDOWS
using System.Text.Json;
using Perch.Platform.Windows;
using Perch.Services.Ai.Agent;

namespace Perch.Tests;

internal static class WindowsTests
{
    private static JsonElement Args(string script)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string, object> { ["script"] = script, ["timeout_seconds"] = 60 }));
        return document.RootElement.Clone();
    }

    public static async Task Run()
    {
        await T.Group("Windows: PowerShell tool", async () =>
        {
            var tool = new PowerShellTool();
            var small = await tool.RunAsync(Args("Write-Output ('中文 ' + (1+1))"), default);
            T.Check(small.Content.Contains("Exit code: 0") && small.Content.Contains("中文 2"), "a short script runs and its Chinese output comes back");

            // About 60 KB of script: far over what fits on a command line once encoded, so it goes through a file.
            var big = "Write-Output 'start'\n# " + new string('x', 60000) + "\nWrite-Output 'end'";
            var result = await tool.RunAsync(Args(big), default);
            T.Check(result.Content.Contains("Exit code: 0") && result.Content.Contains("start") && result.Content.Contains("end"), "a very long script still runs");
            T.Check(!Directory.GetFiles(Path.GetTempPath(), "perch-*.ps1").Any(), "and its temporary file is removed");

            var failing = await tool.RunAsync(Args("exit 7"), default);
            T.Check(failing.Content.Contains("Exit code: 7"), "the exit code is reported");
            var slow = await tool.RunAsync(JsonDocument.Parse("""{"script":"Start-Sleep 20","timeout_seconds":1}""").RootElement, default);
            T.Check(slow.Content.Contains("timeout"), "a script that runs too long is stopped");
            await T.Throws<ArgumentException>(() => tool.RunAsync(Args("  "), default), "an empty script is refused");
        });

        await T.Group("Windows: user commands", async () =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "perch cmd test " + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                var script = Path.Combine(folder, "write it.cmd");
                File.WriteAllText(script, "@echo %~1>\"%~dp0out.txt\"\r\n");
                // A quoted program path with spaces followed by a quoted argument: cmd's quote stripping used to break this.
                T.Check(ShellActions.Run($"\"{script}\" \"hello world\""), "the command starts");
                var output = Path.Combine(folder, "out.txt");
                for (var i = 0; i < 80 && !File.Exists(output); i++) await Task.Delay(100);
                await Task.Delay(200);
                T.Eq("hello world", File.Exists(output) ? File.ReadAllText(output).Trim() : "(not written)", "a quoted path and a quoted argument both arrive intact");
            }
            finally { try { Directory.Delete(folder, true); } catch (IOException) { } }
        });

        await T.Group("Windows: battery", () =>
        {
            var reading = PowerInfo.Read();
            T.Check(reading.Percent is >= 0 and <= 100, "battery percent is a percentage");
            return Task.CompletedTask;
        });
    }
}
#endif
