using System.Net;
using System.Text;
using System.Text.Json;

namespace Perch.Tests;

// A tiny check runner: no test framework, every failure is listed at the end and the exit code is the number of failures.
internal static class T
{
    private static int passed;
    public static readonly List<string> Failures = [];
    public static int Passed => passed;

    public static void Check(bool ok, string name)
    {
        if (ok) { passed++; Console.WriteLine("PASS " + name); }
        else { Failures.Add(name); Console.WriteLine("FAIL " + name); }
    }

    public static void Eq<TValue>(TValue expected, TValue actual, string name)
    {
        var ok = EqualityComparer<TValue>.Default.Equals(expected, actual);
        Check(ok, ok ? name : $"{name} (expected '{expected}', got '{actual}')");
    }

    // Runs one group of checks; an exception counts as one failure and the other groups still run.
    public static async Task Group(string title, Func<Task> body)
    {
        Console.WriteLine("== " + title);
        try { await body(); }
        catch (Exception e)
        {
            Failures.Add($"{title}: {e.GetType().Name}: {e.Message}");
            Console.WriteLine($"FAIL {title}: {e}");
        }
    }

    public static async Task Throws<TException>(Func<Task> action, string name) where TException : Exception
    {
        try { await action(); Check(false, name + " (nothing was thrown)"); }
        catch (TException) { Check(true, name); }
        catch (Exception e) { Check(false, $"{name} (threw {e.GetType().Name}: {e.Message})"); }
    }
}

// A local OpenAI-compatible server for the AI client and agent checks. Each request is answered by the next scripted step
// (the last step repeats); every request body is kept so a check can see what Perch sent.
internal sealed class MockAi : IDisposable
{
    private readonly HttpListener listener = new();
    private readonly Func<int, HttpListenerContext, Task>[] steps;
    private int count;
    public List<string> Bodies { get; } = [];
    public string Url { get; }
    public int Requests => Volatile.Read(ref count);

    public MockAi(params Func<int, HttpListenerContext, Task>[] steps)
    {
        this.steps = steps;
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        Url = $"http://127.0.0.1:{port}/v1";
        _ = Loop();
    }

    private async Task Loop()
    {
        while (listener.IsListening)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync(); }
            catch (Exception) { break; }
            _ = Handle(context);
        }
    }

    private async Task Handle(HttpListenerContext context)
    {
        try
        {
            var body = await new StreamReader(context.Request.InputStream, Encoding.UTF8).ReadToEndAsync();
            lock (Bodies) Bodies.Add(body);
            var index = Interlocked.Increment(ref count) - 1;
            await steps[Math.Min(index, steps.Length - 1)](index, context);
        }
        catch (Exception) { /* the client went away */ }
        finally { try { context.Response.Close(); } catch (Exception) { } }
    }

    public void Dispose() { try { listener.Close(); } catch (Exception) { } }

    // ---- scripted answers

    public static Func<int, HttpListenerContext, Task> Reply(params string[] events) => async (_, context) => await Write(context, events);

    public static Func<int, HttpListenerContext, Task> Fail(int status, string body) => async (_, context) =>
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        var bytes = Encoding.UTF8.GetBytes(body);
        await context.Response.OutputStream.WriteAsync(bytes);
    };

    // One chunk, then silence for longer than any check waits.
    public static Func<int, HttpListenerContext, Task> Stall(string firstEvent) => async (_, context) =>
    {
        await Write(context, firstEvent);
        await Task.Delay(8000);
    };

    private static async Task Write(HttpListenerContext context, params string[] events)
    {
        context.Response.ContentType = "text/event-stream";
        context.Response.SendChunked = true;
        foreach (var e in events)
        {
            var bytes = Encoding.UTF8.GetBytes("data: " + e + "\n\n");
            await context.Response.OutputStream.WriteAsync(bytes);
            await context.Response.OutputStream.FlushAsync();
        }
    }

    // ---- events as the server would send them

    public const string Done = "[DONE]";
    public static string Json(string text) => JsonSerializer.Serialize(text);
    public static string Text(string content) => """{"choices":[{"delta":{"content":""" + Json(content) + """}}]}""";
    public static string Thought(string content) => """{"choices":[{"delta":{"reasoning_content":""" + Json(content) + """}}]}""";
    public static string Finish(string reason) => """{"choices":[{"delta":{},"finish_reason":""" + Json(reason) + """}]}""";
    public static string CallStart(string id, string name) =>
        """{"choices":[{"delta":{"tool_calls":[{"index":0,"id":""" + Json(id) + ""","type":"function","function":{"name":""" + Json(name) + ""","arguments":""}}]}}]}""";
    public static string CallArguments(string piece) =>
        """{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":""" + Json(piece) + """}}]}}]}""";
}
