using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Perch.Services;

// Named EventWaitHandle is Windows-only. Named pipes work on both Windows and Unix.
internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex mutex;
    private readonly CancellationTokenSource stop = new();
    private readonly string key;
    private Task? listener;
    public bool IsFirst { get; }

    public SingleInstance(string folder)
    {
        var identity = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if (OperatingSystem.IsWindows()) identity = identity.ToUpperInvariant();
        key = "Perch." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..24];
        mutex = new Mutex(false, key);
        try { IsFirst = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { IsFirst = true; }
    }

    public void Listen(Action wake)
    {
        if (!IsFirst) throw new InvalidOperationException("Only the first instance can listen.");
        listener = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    using var pipe = new NamedPipeServerStream(key, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await pipe.WaitForConnectionAsync(stop.Token);
                    var data = new byte[1];
                    if (await pipe.ReadAsync(data, stop.Token) == 1) wake();
                }
                catch (OperationCanceledException) { break; }
                catch (IOException e) { Log.Warn("Second-start notification failed", e); }
            }
        });
    }

    public void Wake()
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", key, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            pipe.Connect(3000);
            pipe.WriteByte(1);
        }
        catch (Exception e) when (e is IOException or TimeoutException) { Log.Warn("Could not wake the running instance", e); }
    }

    public void Dispose()
    {
        stop.Cancel();
        listener?.GetAwaiter().GetResult();
        stop.Dispose();
        if (IsFirst) mutex.ReleaseMutex();
        mutex.Dispose();
    }
}
