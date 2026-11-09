using System.Diagnostics;
using System.Threading;
using Perch.Services;
using Perch.Services.Sounds;

namespace Perch.Platform.Linux;

internal sealed class LinuxSoundPlayer : ISoundPlayer
{
    private CancellationTokenSource? playback;
    public void Play(byte[] wav, bool loop)
    {
        Stop();
        var cancellation = new CancellationTokenSource();
        playback = cancellation;
        _ = Task.Run(async () =>
        {
            try
            {
                do
                {
                    using var process = new Process { StartInfo = new ProcessStartInfo("aplay") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardError = true } };
                    process.StartInfo.ArgumentList.Add("-q");
                    process.Start();
                    using var stop = cancellation.Token.Register(() => { try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { } });
                    var error = process.StandardError.ReadToEndAsync();
                    await process.StandardInput.BaseStream.WriteAsync(wav, cancellation.Token);
                    process.StandardInput.Close();
                    await process.WaitForExitAsync(cancellation.Token);
                    await error;
                    if (process.ExitCode != 0) break;
                } while (loop && !cancellation.IsCancellationRequested);
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { if (!cancellation.IsCancellationRequested) Log.Warn("Could not play a sound (alsa-utils is required)", e); }
            finally { cancellation.Dispose(); }
        });
    }
    public void Stop()
    {
        var previous = Interlocked.Exchange(ref playback, null);
        try { previous?.Cancel(); } catch (ObjectDisposedException) { }
    }
    public void SystemBeep() => Play(SoundBank.Wav("gentle", (Audio.Settings?.SoundVolume ?? 70) / 100.0, 0), false);
}
