using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Perch.Services.Ai.Agent;

// DeepSeek models sometimes write a tool call into the reply text instead of the API's tool_calls field, in their own markup:
//   <｜｜DSML｜｜ calls><｜｜DSML｜｜ invoke name="run_powershell"><｜｜DSML｜｜ parameter name="script" string="true">...</...>
// The server then reports an ordinary finished answer, nothing is run, and the conversation just stops with that markup on
// screen. This keeps the markup out of the visible answer and turns it back into the calls the model meant.
public sealed partial class DsmlFilter
{
    private readonly StringBuilder held = new(), markup = new();
    private bool inMarkup;

    // Takes a piece of the reply; returns what may be shown (text up to where the markup begins, and nothing after).
    public string Push(string piece)
    {
        if (inMarkup) { markup.Append(piece); return ""; }
        held.Append(piece);
        var pending = held.ToString();
        var shown = new StringBuilder();
        while (pending.Length > 0)
        {
            var open = pending.IndexOf('<');
            if (open < 0) { shown.Append(pending); pending = ""; break; }
            shown.Append(pending, 0, open);
            pending = pending[open..];
            var verdict = Looks(pending);
            if (verdict < 0) break; // too short to tell yet: wait for more
            if (verdict > 0) { inMarkup = true; markup.Append(pending); pending = ""; break; }
            shown.Append('<');
            pending = pending[1..];
        }
        held.Clear().Append(pending);
        return shown.ToString();
    }

    // The reply is complete: text that was only held back in case it turned out to be markup is shown after all.
    public string Finish()
    {
        if (inMarkup) return "";
        var rest = held.ToString();
        held.Clear();
        return rest;
    }

    public bool Found => inMarkup;

    // 1 if the text starts the markup ("<" + bars + "DSML"), 0 if it does not, -1 if it is too short to tell.
    private static int Looks(string text)
    {
        var i = 1;
        while (i < text.Length && text[i] is '｜' or '|') i++;
        if (i >= text.Length) return -1;
        if (i == 1) return 0;
        while (i < text.Length && text[i] == ' ') i++;
        if (i >= text.Length) return -1;
        var rest = text.AsSpan(i);
        const string tag = "DSML";
        if (rest.Length >= tag.Length) return rest.StartsWith(tag) ? 1 : 0;
        return tag.AsSpan().StartsWith(rest) ? -1 : 0;
    }

    // The calls written in the markup: tool name and arguments as JSON text.
    public List<(string Name, string Arguments)> Calls()
    {
        var result = new List<(string, string)>();
        if (!inMarkup) return result;
        // Without the bars and the DSML mark the tags are plain ("< invoke name=..."), whatever the model's exact spelling was.
        var text = markup.ToString().Replace("｜", "").Replace("|", "").Replace("DSML", "");
        foreach (Match invoke in Invoke().Matches(text))
        {
            using var stream = new MemoryStream();
            using (var json = new Utf8JsonWriter(stream))
            {
                json.WriteStartObject();
                foreach (Match parameter in Parameter().Matches(invoke.Groups[2].Value))
                {
                    var value = parameter.Groups[3].Value;
                    var asText = !parameter.Groups[2].Success || parameter.Groups[2].Value != "false";
                    if (asText) json.WriteString(parameter.Groups[1].Value, value);
                    else
                    {
                        json.WritePropertyName(parameter.Groups[1].Value);
                        try { using var _ = JsonDocument.Parse(value); json.WriteRawValue(value.Trim()); }
                        catch (JsonException) { json.WriteStringValue(value); }
                    }
                }
                json.WriteEndObject();
            }
            result.Add((invoke.Groups[1].Value, Encoding.UTF8.GetString(stream.ToArray())));
        }
        return result;
    }

    [GeneratedRegex("<\\s*invoke\\s+name=\"([^\"]+)\"\\s*>(.*?)<\\s*/\\s*invoke\\s*>", RegexOptions.Singleline)]
    private static partial Regex Invoke();

    [GeneratedRegex("<\\s*parameter\\s+name=\"([^\"]+)\"(?:\\s+string=\"(true|false)\")?\\s*>(.*?)<\\s*/\\s*parameter\\s*>", RegexOptions.Singleline)]
    private static partial Regex Parameter();
}
