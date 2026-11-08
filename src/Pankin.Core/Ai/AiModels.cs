namespace Pankin.Core.Ai;

public enum AiChatRole
{
    System,
    User,
    Assistant
}

public sealed record AiChatMessage(AiChatRole Role, string Content);

public enum AiApiFormat
{
    OpenAi,
    Anthropic
}

public enum AiReasoningEffort
{
    Default,
    Low,
    Medium,
    High
}

public sealed record AiPreset(
    string Id,
    string Name,
    string BaseUrl,
    string Model,
    string? TranslationModel,
    AiApiFormat Format = AiApiFormat.OpenAi);

public static class AiPresets
{
    public static IReadOnlyList<AiPreset> All { get; } =
    [
        new("deepseek", "DeepSeek", "https://api.deepseek.com", "deepseek-chat", null),
        new("qwen", "通义千问", "https://dashscope.aliyuncs.com/compatible-mode/v1", "qwen-plus", null),
        new("zhipu", "智谱 GLM", "https://open.bigmodel.cn/api/paas/v4", "glm-4-flash", null),
        new("openai", "OpenAI", "https://api.openai.com/v1", "gpt-4o-mini", null),
        new("anthropic", "Anthropic", "https://api.anthropic.com", "claude-3-5-sonnet-20241022", null, AiApiFormat.Anthropic),
    ];

    public static AiPreset? Find(string? id) => All.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
}

public sealed record AiConfig
{
    public string BaseUrl { get; init; } = "https://api.deepseek.com";
    public string? ApiKey { get; init; }
    public string Model { get; init; } = "deepseek-chat";
    public string? TranslationModel { get; init; }
    public AiApiFormat Format { get; init; } = AiApiFormat.OpenAi;
    public AiReasoningEffort ReasoningEffort { get; init; } = AiReasoningEffort.Default;
    public string DefaultTargetLanguage { get; init; } = "auto";
    public int TimeoutSeconds { get; init; } = 60;
}

public sealed record AiAction(string Id, string Name, string Glyph, string PromptTemplate);

public abstract class AiException : Exception
{
    protected AiException(string message, Exception? inner = null) : base(message, inner) { }
}

public sealed class AiNetworkException : AiException
{
    public AiNetworkException(string message, Exception? inner = null) : base(message, inner) { }
}

public sealed class AiCredentialException : AiException
{
    public AiCredentialException(string message) : base(message) { }
}

public sealed class AiQuotaException : AiException
{
    public AiQuotaException(string message) : base(message) { }
}

public sealed class AiSignInRequiredException : AiException
{
    public AiSignInRequiredException(string message) : base(message) { }
}
