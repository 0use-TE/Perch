using System.Net;
using System.Text;
using System.Text.Json;
using Perch.Services.Ai;
using Perch.Services.Ai.Agent;

namespace Perch.Tests;

internal static class ClientTests
{
    private static readonly ChatMessage[] Hello = [new("user", "hi")];

    private sealed class Collected
    {
        public readonly StringBuilder Text = new(), Thinking = new();
        public string Finish = "(none)";
    }

    private static async Task<(List<ToolCall> Calls, Collected Seen)> Ask(MockAi server, IReadOnlyList<ToolSpec>? tools = null, CancellationToken cancel = default)
    {
        var seen = new Collected();
        var calls = await OpenAiClient.StreamAsync(server.Url, "key", "model", Hello, p => seen.Text.Append(p), p => seen.Thinking.Append(p), cancel, tools, f => seen.Finish = f);
        return (calls, seen);
    }

    public static async Task Run()
    {
        await T.Group("AI client: addresses", () =>
        {
            T.Eq("https://api.openai.com/v1", OpenAiClient.Normalize("api.openai.com/v1/"), "address gets https and loses the trailing slash");
            T.Eq("http://localhost:11434/v1", OpenAiClient.Normalize(" http://localhost:11434/v1/ "), "address keeps its own scheme");
            T.Check(OpenAiClient.IsLocal("http://localhost:11434/v1"), "localhost is a local server");
            T.Check(OpenAiClient.IsLocal("127.0.0.1:1234/v1"), "127.0.0.1 is a local server");
            T.Check(!OpenAiClient.IsLocal("https://api.deepseek.com"), "a public server is not local");
            return Task.CompletedTask;
        });

        await T.Group("AI client: streaming", async () =>
        {
            using (var server = new MockAi(MockAi.Reply(MockAi.Thought("hmm "), MockAi.Thought("ok"), MockAi.Text("Hello"), MockAi.Text(" world"), MockAi.Finish("stop"), MockAi.Done)))
            {
                var (calls, seen) = await Ask(server);
                T.Eq("Hello world", seen.Text.ToString(), "answer text is assembled");
                T.Eq("hmm ok", seen.Thinking.ToString(), "thinking arrives separately");
                T.Eq("stop", seen.Finish, "the stop reason is reported");
                T.Eq(0, calls.Count, "no tool calls");
                using var body = JsonDocument.Parse(server.Bodies[0]);
                T.Eq("model", body.RootElement.GetProperty("model").GetString(), "the model is sent");
                T.Check(body.RootElement.GetProperty("stream").GetBoolean(), "streaming is requested");
                T.Eq("hi", body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString(), "the question is sent");
                T.Check(!body.RootElement.TryGetProperty("tools", out _), "no tools property when none are offered");
            }

            using (var server = new MockAi(MockAi.Reply(MockAi.CallStart("call_9", "echo"), MockAi.CallArguments("{\"te"), MockAi.CallArguments("xt\":\"hi\"}"), MockAi.Finish("tool_calls"), MockAi.Done)))
            {
                var spec = new ToolSpec("echo", "Echo", """{"type":"object","properties":{"text":{"type":"string"}}}""");
                var (calls, seen) = await Ask(server, [spec]);
                T.Eq(1, calls.Count, "one tool call is assembled from pieces");
                T.Eq(("call_9", "echo", "{\"text\":\"hi\"}"), (calls[0].Id, calls[0].Name, calls[0].Arguments), "tool call id, name and arguments");
                T.Eq("tool_calls", seen.Finish, "stop reason of a tool call");
                using var body = JsonDocument.Parse(server.Bodies[0]);
                T.Eq("echo", body.RootElement.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString(), "the tool is offered in the request");
            }

            using (var server = new MockAi(MockAi.Reply(MockAi.Text("cut"), MockAi.Finish("length"), MockAi.Done)))
            {
                var (_, seen) = await Ask(server);
                T.Eq("length", seen.Finish, "a reply cut by the length limit says so");
            }

            using (var server = new MockAi(MockAi.Reply(MockAi.Text("no done marker"), MockAi.Finish("stop"))))
            {
                var (_, seen) = await Ask(server);
                T.Eq("no done marker", seen.Text.ToString(), "a stream with a stop reason but no [DONE] is complete");
            }

            using (var server = new MockAi(MockAi.Reply("{not json", MockAi.Text("still here"), MockAi.Finish("stop"), MockAi.Done)))
            {
                var (_, seen) = await Ask(server);
                T.Eq("still here", seen.Text.ToString(), "a damaged event is skipped");
            }
        });

        await T.Group("AI client: failures", async () =>
        {
            using (var server = new MockAi(MockAi.Reply(MockAi.Text("half"))))
            {
                var ex = await Assert<AiException>(() => Ask(server), "a stream that ends without [DONE] or a stop reason is an error");
                T.Check(ex != null, "...with a message");
            }

            using (var server = new MockAi(MockAi.Reply(MockAi.Text("a"), """{"error":{"message":"overloaded"}}""")))
            {
                var ex = await Assert<AiException>(() => Ask(server), "an error event in the middle of a stream is an error");
                T.Eq("overloaded", ex?.Message, "the server's own words are kept");
            }

            using (var server = new MockAi(MockAi.Fail(401, """{"error":{"message":"bad key"}}""")))
            {
                var ex = await Assert<AiException>(() => Ask(server), "401 is an error");
                T.Eq((HttpStatusCode.Unauthorized, "bad key"), (ex!.Status!.Value, ex.Message), "status and the server's explanation");
                T.Eq(1, server.Requests, "a 401 is not retried");
            }

            using (var server = new MockAi(MockAi.Fail(503, "busy"), MockAi.Reply(MockAi.Text("ok"), MockAi.Finish("stop"), MockAi.Done)))
            {
                var (_, seen) = await Ask(server);
                T.Eq("ok", seen.Text.ToString(), "a 503 is retried once and then succeeds");
                T.Eq(2, server.Requests, "two requests were made");
            }

            using (var server = new MockAi(MockAi.Fail(503, "busy")))
            {
                var ex = await Assert<AiException>(() => Ask(server), "a 503 that keeps happening is an error");
                T.Eq(2, server.Requests, "retried only once");
                T.Eq(HttpStatusCode.ServiceUnavailable, ex?.Status, "status is kept");
            }

            var oldIdle = OpenAiClient.StreamIdleLimit;
            OpenAiClient.StreamIdleLimit = TimeSpan.FromMilliseconds(700);
            try
            {
                using var server = new MockAi(MockAi.Stall(MockAi.Text("start")));
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var ex = await Assert<AiException>(() => Ask(server), "a stalled stream is given up");
                T.Check(clock.Elapsed < TimeSpan.FromSeconds(5), "...promptly, not after the server's own delay");
                T.Eq<HttpStatusCode?>(null, ex?.Status, "a timeout has no HTTP status");
            }
            finally { OpenAiClient.StreamIdleLimit = oldIdle; }

            using (var server = new MockAi(MockAi.Stall(MockAi.Text("start"))))
            {
                using var stop = new CancellationTokenSource(400);
                await T.Throws<OperationCanceledException>(() => Ask(server, null, stop.Token), "the user's Stop cancels instead of reporting a failure");
            }

            var dead = new MockAi(MockAi.Reply(MockAi.Done));
            var url = dead.Url;
            dead.Dispose();
            await Task.Delay(100);
            await T.Throws<HttpRequestException>(() => OpenAiClient.StreamAsync(url, "", "m", Hello, _ => { }, _ => { }, default), "a server that is not there fails to connect");
        });
    }

