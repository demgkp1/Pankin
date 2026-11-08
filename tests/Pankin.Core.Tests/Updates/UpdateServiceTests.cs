using System.Net;
using System.Text;
using Pankin.Core.Updates;
using Xunit;

namespace Pankin.Core.Tests.Updates;

public class UpdateServiceTests
{
    private sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        public int StatusCode { get; set; } = 200;
        public string? ResponseBody { get; set; }
        public int Requests { get; private set; }
        public string? LastUserAgent { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            LastUserAgent = request.Headers.UserAgent.ToString();

            var response = new HttpResponseMessage((HttpStatusCode)StatusCode);

            if (StatusCode is 403 or 429)
            {
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(3600));
            }

            if (!string.IsNullOrEmpty(ResponseBody))
            {
                response.Content = new StringContent(ResponseBody, Encoding.UTF8, "application/json");
            }

            return Task.FromResult(response);
        }
    }

    [Fact]
    public async Task CheckAsync_WhenNewerVersionAvailable_ShouldSetStateToAvailable()
    {
        var handler = new MockHttpMessageHandler
        {
            StatusCode = 200,
            ResponseBody = """
                {
                  "tag_name": "v0.1.100",
                  "draft": false,
                  "prerelease": false,
                  "body": "## 新增\n- 划词超级面板\n\n欢迎体验新版本！",
                  "published_at": "2026-11-07T12:00:00Z",
                  "html_url": "https://github.com/LBEILC/Pankin/releases/tag/v0.1.100",
                  "assets": []
                }
                """
        };

        string? openedUrl = null;
        using var service = new UpdateService(
            currentVersion: "0.1.99",
            openReleaseUrlOrInstall: url => openedUrl = url,
            handler: handler);

        Assert.Equal(UpdateState.Idle, service.State);

        int changes = 0;
        service.Changed += (_, _) => changes++;

        await service.CheckAsync();

        Assert.Equal(UpdateState.Available, service.State);
        Assert.NotNull(service.Latest);
        Assert.Equal("0.1.100", service.Latest.Version);
        Assert.Equal("https://github.com/LBEILC/Pankin/releases/tag/v0.1.100", service.Latest.PageUrl);
        Assert.Contains("划词超级面板", service.Latest.Notes);
        Assert.True(changes > 0);
        Assert.Contains("Pankin/0.1.99", handler.LastUserAgent);

        // 测试下载与就绪
        await service.DownloadAsync();
        Assert.Equal(UpdateState.Ready, service.State);
        Assert.Equal(1.0, service.DownloadProgress);

        // 测试在 Linux 下 InstallAndRestart 打开发布页面
        service.InstallAndRestart();
        Assert.Equal("https://github.com/LBEILC/Pankin/releases/tag/v0.1.100", openedUrl);
    }

    [Fact]
    public async Task CheckAsync_WhenVersionIsSameOrOlder_ShouldSetUpToDate()
    {
        var handler = new MockHttpMessageHandler
        {
            StatusCode = 200,
            ResponseBody = """
                {
                  "tag_name": "v0.1.99",
                  "draft": false,
                  "prerelease": false,
                  "body": "当前稳定版",
                  "published_at": "2026-11-01T00:00:00Z",
                  "html_url": "https://github.com/LBEILC/Pankin/releases/tag/v0.1.99"
                }
                """
        };

        using var service = new UpdateService(
            currentVersion: "0.1.99",
            handler: handler);

        await service.CheckAsync();

        Assert.Equal(UpdateState.UpToDate, service.State);
        Assert.Null(service.Latest);
    }

    [Fact]
    public async Task CheckAsync_WhenDraftOrPrerelease_ShouldIgnoreAndSetUpToDate()
    {
        var handler = new MockHttpMessageHandler
        {
            StatusCode = 200,
            ResponseBody = """
                {
                  "tag_name": "v0.2.000",
                  "draft": true,
                  "prerelease": false,
                  "body": "内部测试草稿",
                  "published_at": "2026-11-07T00:00:00Z",
                  "html_url": "https://github.com/LBEILC/Pankin/releases/tag/v0.2.000"
                }
                """
        };

        using var service = new UpdateService(
            currentVersion: "0.1.99",
            handler: handler);

        await service.CheckAsync();

        Assert.Equal(UpdateState.UpToDate, service.State);
    }

    [Fact]
    public async Task CheckAsync_WhenRateLimited_ShouldSetFailedAndNotRetryImmediately()
    {
        var handler = new MockHttpMessageHandler
        {
            StatusCode = 429
        };

        using var service = new UpdateService(
            currentVersion: "0.1.99",
            handler: handler);

        await service.CheckAsync();
        Assert.Equal(UpdateState.Failed, service.State);
        Assert.Equal(UpdateStep.Check, service.FailedStep);
        Assert.Contains("限流", service.Error);

        int initialRequests = handler.Requests;
        await service.CheckAsync();
        Assert.Equal(initialRequests, handler.Requests); // 冷却期内不发送重复请求
    }

    [Fact]
    public void PlainNotes_ShouldStripMarkdownAndLimitLength()
    {
        string raw = "### 标题\n[链接描述](https://example.com)\n* **加粗内容**\n> 引用块";
        string plain = UpdateService.PlainNotes(raw);

        Assert.DoesNotContain("###", plain);
        Assert.DoesNotContain("https://example.com", plain);
        Assert.Contains("链接描述", plain);
        Assert.Contains("加粗内容", plain);

        string longText = new('a', 2500);
        string limited = UpdateService.PlainNotes(longText);
        Assert.Equal(2000, limited.Length);
    }
}
