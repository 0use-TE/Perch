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
    // Asks the user to allow an action that changes something; false when they decline (or the answer is stopped).
    Task<bool> ConfirmAsync(string title, string detail, ToolRisk risk, CancellationToken cancel);
}

// The client-side agent loop: ask the model; if it wants tools, run them, hand it the results, and ask again, until it
// answers in words (or the round limit is reached, when it is asked to answer with what it has).
public static class Agent
{
    public const int MaxRounds = 6;

    public static async Task RunAsync(string baseUrl, string key, string model, IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ITool> tools, IAgentObserver observer, CancellationToken cancel, Func<ToolRisk, bool>? needsConfirm = null)
    {
        needsConfirm ??= risk => risk != ToolRisk.Safe;
        var conversation = new List<ChatMessage>(messages);
        var specs = tools.Select(t => t.Spec).ToList();
        for (var round = 0; round < MaxRounds; round++)
        {
            var text = new StringBuilder();
            var thinking = new StringBuilder();
            var last = round == MaxRounds - 1; // the final round offers no tools, so the model has to answer
            var calls = await OpenAiClient.StreamAsync(baseUrl, key, model, conversation,
                piece => { text.Append(piece); observer.Text(piece); },
                piece => { thinking.Append(piece); observer.Thinking(piece); },
                cancel, last || specs.Count == 0 ? null : specs);
            if (calls.Count == 0) return;
            if (text.Length > 0) observer.Text("\n\n"); // what the model said before using a tool stays apart from the answer that follows

            conversation.Add(new ChatMessage("assistant", text.ToString(), calls, null, thinking.ToString()));
            foreach (var call in calls)
            {
                cancel.ThrowIfCancellationRequested();
                var tool = tools.FirstOrDefault(t => t.Spec.Name == call.Name);
                JsonDocument? arguments = null;
                try { arguments = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.Arguments) ? "{}" : call.Arguments); }
                catch (JsonException) { }
                var args = arguments?.RootElement ?? default;
                var step = observer.StepStarted(tool != null && arguments != null ? tool.Describe(args) : call.Name);
                string content;
                try
                {
                    if (tool == null) throw new InvalidOperationException($"There is no tool named {call.Name}.");
                    if (arguments == null) throw new InvalidOperationException("The arguments were not valid JSON.");
                    if (needsConfirm(tool.Risk) && !await observer.ConfirmAsync(tool.Describe(args), tool.Detail(args), tool.Risk, cancel))
                    {
                        cancel.ThrowIfCancellationRequested();
                        Log.Info($"Declined by the user: {call.Name}");
                        observer.StepDone(step, Loc.T("Ai.Declined"), true);
                        conversation.Add(new ChatMessage("tool", "The user declined this action, so it was not done. Do not try it again; say what you could not do.", null, call.Id));
                        continue;
                    }
                    if (tool.Risk != ToolRisk.Safe) Log.Info($"Running {call.Name}: {call.Arguments}");
                    var result = await tool.RunAsync(args, cancel);
                    content = result.Content;
                    observer.StepDone(step, result.Summary, false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception e)
                {
                    Log.Warn($"Tool {call.Name} failed", e);
                    content = "Error: " + e.Message;
                    observer.StepDone(step, e.Message, true);
                }
                finally { arguments?.Dispose(); }
                conversation.Add(new ChatMessage("tool", content, null, call.Id));
            }
        }
    }

    public static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
