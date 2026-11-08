using System.Net.Http;
using System.Text.Json;

namespace Pankin.Core.Launcher;

/// <summary>
/// 网页搜索引擎自动补全与联想词网络服务
/// </summary>
public sealed class WebSuggestions : IWebSuggestions, IDisposable
{
    private readonly HttpClient _client;
    private readonly Func<bool> _enabled;
    private readonly TimeProvider _clock;
    private readonly object _lock = new();
    private readonly Dictionary<(string Site, string Query), (DateTimeOffset Time, string[] Items)> _cache = [];
    private bool _disposed;

    public WebSuggestions(Func<bool>? enabled = null, HttpMessageHandler? handler = null, TimeProvider? clock = null)
    {
        _enabled = enabled ?? (() => true);
        _clock = clock ?? TimeProvider.System;

        var httpHandler = handler ?? new HttpClientHandler
        {
            UseCookies = false,
            UseDefaultCredentials = false,
            AllowAutoRedirect = false
        };

        _client = new HttpClient(httpHandler)
        {
            Timeout = TimeSpan.FromSeconds(1)
        };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36");
    }

    public async Task<IReadOnlyList<string>> SuggestAsync(SearchEngine engine, string query, CancellationToken cancellationToken)
    {
        if (!_enabled() || string.IsNullOrWhiteSpace(query) || cancellationToken.IsCancellationRequested)
            return Array.Empty<string>();

        if (!Uri.TryCreate(engine.UrlTemplate, UriKind.Absolute, out var uri))
            return Array.Empty<string>();

        var host = uri.Host.ToLowerInvariant();
        string? site = host switch
        {
            "bing.com" or "www.bing.com" or "cn.bing.com" => "bing",
            "baidu.com" or "www.baidu.com" => "baidu",
            _ => null
        };

        if (site == null)
            return Array.Empty<string>();

        var trimmedQuery = query.Trim();
        var cacheKey = (site, trimmedQuery);

        lock (_lock)
        {
            if (_cache.TryGetValue(cacheKey, out var saved) && _clock.GetUtcNow() - saved.Time < TimeSpan.FromMinutes(1))
            {
                return saved.Items;
            }
        }

        string[] result;
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(1));

            string requestUrl = site == "bing"
                ? "https://api.bing.com/osjson.aspx?query=" + Uri.EscapeDataString(trimmedQuery)
                : "https://suggestion.baidu.com/su?ie=utf-8&oe=utf-8&action=opensearch&wd=" + Uri.EscapeDataString(trimmedQuery);

            using var response = await _client.GetAsync(requestUrl, timeoutCts.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var responseBody = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
            using var json = JsonDocument.Parse(responseBody);
            var root = json.RootElement;

            result = root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 1 && root[1].ValueKind == JsonValueKind.Array
                ? root[1].EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Distinct(StringComparer.Ordinal)
                    .Take(8)
                    .ToArray()
                : Array.Empty<string>();
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or IOException)
        {
            result = Array.Empty<string>();
        }

        if (cancellationToken.IsCancellationRequested || !_enabled())
            return Array.Empty<string>();

        lock (_lock)
        {
            if (_cache.Count >= 128)
            {
                _cache.Clear();
            }
            _cache[cacheKey] = (_clock.GetUtcNow(), result);
        }

        return result;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _client.Dispose();
    }
}
