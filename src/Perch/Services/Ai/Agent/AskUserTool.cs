using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Perch.Services.Ai.Agent;

/// <summary>
/// Presents a multiple-choice question or plan options to the user in the chat UI
/// and waits for the user to click a choice (or type a custom reply).
/// </summary>
public sealed class AskUserTool(Func<string, IReadOnlyList<string>, CancellationToken, Task<string>> ask) : ITool
{
    public ToolSpec Spec { get; } = new(
        "ask_user",
        "Ask the user to choose among concrete options or plans in the chat UI. Use when there are distinct approaches, preferences, or decisions for the user to pick.",
        """
            {
              "type": "object",
              "properties": {
                "question": { "type": "string", "description": "Short question or decision title shown above the options." },
                "options": {
                  "type": "array",
                  "items": { "type": "string" },
                  "description": "2 to 5 concise option labels for the user to click."
                }
              },
              "required": ["question", "options"]
            }
            """);

    public ToolRisk Risk => ToolRisk.Safe;

    public string Describe(JsonElement arguments)
    {
        var q = arguments.TryGetProperty("question", out var qp) ? qp.GetString() ?? "" : "";
        return string.IsNullOrWhiteSpace(q) ? "Ask user" : q;
    }

    public async Task<ToolResult> RunAsync(JsonElement arguments, CancellationToken cancel)
    {
        var question = arguments.TryGetProperty("question", out var qp) ? qp.GetString()?.Trim() ?? "" : "";
        var options = new List<string>();
        if (arguments.TryGetProperty("options", out var op) && op.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in op.EnumerateArray())
            {
                var text = item.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(text)) options.Add(text);
                if (options.Count >= 6) break;
            }
        }
        if (string.IsNullOrWhiteSpace(question) || options.Count == 0)
            return new ToolResult("ask_user requires a non-empty question and at least one option.", "Invalid args");

        var chosen = await ask(question, options, cancel);
        return new ToolResult($"User selected: {chosen}", chosen);
    }
}
