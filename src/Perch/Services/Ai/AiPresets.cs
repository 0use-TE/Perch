namespace Perch.Services.Ai;

// Well-known OpenAI-compatible services: picking one fills in the address (and a few models); the user only adds a key.
// "Fetch models" (also run automatically once a key is entered) replaces the model list with what the service offers.
public sealed record AiPreset(string Name, string BaseUrl, string[] Models, bool NeedsKey = true);

public static class AiPresets
{
    public static readonly AiPreset[] All =
    [
        new("DeepSeek", "https://api.deepseek.com", ["deepseek-chat", "deepseek-reasoner"]),
        new("OpenAI", "https://api.openai.com/v1", []),
        new("Claude", "https://api.anthropic.com/v1", ["claude-opus-5-5", "claude-sonnet-5-5", "claude-haiku-5-5"]),
        new("Gemini", "https://generativelanguage.googleapis.com/v1beta/openai", []),
        new("Grok", "https://api.x.ai/v1", []),
        new("通义千问", "https://dashscope.aliyuncs.com/compatible-mode/v1", ["qwen-max", "qwen-plus", "qwen-turbo"]),
        new("Kimi", "https://api.moonshot.cn/v1", []),
        new("智谱 GLM", "https://open.bigmodel.cn/api/paas/v4", []),
        new("豆包", "https://ark.cn-beijing.volces.com/api/v3", []),
        new("硅基流动", "https://api.siliconflow.cn/v1", []),
        new("OpenRouter", "https://openrouter.ai/api/v1", []),
        new("Ollama", "http://localhost:11434/v1", [], NeedsKey: false),
        new("LM Studio", "http://localhost:1234/v1", [], NeedsKey: false),
    ];
}