    private static async Task<TException?> Assert<TException>(Func<Task> action, string name) where TException : Exception
    {
        try { await action(); T.Check(false, name + " (nothing was thrown)"); return null; }
        catch (TException e) { T.Check(true, name); return e; }
        catch (Exception e) { T.Check(false, $"{name} (threw {e.GetType().Name}: {e.Message})"); return null; }
    }
}

// What the agent shows while it works, kept for the checks.
internal sealed class FakeObserver : IAgentObserver
{
    public readonly StringBuilder Shown = new(), Thought = new();
    public readonly List<string> Steps = [], Summaries = [];
    public bool WasTruncated, Allow = true;
    public int Asked;
    public void Text(string piece) => Shown.Append(piece);
    public void Thinking(string piece) => Thought.Append(piece);
    public object StepStarted(string label) { Steps.Add(label); return label; }
    public void StepDone(object step, string summary, bool failed) => Summaries.Add((failed ? "failed: " : "") + summary);
    public Task<bool> ConfirmAsync(string title, string detail, ToolRisk risk, CancellationToken cancel) { Asked++; return Task.FromResult(Allow); }
    public void Truncated() => WasTruncated = true;
}

internal sealed class EchoTool(ToolRisk risk = ToolRisk.Safe) : ITool
{
    public readonly List<string> Ran = [];
    public ToolSpec Spec { get; } = new("echo", "Echo the text back.", """{"type":"object","properties":{"text":{"type":"string"}},"required":["text"]}""");
    public ToolRisk Risk { get; } = risk;
    public string Describe(JsonElement arguments) => "Echo " + Agent.Str(arguments, "text");
    public Task<ToolResult> RunAsync(JsonElement arguments, CancellationToken cancel)
    {
        var text = Agent.Str(arguments, "text") ?? "";
        Ran.Add(text);
        return Task.FromResult(new ToolResult("echoed: " + text, "ok"));
    }
}

