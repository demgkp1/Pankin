using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Pankin.Core.Ai;

public interface IAiService
{
    AiConfig Config { get; }
    void UpdateConfig(AiConfig config);

    IAsyncEnumerable<string> StreamChatAsync(
        IReadOnlyList<AiChatMessage> messages,
        string? modelOverride = null,
        CancellationToken ct = default);

    IAsyncEnumerable<string> StreamTranslateAsync(
        string text,
        string targetLanguage = "auto",
        string? modelOverride = null,
        CancellationToken ct = default);

    Task<bool> TestConnectionAsync(CancellationToken ct = default);
}

public sealed class AiService : IAiService, IDisposable
{
    private const string SystemPrompt = "你在 Linux 桌面效率工具 Pankin 中回答。请回答简洁直接；除非用户要求，否则使用提问相同的语言；尽量使用排版清晰的纯文本或 Markdown。";

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private AiConfig _config;
    private readonly object _lock = new();

    public AiConfig Config
    {
        get
        {
            lock (_lock) return _config;
        }
    }

    public AiService(AiConfig? config = null, HttpClient? httpClient = null)
    {
        _config = config ?? new AiConfig();
        if (httpClient != null)
        {
            _httpClient = httpClient;
            _ownsHttpClient = false;
        }
        else
        {
            var handler = new SocketsHttpHandler
            {
                PooledConnectionIdleTimeout = TimeSpan.FromSeconds(60),
                AllowAutoRedirect = true
            };
            _httpClient = new HttpClient(handler);
            _ownsHttpClient = true;
        }
    }

