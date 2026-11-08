using System.Net;
using System.Text;
using Pankin.Core.Launcher;
using Xunit;

namespace Pankin.Core.Tests.Launcher;

public class WebSuggestionsTests
{
    private sealed class MockHttpMessageHandler(string responseContent) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseContent, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }

    [Fact]
    public async Task SuggestAsync_BingResponse_ShouldParseSuggestions()
    {
        var json = """["bing", ["bing search", "bing image", "bing maps"]]""";
        var handler = new MockHttpMessageHandler(json);
        using var suggestions = new WebSuggestions(null, handler);

        var engine = new SearchEngine("bing", "Bing", "https://cn.bing.com/search?q={query}");
        var results = await suggestions.SuggestAsync(engine, "bing", CancellationToken.None);

        Assert.Equal(3, results.Count);
        Assert.Equal("bing search", results[0]);
        Assert.Equal("bing image", results[1]);
        Assert.Equal("bing maps", results[2]);
    }

    [Fact]
    public async Task SuggestAsync_UnsupportedDomain_ShouldReturnEmptyWithoutThrowing()
    {
        using var suggestions = new WebSuggestions();
        var engine = new SearchEngine("custom", "Custom", "https://example.com/search?q={query}");

        var results = await suggestions.SuggestAsync(engine, "test", CancellationToken.None);
        Assert.Empty(results);
    }
}
