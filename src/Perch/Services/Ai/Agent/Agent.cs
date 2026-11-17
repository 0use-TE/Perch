using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace Perch.Services.Ai.Agent;

// What a tool hands back: the text the model reads, and a short line for the person watching ("5 results").
public sealed record ToolResult(string Content, string Summary);

// How much a tool can change. Anything but Safe is shown to the user and runs only after they allow it.
public enum ToolRisk { Safe, Change, Dangerous }

// Something the model may use while answering (search the web, read a page, ...). New abilities are new tools.
public interface ITool
{
    ToolSpec Spec { get; }
    ToolRisk Risk => ToolRisk.Safe;
    // What the user is shown when asked to allow the call (the full script, for example); the short line by default.
    string Detail(JsonElement arguments) => "";
    // A short line for the step list, from the call's arguments ("Searching: weather in Paris").
    string Describe(JsonElement arguments);
    Task<ToolResult> RunAsync(JsonElement arguments, CancellationToken cancel);
}

// Progress reports from a run, for the conversation view.
public interface IAgentObserver
{
    void Text(string piece);
    void Thinking(string piece);
    // A tool call started; returns a handle passed back to StepDone.
    object StepStarted(string label);
    void StepDone(object step, string summary, bool failed);
    void StepPayload(object step, string args, string output) { }
    // Asks the user to allow an action that changes something; false when they decline (or the answer is stopped).
    Task<bool> ConfirmAsync(string title, string detail, ToolRisk risk, CancellationToken cancel);
    // The server cut the answer off at a length limit and it could not be continued.
    void Truncated();
    // Called once when the agent run completes (elapsed time, total tool calls executed, and total tokens if reported).
    void Finished(TimeSpan elapsed, int toolCalls, int totalTokens) { }
}

// The client-side agent loop: ask the model; if it wants tools, run them (concurrently when all are Safe), hand it the
// results, and ask again, until it answers in words (or the round limit is reached, when it is asked to answer with what it has).
public static class Agent
{
    public const int MaxRounds = 12;
    private const int MaxContinuations = 4;

    public static async Task RunAsync(string baseUrl, string key, string model, IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ITool> tools, IAgentObserver observer, CancellationToken cancel, Func<ToolRisk, bool>? needsConfirm = null, Func<ITool, bool>? toolNeedsConfirm = null)
    {
        needsConfirm ??= risk => risk != ToolRisk.Safe;
        bool ShouldConfirm(ITool t) => toolNeedsConfirm != null ? toolNeedsConfirm(t) : needsConfirm(t.Risk);
        var clock = Stopwatch.StartNew();
        var totalTokens = 0;
        var totalTools = 0;
        var conversation = new List<ChatMessage>(messages);
        var specs = tools.Select(t => t.Spec).ToList();
        var continuations = 0;

        try
        {
            for (var round = 0; round < MaxRounds; round++)
            {
                var text = new StringBuilder();
                var thinking = new StringBuilder();
                var last = round == MaxRounds - 1; // the final round offers no tools, so the model has to answer
                var finish = "";
                var dsml = new DsmlFilter(); // tool calls a DeepSeek model wrote into its text instead of the tool_calls field
                var calls = await OpenAiClient.StreamAsync(baseUrl, key, model, conversation,
                    piece => { if (dsml.Push(piece) is { Length: > 0 } shown) { text.Append(shown); observer.Text(shown); } },
                    piece => { thinking.Append(piece); observer.Thinking(piece); },
                    cancel, last || specs.Count == 0 ? null : specs, reason => finish = reason,
                    usage => totalTokens += usage.TotalTokens);
                if (dsml.Finish() is { Length: > 0 } rest) { text.Append(rest); observer.Text(rest); }
                if (calls.Count == 0 && dsml.Found && !last)
                    calls = dsml.Calls().Select((c, k) => new ToolCall("call_text_" + round + "_" + k, c.Name, c.Arguments)).ToList();
                if (calls.Count == 0)
                {
                    if (finish != "length") return;
                    if (text.Length == 0 || continuations >= MaxContinuations) { observer.Truncated(); return; }
                    continuations++;
                    round--; // continuing is not a tool round
                    conversation.Add(new ChatMessage("assistant", text.ToString()));
                    conversation.Add(new ChatMessage("user", "Your reply was cut off by a length limit. Continue exactly where you stopped, without repeating anything and without any introduction."));
                    continue;
                }
                if (text.Length > 0) observer.Text("\n\n");

                conversation.Add(new ChatMessage("assistant", text.ToString(), calls, null, thinking.ToString()));
                totalTools += calls.Count;

                // Parse all calls first and check if the whole batch can run concurrently (no confirmations needed).
                var parsed = calls.Select(call =>
                {
                    var tool = tools.FirstOrDefault(t => t.Spec.Name == call.Name);
                    JsonDocument? doc = null;
                    try { doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.Arguments) ? "{}" : call.Arguments); }
                    catch (JsonException) { }
                    var args = doc?.RootElement ?? default;
                    var step = observer.StepStarted(tool != null && doc != null ? tool.Describe(args) : call.Name);
                    return (call, tool, doc, args, step);
                }).ToList();

                var allParallelSafe = parsed.Count > 1 && parsed.All(p => p.tool != null && p.doc != null && !ShouldConfirm(p.tool));
                if (allParallelSafe)
                {
                    var results = await Task.WhenAll(parsed.Select(p => RunOneAsync(p.call, p.tool!, p.doc!, p.args, p.step, observer, ShouldConfirm, cancel)));
                    for (var i = 0; i < parsed.Count; i++)
                        conversation.Add(new ChatMessage("tool", results[i], null, parsed[i].call.Id));
                }
                else
                {
                    foreach (var p in parsed)
                    {
                        cancel.ThrowIfCancellationRequested();
                        var content = await RunOneAsync(p.call, p.tool, p.doc, p.args, p.step, observer, ShouldConfirm, cancel);
                        conversation.Add(new ChatMessage("tool", content, null, p.call.Id));
                    }
                }
            }
        }
        finally
        {
            observer.Finished(clock.Elapsed, totalTools, totalTokens);
        }
    }

    private static async Task<string> RunOneAsync(ToolCall call, ITool? tool, JsonDocument? arguments, JsonElement args,
        object step, IAgentObserver observer, Func<ITool, bool> shouldConfirm, CancellationToken cancel)
    {
        try
        {
            if (tool == null) throw new InvalidOperationException($"There is no tool named {call.Name}.");
            if (arguments == null) throw new InvalidOperationException("The arguments were not valid JSON.");
            if (shouldConfirm(tool) && !await observer.ConfirmAsync(tool.Describe(args), tool.Detail(args), tool.Risk, cancel))
            {
                cancel.ThrowIfCancellationRequested();
                Log.Info($"Declined by the user: {call.Name}");
                observer.StepDone(step, Loc.T("Ai.Declined"), true);
                return "The user declined this action, so it was not done. Do not try it again; say what you could not do.";
            }
            if (tool.Risk != ToolRisk.Safe) Log.Info($"Running {call.Name}: {call.Arguments}");
            var result = await tool.RunAsync(args, cancel);
            observer.StepPayload(step, call.Arguments, result.Content);
            observer.StepDone(step, result.Summary, false);
            return result.Content;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            Log.Warn($"Tool {call.Name} failed", e);
            observer.StepPayload(step, call.Arguments, "Error: " + e.Message);
            observer.StepDone(step, e.Message, true);
            return "Error: " + e.Message;
        }
        finally { arguments?.Dispose(); }
    }

    public static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
