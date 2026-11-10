using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace Perch.Services.Ai;

// A message of the conversation. An assistant message can ask for tools to be run (ToolCalls); the answer to each such
// call is a "tool" message naming the call it answers. Reasoning is the thinking that came with an assistant message,
// which DeepSeek wants passed back while the same turn is still using tools.
public sealed record ChatMessage(string Role, string Content, IReadOnlyList<ToolCall>? ToolCalls = null, string? ToolCallId = null, string? Reasoning = null);

// A tool the model asked to run: its call id, the tool's name, and the arguments as a JSON text.
public sealed record ToolCall(string Id, string Name, string Arguments);

// A tool offered to the model: name, what it does, and its parameters as a JSON schema.
public sealed record ToolSpec(string Name, string Description, string ParametersSchema);

// A failed request, with the HTTP status when the server answered at all.
public sealed class AiException(string message, HttpStatusCode? status = null) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;
}

// Talks to any server that implements the OpenAI "chat completions" protocol (DeepSeek, OpenAI, Ollama, LM Studio,
// OpenRouter, ...). Answers stream in as server-sent events, so they can be shown while they are being written; a
// model that thinks first (deepseek-reasoner and others) sends that thinking separately. JSON is written and read by
// hand, which keeps this free of reflection (Native AOT).
public static class OpenAiClient
{
    // Streaming answers take as long as they take, so the client itself has no overall timeout; instead a stalled connection is
    // noticed by the watchdog in StreamAsync. Pooled connections are retired quickly: a connection that a router or proxy silently
    // dropped while idle would otherwise swallow the next question and leave it waiting forever.
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(15),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
        KeepAlivePingDelay = TimeSpan.FromSeconds(30),
        KeepAlivePingTimeout = TimeSpan.FromSeconds(15),
        KeepAlivePingPolicy = HttpKeepAlivePingPolicy.WithActiveRequests,
    }) { Timeout = Timeout.InfiniteTimeSpan };

    // How long the server may stay silent: before the first byte (a local model may be loading, a thinking model may be
    // thinking quietly), and between two lines of a stream that has begun (servers send keep-alive comments, so a long silence is a stall).
    internal static TimeSpan FirstByteLimit = TimeSpan.FromSeconds(180), StreamIdleLimit = TimeSpan.FromSeconds(90); // not readonly: the tests shorten them

    // "api.openai.com/v1/" -> https://api.openai.com/v1 ; the protocol path is added by the callers.
    public static string Normalize(string baseUrl)
    {
        var text = (baseUrl ?? "").Trim().TrimEnd('/');
        if (text.Length > 0 && !text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        return text;
    }

    // Local servers (Ollama, LM Studio) usually need no key.
    public static bool IsLocal(string baseUrl) =>
        Uri.TryCreate(Normalize(baseUrl), UriKind.Absolute, out var uri) && (uri.IsLoopback || uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase));

    // Streams one reply. Text and thinking are passed on as they arrive; the tools the model wants run (if any were
    // offered and it chose to) are returned once the reply is complete. onFinish gets the server's reason for stopping
    // ("stop", "length" when the reply was cut off by a length limit, "tool_calls", ...).
    public static async Task<List<ToolCall>> StreamAsync(string baseUrl, string key, string model, IReadOnlyList<ChatMessage> messages,
        Action<string> onText, Action<string> onThinking, CancellationToken cancel, IReadOnlyList<ToolSpec>? tools = null, Action<string>? onFinish = null)
    {
        var body = Body(model, messages, tools);
        // The watchdog: cancelled when the server has been silent for too long (the user's own Stop is told apart below).
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        try
        {
            using var response = await Connect(baseUrl, key, body, watchdog, cancel);
            var calls = new SortedDictionary<int, (string Id, string Name, StringBuilder Arguments)>();
            using var stream = await response.Content.ReadAsStreamAsync(watchdog.Token);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            string? finish = null;
            var done = false;
            while (!done)
            {
                watchdog.CancelAfter(StreamIdleLimit);
                if (await reader.ReadLineAsync(watchdog.Token) is not { } line) break;
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue; // comments and blank separators
                var payload = line.AsSpan(5).Trim();
                if (payload.SequenceEqual("[DONE]")) { done = true; break; }
                try
                {
                    using var chunk = JsonDocument.Parse(payload.ToString());
                    var root = chunk.RootElement;
                    // Some servers (OpenRouter and others) report a failure in the middle of the stream as an event of its own.
                    if (root.TryGetProperty("error", out var failure) && failure.ValueKind is JsonValueKind.Object or JsonValueKind.String)
                        throw new AiException(failure.ValueKind == JsonValueKind.Object ? Text(failure, "message") ?? failure.ToString() : failure.GetString() ?? "");
                    if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0) continue;
                    if (Text(choices[0], "finish_reason") is { Length: > 0 } reason) finish = reason;
                    if (!choices[0].TryGetProperty("delta", out var delta)) continue;
                    if (Text(delta, "content") is { Length: > 0 } text) onText(text);
                    if ((Text(delta, "reasoning_content") ?? Text(delta, "reasoning")) is { Length: > 0 } thinking) onThinking(thinking);
                    // Tool calls arrive in pieces, keyed by their index: the id and name first, the arguments a few characters at a time.
                    if (delta.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
                        foreach (var part in toolCalls.EnumerateArray())
                        {
                            var index = part.TryGetProperty("index", out var i) && i.TryGetInt32(out var n) ? n : calls.Count;
                            var entry = calls.TryGetValue(index, out var e) ? e : (Id: "", Name: "", Arguments: new StringBuilder());
                            if (Text(part, "id") is { Length: > 0 } id) entry.Id = id;
                            if (part.TryGetProperty("function", out var function))
                            {
                                if (Text(function, "name") is { Length: > 0 } name) entry.Name += name;
                                if (Text(function, "arguments") is { } arguments) entry.Arguments.Append(arguments);
                            }
                            calls[index] = entry;
                        }
                }
                catch (JsonException) { /* a damaged event: skip it, the next one continues the answer */ }
            }
            // A stream that just stops, with no "[DONE]" and no stop reason, was cut off (connection lost, server restarted).
            if (!done && finish == null) throw new AiException(Loc.T("Ai.ErrCut"));
            onFinish?.Invoke(finish ?? "");
            return calls.Values.Where(c => c.Name.Length > 0)
                .Select((c, k) => new ToolCall(c.Id.Length > 0 ? c.Id : "call_" + k, c.Name, c.Arguments.Length > 0 ? c.Arguments.ToString() : "{}")).ToList();
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new AiException(Loc.T("Ai.ErrTimeout")); // the watchdog fired, not the user
        }
    }

    // Sends the request and waits for the answer to begin. A connection problem or a server that is briefly overloaded
    // (502, 503, 504) is tried once more before giving up; nothing has been shown to the user yet at that point.
    private static async Task<HttpResponseMessage> Connect(string baseUrl, string key, string body, CancellationTokenSource watchdog, CancellationToken cancel)
    {
        for (var attempt = 0; ; attempt++)
        {
            watchdog.CancelAfter(FirstByteLimit);
            using var request = Create(HttpMethod.Post, baseUrl + "/chat/completions", key);
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            HttpResponseMessage? response = null;
            try
            {
                response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, watchdog.Token);
                if (response.IsSuccessStatusCode) return response;
                if (attempt == 0 && response.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout)
                {
                    response.Dispose();
                    response = null;
                    await Task.Delay(1500, cancel);
                    continue;
                }
                throw new AiException(await ErrorText(response, watchdog.Token), response.StatusCode);
            }
            catch (HttpRequestException) when (attempt == 0 && !cancel.IsCancellationRequested)
            {
                await Task.Delay(1500, cancel);
            }
            finally
            {
                if (response is { IsSuccessStatusCode: false }) response.Dispose();
            }
        }
    }

    // The model names the server offers (GET /models), for filling in the provider's list.
    public static async Task<List<string>> ListModelsAsync(string baseUrl, string key, CancellationToken cancel)
    {
        using var request = Create(HttpMethod.Get, baseUrl + "/models", key);
        using var response = await Http.SendAsync(request, cancel);
        if (!response.IsSuccessStatusCode) throw new AiException(await ErrorText(response, cancel), response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancel));
        var names = new List<string>();
        foreach (var field in new[] { "data", "models" })
            if (document.RootElement.TryGetProperty(field, out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var item in list.EnumerateArray())
                    if ((item.ValueKind == JsonValueKind.Object ? Text(item, "id") ?? Text(item, "name") : item.GetString()) is { Length: > 0 } name) names.Add(name);
        return names.Distinct().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static HttpRequestMessage Create(HttpMethod method, string url, string key)
    {
        if (!Uri.TryCreate(Normalize(url), UriKind.Absolute, out var uri)) throw new AiException("Invalid address: " + url);
        var request = new HttpRequestMessage(method, uri);
        if (!string.IsNullOrWhiteSpace(key)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Trim());
        return request;
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string Body(string model, IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolSpec>? tools)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteString("model", model);
            json.WriteBoolean("stream", true);
            json.WriteStartArray("messages");
            foreach (var message in messages)
            {
                json.WriteStartObject();
                json.WriteString("role", message.Role);
                json.WriteString("content", message.Content);
                if (message.ToolCallId != null) json.WriteString("tool_call_id", message.ToolCallId);
                if (!string.IsNullOrEmpty(message.Reasoning)) json.WriteString("reasoning_content", message.Reasoning);
                if (message.ToolCalls is { Count: > 0 } calls)
                {
                    json.WriteStartArray("tool_calls");
                    foreach (var call in calls)
                    {
                        json.WriteStartObject();
                        json.WriteString("id", call.Id);
                        json.WriteString("type", "function");
                        json.WriteStartObject("function");
                        json.WriteString("name", call.Name);
                        json.WriteString("arguments", call.Arguments);
                        json.WriteEndObject();
                        json.WriteEndObject();
                    }
                    json.WriteEndArray();
                }
                json.WriteEndObject();
            }
            json.WriteEndArray();
            if (tools is { Count: > 0 })
            {
                json.WriteStartArray("tools");
                foreach (var tool in tools)
                {
                    json.WriteStartObject();
                    json.WriteString("type", "function");
                    json.WriteStartObject("function");
                    json.WriteString("name", tool.Name);
                    json.WriteString("description", tool.Description);
                    json.WritePropertyName("parameters");
                    json.WriteRawValue(tool.ParametersSchema);
                    json.WriteEndObject();
                    json.WriteEndObject();
                }
                json.WriteEndArray();
            }
            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    // The server's own explanation ({"error":{"message":"..."}}) when there is one.
    private static async Task<string> ErrorText(HttpResponseMessage response, CancellationToken cancel)
    {
        var body = await response.Content.ReadAsStringAsync(cancel);
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.Object && Text(error, "message") is { Length: > 0 } message) return message;
                if (error.ValueKind == JsonValueKind.String && error.GetString() is { Length: > 0 } plain) return plain;
            }
        }
        catch (JsonException) { }
        return body.Length > 0 ? (body.Length > 300 ? body[..300] : body) : response.ReasonPhrase ?? "";
    }
}
