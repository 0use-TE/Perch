using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace Perch.Services.Ai.Agent;

// Searches the web through the service chosen in Settings (Tavily, Bocha or a SearXNG server).
public sealed partial class WebSearchTool(Settings settings) : ITool
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public ToolSpec Spec { get; } = new("web_search",
        "Search the web for current or specific information. Returns numbered results with title, link and a snippet. "
        + "Use several short searches rather than one long one; open a page with read_webpage when the snippets are not enough.",
        """{"type":"object","properties":{"query":{"type":"string","description":"What to search for, in the language most likely to find good pages."}},"required":["query"]}""");

    // "local" needs nothing: the search page is fetched from this computer, like a browser would.
    public static bool Configured(Settings s) => s.SearchEngine switch
    {
        "searxng" => s.SearchUrl.Trim().Length > 0,
        "tavily" or "bocha" => s.SearchKeyPlain.Length > 0,
        _ => true,
    };

    public string Describe(JsonElement arguments) => Loc.F("Ai.StepSearch", Agent.Str(arguments, "query") ?? "");

    public async Task<ToolResult> RunAsync(JsonElement arguments, CancellationToken cancel)
    {
        var query = Agent.Str(arguments, "query")?.Trim();
        if (string.IsNullOrEmpty(query)) throw new ArgumentException("query is empty");
        if (!Configured(settings)) throw new InvalidOperationException("No web search service is set up (Settings > AI > Web search).");
        var results = settings.SearchEngine switch
        {
            "bocha" => await Bocha(query, cancel),
            "searxng" => await SearXng(query, cancel),
            "tavily" => await Tavily(query, cancel),
            _ => await Local(query, cancel),
        };
        var text = new StringBuilder();
        for (var i = 0; i < results.Count; i++)
            text.Append('[').Append(i + 1).Append("] ").AppendLine(results[i].Title).AppendLine(results[i].Url).AppendLine(results[i].Snippet).AppendLine();
        return new ToolResult(results.Count == 0 ? "No results." : text.ToString().TrimEnd(), Loc.F("Ai.StepFound", results.Count));
    }

    private sealed record Hit(string Title, string Url, string Snippet);

    // No service and no key: the results page of Bing (the Chinese edition for a Chinese interface, which needs no VPN there)
    // is fetched from this computer and read; DuckDuckGo's plain page is the fallback when Bing returns nothing usable.
    private static async Task<List<Hit>> Local(string query, CancellationToken cancel)
    {
        var host = Loc.Code.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "cn.bing.com" : "www.bing.com";
        var hits = new List<Hit>();
        try { hits = BingHits(await Page($"https://{host}/search?q={Uri.EscapeDataString(query)}&setlang={(host == "cn.bing.com" ? "zh-CN" : "en")}", cancel)); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !cancel.IsCancellationRequested) { Log.Warn("Bing search failed, trying DuckDuckGo", e); }
        if (hits.Count == 0) hits = DuckHits(await Page("https://html.duckduckgo.com/html/?q=" + Uri.EscapeDataString(query), cancel));
        return hits.Take(6).ToList();
    }

    private static async Task<string> Page(string url, CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
        request.Headers.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9,en;q=0.8");
        using var response = await Http.SendAsync(request, cancel);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancel);
    }

    private static List<Hit> BingHits(string html)
    {
        var hits = new List<Hit>();
        foreach (Match block in BingBlock().Matches(html))
        {
            var heading = BingHeading().Match(block.Value);
            if (!heading.Success) continue;
            var url = BingTarget(WebUtility.HtmlDecode(heading.Groups[1].Value));
            var snippet = BingSnippet().Match(block.Value);
            hits.Add(new Hit(PlainText(heading.Groups[2].Value), url, snippet.Success ? PlainText(snippet.Groups[1].Value) : ""));
        }
        return hits.Where(h => h.Title.Length > 0 && Uri.IsWellFormedUriString(h.Url, UriKind.Absolute)).ToList();
    }

    // Bing wraps every result link in a redirect (bing.com/ck/a?...&u=a1<base64 of the real address>); this unwraps it.
    private static string BingTarget(string href)
    {
        if (!href.Contains("/ck/a", StringComparison.Ordinal) || !Uri.TryCreate(href, UriKind.Absolute, out var uri)) return href;
        foreach (var pair in uri.Query.TrimStart('?').Split('&'))
        {
            if (!pair.StartsWith("u=", StringComparison.Ordinal)) continue;
            var value = Uri.UnescapeDataString(pair[2..]);
            if (!value.StartsWith("a1", StringComparison.Ordinal)) break;
            var b64 = value[2..].Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            try { return Encoding.UTF8.GetString(Convert.FromBase64String(b64)); } catch (FormatException) { break; }
        }
        return href;
    }

    private static List<Hit> DuckHits(string html)
    {
        var hits = new List<Hit>();
        foreach (Match m in DuckResult().Matches(html))
        {
            var href = WebUtility.HtmlDecode(m.Groups[1].Value);
            // DuckDuckGo's own redirect keeps the real address in uddg=
            if (href.Contains("uddg=", StringComparison.Ordinal))
            {
                var at = href.IndexOf("uddg=", StringComparison.Ordinal) + 5;
                var end = href.IndexOf('&', at);
                href = Uri.UnescapeDataString(end < 0 ? href[at..] : href[at..end]);
            }
            if (href.StartsWith("//", StringComparison.Ordinal)) href = "https:" + href;
            if (Uri.IsWellFormedUriString(href, UriKind.Absolute)) hits.Add(new Hit(PlainText(m.Groups[2].Value), href, PlainText(m.Groups[3].Value)));
        }
        return hits;
    }

    private static string PlainText(string html) => Spaces().Replace(WebUtility.HtmlDecode(AnyTag().Replace(html, "")), " ").Trim();

    [GeneratedRegex(@"<li class=""b_algo"".*?</li>", RegexOptions.Singleline)] private static partial Regex BingBlock();
    [GeneratedRegex(@"<h2[^>]*>\s*<a[^>]*href=""([^""]+)""[^>]*>(.*?)</a>", RegexOptions.Singleline)] private static partial Regex BingHeading();
    [GeneratedRegex(@"<p[^>]*class=""[^""]*b_lineclamp[^""]*""[^>]*>(.*?)</p>|<div class=""b_caption[^""]*"".*?<p[^>]*>(.*?)</p>", RegexOptions.Singleline)] private static partial Regex BingSnippet();
    [GeneratedRegex(@"<a[^>]*class=""result__a""[^>]*href=""([^""]+)""[^>]*>(.*?)</a>.*?<a[^>]*class=""result__snippet""[^>]*>(.*?)</a>", RegexOptions.Singleline)] private static partial Regex DuckResult();
    [GeneratedRegex(@"<[^>]+>")] private static partial Regex AnyTag();
    [GeneratedRegex(@"\s+")] private static partial Regex Spaces();

    private async Task<List<Hit>> Tavily(string query, CancellationToken cancel)
    {
        using var root = await Post("https://api.tavily.com/search", settings.SearchKeyPlain, w =>
        {
            w.WriteString("query", query); w.WriteNumber("max_results", 6); w.WriteString("search_depth", "basic");
        }, cancel);
        return Hits(root.RootElement, "results", "title", "url", "content");
    }

    private async Task<List<Hit>> Bocha(string query, CancellationToken cancel)
    {
        using var root = await Post("https://api.bochaai.com/v1/web-search", settings.SearchKeyPlain, w =>
        {
            w.WriteString("query", query); w.WriteBoolean("summary", true); w.WriteNumber("count", 6);
        }, cancel);
        var pages = root.RootElement.TryGetProperty("data", out var data) && data.TryGetProperty("webPages", out var web) ? web : default;
        var hits = Hits(pages, "value", "name", "url", "summary");
        if (hits.All(h => h.Snippet.Length == 0)) hits = Hits(pages, "value", "name", "url", "snippet");
        return hits;
    }

    private async Task<List<Hit>> SearXng(string query, CancellationToken cancel)
    {
        var url = OpenAiClient.Normalize(settings.SearchUrl) + "/search?format=json&q=" + Uri.EscapeDataString(query);
        using var response = await Http.GetAsync(url, cancel);
        response.EnsureSuccessStatusCode();
        using var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancel));
        return Hits(root.RootElement, "results", "title", "url", "content").Take(6).ToList();
    }

    private static async Task<JsonDocument> Post(string url, string key, Action<Utf8JsonWriter> body, CancellationToken cancel)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream)) { w.WriteStartObject(); body(w); w.WriteEndObject(); }
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(stream.ToArray()) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var response = await Http.SendAsync(request, cancel);
        var text = await response.Content.ReadAsStringAsync(cancel);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {(int)response.StatusCode}: {(text.Length > 200 ? text[..200] : text)}");
        return JsonDocument.Parse(text);
    }

    private static List<Hit> Hits(JsonElement parent, string list, string title, string url, string snippet)
    {
        var hits = new List<Hit>();
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(list, out var items) || items.ValueKind != JsonValueKind.Array) return hits;
        foreach (var item in items.EnumerateArray())
        {
            var link = Agent.Str(item, url) ?? "";
            if (link.Length == 0) continue;
            var text = Agent.Str(item, snippet) ?? "";
            hits.Add(new Hit(Agent.Str(item, title) ?? link, link, text.Length > 600 ? text[..600] + "…" : text));
        }
        return hits;
    }
}