    public void UpdateConfig(AiConfig config)
    {
        lock (_lock)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
        }
    }

    public async IAsyncEnumerable<string> StreamChatAsync(
        IReadOnlyList<AiChatMessage> messages,
        string? modelOverride = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var config = Config;
        if (string.IsNullOrWhiteSpace(config.ApiKey))
            throw new AiCredentialException("未配置 AI API Key");

        var model = modelOverride ?? config.Model;
        using var request = CreateChatRequest(config, messages, model);

        using var response = await SendRequestAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            await HandleErrorResponseAsync(response, ct).ConfigureAwait(false);
        }

        using var responseStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(responseStream, Encoding.UTF8);

        if (config.Format == AiApiFormat.Anthropic)
        {
            await foreach (var chunk in ProcessAnthropicSseStreamAsync(reader, ct).ConfigureAwait(false))
            {
                yield return chunk;
            }
        }
        else
        {
            await foreach (var chunk in ProcessOpenAiSseStreamAsync(reader, ct).ConfigureAwait(false))
            {
                yield return chunk;
            }
        }
    }

    public IAsyncEnumerable<string> StreamTranslateAsync(
        string text,
        string targetLanguage = "auto",
        string? modelOverride = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return EmptyAsync();

        var config = Config;
        string resolvedTarget = ResolveTargetLanguage(text, targetLanguage);
        string langName = MapLanguageName(resolvedTarget);

        var prompt = $"把下面的文字翻译成{langName}。只输出译文，不要多余解释；保留原文的换行和格式：\n\n{text}";
        var messages = new List<AiChatMessage>
        {
            new(AiChatRole.User, prompt)
        };

        var model = modelOverride ?? config.TranslationModel ?? config.Model;
        return StreamChatAsync(messages, model, ct);
    }

    public async Task<bool> TestConnectionAsync(CancellationToken ct = default)
    {
        var config = Config;
        if (string.IsNullOrWhiteSpace(config.ApiKey) || string.IsNullOrWhiteSpace(config.BaseUrl))
            return false;

        var messages = new List<AiChatMessage>
        {
            new(AiChatRole.User, "Hi")
        };

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            await foreach (var _ in StreamChatAsync(messages, null, cts.Token).ConfigureAwait(false))
            {
                return true;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async IAsyncEnumerable<string> EmptyAsync()
    {
        await Task.CompletedTask;
        yield break;
    }

    private static string ResolveTargetLanguage(string text, string target)
    {
        if (!target.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return target;

        int total = 0;
        int han = 0;
        foreach (var r in text.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(r)) continue;
            total++;
            if (r.Value is (>= 0x3400 and <= 0x4dbf) or (>= 0x4e00 and <= 0x9fff) or (>= 0xf900 and <= 0xfaff))
                han++;
        }

        return total > 0 && han * 2 >= total ? "en" : "zh";
    }

    private static string MapLanguageName(string code) => code.ToLowerInvariant() switch
    {
        "zh" or "zh-cn" or "zh-hans" => "中文",
        "en" => "英文",
        "ja" => "日文",
        "ko" => "韩文",
        "fr" => "法文",
        "de" => "德文",
        "es" => "西班牙文",
        "ru" => "俄文",
        _ => "中文"
    };

    private HttpRequestMessage CreateChatRequest(AiConfig config, IReadOnlyList<AiChatMessage> messages, string model)
    {
        var uri = BuildEndpointUri(config.BaseUrl, config.Format);
        var request = new HttpRequestMessage(HttpMethod.Post, uri);

        if (config.Format == AiApiFormat.Anthropic)
        {
            request.Headers.Add("x-api-key", config.ApiKey);
            request.Headers.Add("anthropic-version", "2023-06-01");
            var body = BuildAnthropicPayload(messages, model, config.ReasoningEffort);
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        else
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);
            var body = BuildOpenAiPayload(messages, model, config.ReasoningEffort);
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        return request;
    }

    private static Uri BuildEndpointUri(string baseUrl, AiApiFormat format)
    {
        var trimmed = baseUrl.Trim().TrimEnd('/');
        if (format == AiApiFormat.Anthropic)
        {
            if (trimmed.EndsWith("/messages", StringComparison.OrdinalIgnoreCase))
                return new Uri(trimmed);
            if (trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
                return new Uri($"{trimmed}/messages");
            return new Uri($"{trimmed}/v1/messages");
        }
        else
        {
            if (trimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
                return new Uri(trimmed);
            if (trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
                return new Uri($"{trimmed}/chat/completions");
            return new Uri($"{trimmed}/v1/chat/completions");
        }
    }

    private static byte[] BuildOpenAiPayload(IReadOnlyList<AiChatMessage> messages, string model, AiReasoningEffort reasoning)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("model", model);
            writer.WriteBoolean("stream", true);

            if (reasoning != AiReasoningEffort.Default)
            {
                string effort = reasoning switch
                {
                    AiReasoningEffort.Low => "low",
                    AiReasoningEffort.Medium => "medium",
                    AiReasoningEffort.High => "high",
                    _ => "default"
                };
                writer.WriteString("reasoning_effort", effort);
            }

            writer.WriteStartArray("messages");

            // 注入系统角色提示
            writer.WriteStartObject();
            writer.WriteString("role", "system");
            writer.WriteString("content", SystemPrompt);
            writer.WriteEndObject();

            foreach (var msg in messages)
            {
                writer.WriteStartObject();
                writer.WriteString("role", msg.Role switch
                {
                    AiChatRole.System => "system",
                    AiChatRole.Assistant => "assistant",
                    _ => "user"
                });
                writer.WriteString("content", msg.Content);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    private static byte[] BuildAnthropicPayload(IReadOnlyList<AiChatMessage> messages, string model, AiReasoningEffort reasoning)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("model", model);
            writer.WriteBoolean("stream", true);
            writer.WriteNumber("max_tokens", 8192);
            writer.WriteString("system", SystemPrompt);

            if (reasoning != AiReasoningEffort.Default)
            {
                writer.WriteStartObject("thinking");
                writer.WriteString("type", "enabled");
                writer.WriteNumber("budget_tokens", reasoning switch
                {
                    AiReasoningEffort.Low => 1024,
                    AiReasoningEffort.Medium => 4096,
                    AiReasoningEffort.High => 8192,
                    _ => 2048
                });
                writer.WriteEndObject();
            }

            writer.WriteStartArray("messages");

            // Anthropic 要求相邻消息角色不同，因此做相邻合并
            for (int i = 0; i < messages.Count; i++)
            {
                var role = messages[i].Role == AiChatRole.Assistant ? "assistant" : "user";
                var contentBuilder = new StringBuilder(messages[i].Content);

                while (i + 1 < messages.Count && (messages[i + 1].Role == AiChatRole.Assistant ? "assistant" : "user") == role)
                {
                    i++;
                    contentBuilder.Append("\n\n").Append(messages[i].Content);
                }

                writer.WriteStartObject();
                writer.WriteString("role", role);
                writer.WriteString("content", contentBuilder.ToString());
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    private async Task<HttpResponseMessage> SendRequestAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new AiNetworkException($"网络连接失败: {ex.Message}", ex);
        }
    }

    private static async Task HandleErrorResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        string body = string.Empty;
        try
        {
            body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch { }

        int status = (int)response.StatusCode;
        string? errorCode = null;
        string? errorMessage = null;

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var errObj))
            {
                if (errObj.ValueKind == JsonValueKind.Object)
                {
                    if (errObj.TryGetProperty("message", out var msgProp)) errorMessage = msgProp.GetString();
                    if (errObj.TryGetProperty("code", out var codeProp)) errorCode = codeProp.GetString();
                    if (errObj.TryGetProperty("type", out var typeProp) && errorCode == null) errorCode = typeProp.GetString();
                }
                else if (errObj.ValueKind == JsonValueKind.String)
                {
                    errorMessage = errObj.GetString();
                }
            }
        }
        catch { }

        if (status is 401 or 403)
            throw new AiCredentialException($"AI 凭据无效或已过期 ({status}): {errorMessage ?? errorCode ?? "未授权"}");

        if (status == 429 || errorCode is "rate_limit_exceeded" or "insufficient_quota")
            throw new AiQuotaException($"AI 调用额度受限或不足 ({status}): {errorMessage ?? errorCode ?? "超出限额"}");

        if (status >= 500)
            throw new AiNetworkException($"AI 服务端不可用 ({status}): {errorMessage ?? "服务异常"}");

        throw new AiNetworkException($"AI 请求失败 ({status}): {errorMessage ?? body}");
    }

    private static async IAsyncEnumerable<string> ProcessOpenAiSseStreamAsync(
        StreamReader reader,
        [EnumeratorCancellation] CancellationToken ct)
    {
        while (!reader.EndOfStream && !ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(line)) continue;

            if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                var data = line[6..].Trim();
                if (data == "[DONE]") yield break;

                string? deltaText = null;
                try
                {
                    using var doc = JsonDocument.Parse(data);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("choices", out var choices) &&
                        choices.ValueKind == JsonValueKind.Array &&
                        choices.GetArrayLength() > 0)
                    {
                        var firstChoice = choices[0];
                        if (firstChoice.TryGetProperty("delta", out var delta) &&
                            delta.TryGetProperty("content", out var contentProp) &&
                            contentProp.ValueKind == JsonValueKind.String)
                        {
                            deltaText = contentProp.GetString();
                        }
                    }
                }
                catch (JsonException) { }

                if (!string.IsNullOrEmpty(deltaText))
                {
                    yield return deltaText;
                }
            }
        }
    }

    private static async IAsyncEnumerable<string> ProcessAnthropicSseStreamAsync(
        StreamReader reader,
        [EnumeratorCancellation] CancellationToken ct)
    {
        while (!reader.EndOfStream && !ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(line)) continue;

            if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                var data = line[6..].Trim();
                string? textDelta = null;
                bool isStop = false;

                try
                {
                    using var doc = JsonDocument.Parse(data);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("type", out var typeProp))
                    {
                        var typeStr = typeProp.GetString();
                        if (typeStr == "message_stop")
                        {
                            isStop = true;
                        }
                        else if (typeStr == "content_block_delta" &&
                                 root.TryGetProperty("delta", out var delta) &&
                                 delta.TryGetProperty("type", out var deltaType) &&
                                 deltaType.GetString() == "text_delta" &&
                                 delta.TryGetProperty("text", out var textProp))
                        {
                            textDelta = textProp.GetString();
                        }
                    }
                }
                catch (JsonException) { }

                if (isStop) yield break;

                if (!string.IsNullOrEmpty(textDelta))
                {
                    yield return textDelta;
                }
            }
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}
