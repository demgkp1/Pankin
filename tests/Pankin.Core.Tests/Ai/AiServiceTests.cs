using System.Net;
using System.Text;
using System.Text.Json;
using Pankin.Core.Ai;
using Xunit;

namespace Pankin.Core.Tests.Ai;

public class AiServiceTests
{
    private sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        public HttpRequestMessage? CapturedRequest { get; private set; }
        public string? CapturedRequestBody { get; private set; }
        public Func<HttpRequestMessage, HttpResponseMessage>? ResponseFactory { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CapturedRequest = request;
            if (request.Content != null)
            {
                CapturedRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            if (ResponseFactory != null)
            {
                return ResponseFactory(request);
            }

            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("data: [DONE]\n\n", Encoding.UTF8, "text/event-stream")
            };
            return resp;
        }
    }

    [Fact]
    public void Config_Defaults_AreSetProperly()
    {
        var service = new AiService();
        Assert.NotNull(service.Config);
        Assert.Equal("https://api.deepseek.com", service.Config.BaseUrl);
        Assert.Equal("deepseek-chat", service.Config.Model);
        Assert.Equal(AiApiFormat.OpenAi, service.Config.Format);
        Assert.Null(service.Config.ApiKey);
    }

    [Fact]
    public void UpdateConfig_UpdatesCurrentConfig()
    {
        var service = new AiService();
        var newConfig = new AiConfig
        {
            BaseUrl = "https://custom.api.com",
            ApiKey = "sk-custom-123456",
            Model = "custom-model",
            Format = AiApiFormat.Anthropic
        };

        service.UpdateConfig(newConfig);

        Assert.Equal("https://custom.api.com", service.Config.BaseUrl);
        Assert.Equal("sk-custom-123456", service.Config.ApiKey);
        Assert.Equal("custom-model", service.Config.Model);
        Assert.Equal(AiApiFormat.Anthropic, service.Config.Format);
    }

    [Fact]
    public void UpdateConfig_Null_ThrowsArgumentNullException()
    {
        var service = new AiService();
        Assert.Throws<ArgumentNullException>(() => service.UpdateConfig(null!));
    }

    [Fact]
    public async Task StreamChatAsync_MissingApiKey_ThrowsAiCredentialException()
    {
        var service = new AiService(new AiConfig { ApiKey = null });
        var messages = new List<AiChatMessage> { new(AiChatRole.User, "Hello") };

        await Assert.ThrowsAsync<AiCredentialException>(async () =>
        {
            await foreach (var _ in service.StreamChatAsync(messages))
            {
            }
        });
    }

    [Fact]
    public async Task StreamChatAsync_OpenAiFormat_BuildsCorrectRequestAndParsesStream()
    {
        var handler = new MockHttpMessageHandler
        {
            ResponseFactory = _ =>
            {
                string sse = """
                    data: {"choices":[{"delta":{"content":"Hello"}}]}

                    data: {"choices":[{"delta":{"content":" world"}}]}

                    data: [DONE]

                    """;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
                };
            }
        };

        using var client = new HttpClient(handler);
        var config = new AiConfig
        {
            BaseUrl = "https://api.deepseek.com",
            ApiKey = "test-sk-key",
            Model = "deepseek-chat",
            Format = AiApiFormat.OpenAi
        };

        var service = new AiService(config, client);
        var messages = new List<AiChatMessage> { new(AiChatRole.User, "Ping") };

        var chunks = new List<string>();
        await foreach (var chunk in service.StreamChatAsync(messages))
        {
            chunks.Add(chunk);
        }

        // 验证 SSE 流解析结果
        Assert.Equal(["Hello", " world"], chunks);

        // 验证请求 URL 与认证头
        Assert.NotNull(handler.CapturedRequest);
        Assert.Equal("https://api.deepseek.com/v1/chat/completions", handler.CapturedRequest!.RequestUri!.ToString());
        Assert.Equal("Bearer", handler.CapturedRequest.Headers.Authorization?.Scheme);
        Assert.Equal("test-sk-key", handler.CapturedRequest.Headers.Authorization?.Parameter);

        // 验证请求体包含模型与消息
        Assert.NotNull(handler.CapturedRequestBody);
        using var doc = JsonDocument.Parse(handler.CapturedRequestBody!);
        Assert.Equal("deepseek-chat", doc.RootElement.GetProperty("model").GetString());
        Assert.True(doc.RootElement.GetProperty("stream").GetBoolean());

        var msgArray = doc.RootElement.GetProperty("messages");
        Assert.True(msgArray.GetArrayLength() >= 2); // 包含注入的 system prompt 与 user prompt
    }

    [Fact]
    public async Task StreamChatAsync_AnthropicFormat_BuildsCorrectHeadersAndPayload()
    {
        var handler = new MockHttpMessageHandler
        {
            ResponseFactory = _ =>
            {
                string sse = """
                    data: {"type":"content_block_delta","delta":{"type":"text_delta","text":"Bonjour"}}

                    data: {"type":"message_stop"}

                    """;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
                };
            }
        };

        using var client = new HttpClient(handler);
        var config = new AiConfig
        {
            BaseUrl = "https://api.anthropic.com",
            ApiKey = "anthropic-key-123",
            Model = "claude-3-5-sonnet-20241022",
            Format = AiApiFormat.Anthropic
        };

        var service = new AiService(config, client);
        var messages = new List<AiChatMessage> { new(AiChatRole.User, "Comment ça va?") };

        var chunks = new List<string>();
        await foreach (var chunk in service.StreamChatAsync(messages))
        {
            chunks.Add(chunk);
        }

        Assert.Equal(["Bonjour"], chunks);

        // 验证 Anthropic 请求头
        Assert.NotNull(handler.CapturedRequest);
        Assert.Equal("https://api.anthropic.com/v1/messages", handler.CapturedRequest!.RequestUri!.ToString());
        Assert.Equal("anthropic-key-123", handler.CapturedRequest.Headers.GetValues("x-api-key").First());
        Assert.Equal("2023-06-01", handler.CapturedRequest.Headers.GetValues("anthropic-version").First());

        // 验证 Anthropic 请求体
        Assert.NotNull(handler.CapturedRequestBody);
        using var doc = JsonDocument.Parse(handler.CapturedRequestBody!);
        Assert.Equal("claude-3-5-sonnet-20241022", doc.RootElement.GetProperty("model").GetString());
        Assert.Equal(8192, doc.RootElement.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public async Task StreamChatAsync_401Unauthorized_ThrowsAiCredentialException()
    {
        var handler = new MockHttpMessageHandler
        {
            ResponseFactory = _ =>
            {
                string errJson = """{"error":{"message":"Invalid API key","code":"invalid_api_key"}}""";
                return new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent(errJson, Encoding.UTF8, "application/json")
                };
            }
        };

        using var client = new HttpClient(handler);
        var service = new AiService(new AiConfig { ApiKey = "bad-key" }, client);

        await Assert.ThrowsAsync<AiCredentialException>(async () =>
        {
            await foreach (var _ in service.StreamChatAsync([new(AiChatRole.User, "hi")]))
            {
            }
        });
    }

    [Fact]
    public async Task StreamChatAsync_429TooManyRequests_ThrowsAiQuotaException()
    {
        var handler = new MockHttpMessageHandler
        {
            ResponseFactory = _ =>
            {
                string errJson = """{"error":{"message":"Rate limit reached","code":"rate_limit_exceeded"}}""";
                return new HttpResponseMessage((HttpStatusCode)429)
                {
                    Content = new StringContent(errJson, Encoding.UTF8, "application/json")
                };
            }
        };

        using var client = new HttpClient(handler);
        var service = new AiService(new AiConfig { ApiKey = "valid-key" }, client);

        await Assert.ThrowsAsync<AiQuotaException>(async () =>
        {
            await foreach (var _ in service.StreamChatAsync([new(AiChatRole.User, "hi")]))
            {
            }
        });
    }

    [Fact]
    public void AiPresets_Find_LocatesExistingPresets()
    {
        var deepseek = AiPresets.Find("deepseek");
        Assert.NotNull(deepseek);
        Assert.Equal("DeepSeek", deepseek.Name);
        Assert.Equal("https://api.deepseek.com", deepseek.BaseUrl);

        var anthropic = AiPresets.Find("anthropic");
        Assert.NotNull(anthropic);
        Assert.Equal(AiApiFormat.Anthropic, anthropic.Format);

        Assert.Null(AiPresets.Find("non-existent-preset"));
    }
}