// Opens a web page and returns its readable text (no scripts, styles or markup), shortened to a sensible length.
public sealed partial class ReadPageTool : ITool
{
    private const int MaxChars = 8000, MaxBytes = 3_000_000;
    private static readonly HttpClient Http = CreateClient();

    public ToolSpec Spec { get; } = new("read_webpage",
        "Open a web page (http or https) and read its main text. Use it on links from web_search or links the user gave.",
        """{"type":"object","properties":{"url":{"type":"string","description":"The full address of the page."}},"required":["url"]}""");

    public string Describe(JsonElement arguments)
    {
        var url = Agent.Str(arguments, "url") ?? "";
        return Loc.F("Ai.StepRead", Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url);
    }

    public async Task<ToolResult> RunAsync(JsonElement arguments, CancellationToken cancel)
    {
        var url = Agent.Str(arguments, "url")?.Trim() ?? "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https")) throw new ArgumentException("Only http and https addresses can be read.");
        await RefuseLocal(uri, cancel);
        using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancel);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancel);
        var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancel)) > 0 && buffer.Length < MaxBytes) buffer.Write(chunk, 0, read);
        var charset = response.Content.Headers.ContentType?.CharSet;
        Encoding encoding;
        try { encoding = string.IsNullOrEmpty(charset) ? Encoding.UTF8 : Encoding.GetEncoding(charset.Trim('"')); }
        catch (ArgumentException) { encoding = Encoding.UTF8; }
        var raw = encoding.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        var html = (response.Content.Headers.ContentType?.MediaType ?? "text/html").Contains("html", StringComparison.OrdinalIgnoreCase);
        var title = html && TitleTag().Match(raw) is { Success: true } m ? WebUtility.HtmlDecode(m.Groups[1].Value).Trim() : "";
        var text = html ? Readable(raw) : raw;
        if (text.Length > MaxChars) text = text[..MaxChars] + "\n…(truncated)";
        return new ToolResult((title.Length > 0 ? "Title: " + title + "\n\n" : "") + text, title.Length > 0 ? title : uri.Host);
    }

    // Pages on this computer or the local network are off limits: the model's links come from the internet.
    private static async Task RefuseLocal(Uri uri, CancellationToken cancel)
    {
        var addresses = IPAddress.TryParse(uri.Host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(uri.Host, cancel);
        foreach (var address in addresses)
        {
            var a = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
            var b = a.GetAddressBytes();
            var local = IPAddress.IsLoopback(a) || a.IsIPv6LinkLocal || a.IsIPv6SiteLocal || a.IsIPv6UniqueLocal
                || (a.AddressFamily == AddressFamily.InterNetwork && (b[0] == 10 || b[0] == 127 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254) || b[0] == 0));
            if (local) throw new InvalidOperationException("Addresses on the local network are not read.");
        }
    }

    private static string Readable(string html)
    {
        var text = Hidden().Replace(html, " ");
        text = Breaks().Replace(text, "\n");
        text = Tags().Replace(text, " ");
        text = WebUtility.HtmlDecode(text);
        text = Spaces().Replace(text, " ");
        text = BlankLines().Replace(text, "\n\n");
        return string.Join('\n', text.Split('\n').Select(l => l.Trim())).Trim();
    }

    private static HttpClient CreateClient()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); // pages served as GBK, Big5, Shift-JIS ...
        var client = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All, MaxAutomaticRedirections = 5 }) { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Perch/1.0");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9,en;q=0.8");
        return client;
    }

    [GeneratedRegex(@"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)] private static partial Regex TitleTag();
    [GeneratedRegex(@"<(script|style|noscript|svg|head|nav|footer|iframe)\b.*?</\1\s*>|<!--.*?-->", RegexOptions.IgnoreCase | RegexOptions.Singleline)] private static partial Regex Hidden();
    [GeneratedRegex(@"<(br|/p|/div|/li|/h[1-6]|/tr|/section|/article)\b[^>]*>", RegexOptions.IgnoreCase)] private static partial Regex Breaks();
    [GeneratedRegex(@"<[^>]+>")] private static partial Regex Tags();
    [GeneratedRegex(@"[ \t\f\v ]+")] private static partial Regex Spaces();
    [GeneratedRegex(@"\n\s*\n\s*\n+")] private static partial Regex BlankLines();
}