internal static class AgentTests
{
    private static readonly ChatMessage[] Question = [new("user", "do it")];

    private static async Task<FakeObserver> Go(MockAi server, EchoTool tool, FakeObserver? observer = null)
    {
        observer ??= new FakeObserver();
        await Agent.RunAsync(server.Url, "key", "model", Question, [tool], observer, default);
        return observer;
    }

    private static string ToolBody(string body)
    {
        using var json = JsonDocument.Parse(body);
        return string.Join("|", json.RootElement.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("role").GetString() + ":" + (m.TryGetProperty("content", out var c) ? c.GetString() : "")));
    }

    public static async Task Run()
    {
        await T.Group("Agent: tool calls", async () =>
        {
            var tool = new EchoTool();
            using (var server = new MockAi(
                MockAi.Reply(MockAi.Text("Checking. "), MockAi.CallStart("c1", "echo"), MockAi.CallArguments("{\"text\":\"one\"}"), MockAi.Finish("tool_calls"), MockAi.Done),
                MockAi.Reply(MockAi.Text("All done."), MockAi.Finish("stop"), MockAi.Done)))
            {
                var seen = await Go(server, tool);
                T.Eq("one", string.Join(",", tool.Ran), "the tool ran with the model's arguments");
                T.Eq("Checking. \n\nAll done.", seen.Shown.ToString(), "text before a tool stays apart from the answer after it");
                T.Eq(2, server.Requests, "the model was asked again with the result");
                T.Check(ToolBody(server.Bodies[1]).Contains("tool:echoed: one"), "the tool result went back to the model");
                T.Eq("Echo one", seen.Steps[0], "the step is described for the person watching");
            }

            tool = new EchoTool();
            using (var server = new MockAi(MockAi.Reply(MockAi.CallStart("c1", "nope"), MockAi.CallArguments("{}"), MockAi.Finish("tool_calls"), MockAi.Done),
                                           MockAi.Reply(MockAi.Text("sorry"), MockAi.Finish("stop"), MockAi.Done)))
            {
                var seen = await Go(server, tool);
                T.Check(seen.Summaries.Any(s => s.StartsWith("failed:")), "an unknown tool is reported as a failed step");
                T.Check(ToolBody(server.Bodies[1]).Contains("There is no tool named nope"), "and the model is told");
            }

            tool = new EchoTool(ToolRisk.Dangerous);
            using (var server = new MockAi(MockAi.Reply(MockAi.CallStart("c1", "echo"), MockAi.CallArguments("{\"text\":\"x\"}"), MockAi.Finish("tool_calls"), MockAi.Done),
                                           MockAi.Reply(MockAi.Text("ok, not done"), MockAi.Finish("stop"), MockAi.Done)))
            {
                var seen = await Go(server, tool, new FakeObserver { Allow = false });
                T.Eq(1, seen.Asked, "a dangerous tool asks first");
                T.Eq(0, tool.Ran.Count, "and does not run when declined");
                T.Check(ToolBody(server.Bodies[1]).Contains("declined"), "the model is told it was declined");
            }

            tool = new EchoTool(ToolRisk.Dangerous);
            using (var server = new MockAi(MockAi.Reply(MockAi.CallStart("c1", "echo"), MockAi.CallArguments("{\"text\":\"x\"}"), MockAi.Finish("tool_calls"), MockAi.Done),
                                           MockAi.Reply(MockAi.Text("done"), MockAi.Finish("stop"), MockAi.Done)))
            {
                var seen = await Go(server, tool);
                T.Eq(1, tool.Ran.Count, "a dangerous tool runs once allowed");
            }

            tool = new EchoTool();
            using (var server = new MockAi(MockAi.Reply(MockAi.CallStart("c1", "echo"), MockAi.CallArguments("{broken"), MockAi.Finish("tool_calls"), MockAi.Done),
                                           MockAi.Reply(MockAi.Text("retrying"), MockAi.Finish("stop"), MockAi.Done)))
            {
                await Go(server, tool);
                T.Eq(0, tool.Ran.Count, "arguments that are not JSON run nothing");
                T.Check(ToolBody(server.Bodies[1]).Contains("not valid JSON"), "and the model is told why");
            }

            tool = new EchoTool();
            using (var server = new MockAi(MockAi.Reply(MockAi.CallStart("c1", "echo"), MockAi.CallArguments("{\"text\":\"again\"}"), MockAi.Finish("tool_calls"), MockAi.Done)))
            {
                await Go(server, tool);
                T.Eq(Agent.MaxRounds, server.Requests, "a model that never stops calling tools is cut off at the round limit");
                using var last = JsonDocument.Parse(server.Bodies[^1]);
                T.Check(!last.RootElement.TryGetProperty("tools", out _), "the last round offers no tools, so the model has to answer");
                using var first = JsonDocument.Parse(server.Bodies[0]);
                T.Check(first.RootElement.TryGetProperty("tools", out _), "earlier rounds do");
            }
        });

        await T.Group("Agent: reply cut by a length limit", async () =>
        {
            var tool = new EchoTool();
            using (var server = new MockAi(MockAi.Reply(MockAi.Text("part one, "), MockAi.Finish("length"), MockAi.Done),
                                           MockAi.Reply(MockAi.Text("part two."), MockAi.Finish("stop"), MockAi.Done)))
            {
                var seen = await Go(server, tool);
                T.Eq("part one, part two.", seen.Shown.ToString(), "a cut reply is continued and the pieces join up");
                T.Check(!seen.WasTruncated, "so nothing is reported as cut off");
                var second = ToolBody(server.Bodies[1]);
                T.Check(second.Contains("assistant:part one, ") && second.Contains("Continue exactly where you stopped"), "the model is shown what it wrote and asked to go on");
            }

            using (var server = new MockAi(MockAi.Reply(MockAi.Text("again "), MockAi.Finish("length"), MockAi.Done)))
            {
                var seen = await Go(server, new EchoTool());
                T.Eq(5, server.Requests, "continuing stops after four extra replies");
                T.Check(seen.WasTruncated, "and the person is told the answer was cut");
            }

            using (var server = new MockAi(MockAi.Reply(MockAi.Thought("thinking only"), MockAi.Finish("length"), MockAi.Done)))
            {
                var seen = await Go(server, new EchoTool());
                T.Eq(1, server.Requests, "a reply with no text is not continued");
                T.Check(seen.WasTruncated, "and is reported as cut off");
            }
        });

        await T.Group("Agent: tool calls written as text (DeepSeek)", async () =>
        {
            var bar = "｜";
            string O = "<" + bar + bar + "DSML" + bar + bar, C = "</" + bar + bar + "DSML" + bar + bar;
            var markup = "Let me try.\n" + O + " calls>" + O + " invoke name=\"echo\">" + O + " parameter name=\"text\" string=\"true\">from text" + C + " parameter>" + C + " invoke>" + C + " calls>";
            var tool = new EchoTool();
            using var server = new MockAi(MockAi.Reply(MockAi.Text(markup.Substring(0, 20)), MockAi.Text(markup.Substring(20)), MockAi.Finish("stop"), MockAi.Done),
                                          MockAi.Reply(MockAi.Text("Finished."), MockAi.Finish("stop"), MockAi.Done));
            var seen = await Go(server, tool);
            T.Eq("from text", string.Join(",", tool.Ran), "a call written into the reply text is run");
            T.Check(!seen.Shown.ToString().Contains("DSML") && !seen.Shown.ToString().Contains("invoke"), "the markup never reaches the screen");
            T.Eq("Let me try.\n\n\nFinished.", seen.Shown.ToString(), "the rest of the conversation continues normally");
            T.Check(ToolBody(server.Bodies[1]).Contains("tool:echoed: from text"), "the result goes back to the model");
        });
    }
}
