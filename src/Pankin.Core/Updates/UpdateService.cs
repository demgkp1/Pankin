using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Pankin.Core.Updates;

/// <summary>
/// 跨平台软件内更新服务：从 GitHub Releases 检查新版本，在 Linux 下通过系统通知并在浏览器打开 Release 页面，严禁调用 Windows 原生安装器
/// </summary>
public sealed class UpdateService : IUpdateService, IDisposable
{
    public static readonly Uri DefaultSource = new("https://api.github.com/repos/demgkp1/Pankin/releases/latest");

    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly Uri _source;
    private readonly string _directory;
    private readonly Func<bool> _automatic;
    private readonly Action<string> _openReleaseUrlOrInstall;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Timer? _timer;

    private DateTimeOffset _retryAfter;
    private Uri? _assetUrl;
    private Uri? _sumsUrl;
    private string? _readyPath;
    private bool _disposed;
    private UpdateStep _activeStep;

    public string CurrentVersion { get; }
    public UpdateState State { get; private set; } = UpdateState.Idle;
    public UpdateStep? FailedStep { get; private set; }
    public UpdateRelease? Latest { get; private set; }
    public double DownloadProgress { get; private set; }
    public string? Error { get; private set; }
    public DateTimeOffset? LastChecked { get; private set; }

    public event EventHandler? Changed;

    public UpdateService(
        string currentVersion,
        string? dataDirectory = null,
        Func<bool>? automatic = null,
        Action<string>? openReleaseUrlOrInstall = null,
        Uri? customSource = null,
        HttpMessageHandler? handler = null,
        HttpClient? client = null,
        bool enablePeriodicCheck = false)
    {
        CurrentVersion = currentVersion;
        _directory = Path.Combine(dataDirectory ?? Path.Combine(Path.GetTempPath(), "pankin"), "updates");
        _automatic = automatic ?? (() => false);
        _openReleaseUrlOrInstall = openReleaseUrlOrInstall ?? (_ => { });
        _source = customSource ?? DefaultSource;

        if (client != null)
        {
            _http = client;
            _ownsHttpClient = false;
        }
        else if (handler != null)
        {
            _http = new HttpClient(handler, disposeHandler: false);
            _ownsHttpClient = true;
        }
        else
        {
            var defaultHandler = new HttpClientHandler
            {
                UseCookies = false,
                UseDefaultCredentials = false,
                AllowAutoRedirect = false
            };
            _http = new HttpClient(defaultHandler, disposeHandler: true);
            _ownsHttpClient = true;
        }

        if (!_http.DefaultRequestHeaders.Contains("User-Agent"))
        {
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("Pankin/" + currentVersion);
        }

        if (enablePeriodicCheck)
        {
            _timer = new Timer(_ => _ = TickAsync(), null, TimeSpan.FromMinutes(1), TimeSpan.FromHours(24));
        }

        RestoreReady();
    }

    private async Task TickAsync()
    {
        if (!_disposed && _automatic())
        {
            await CheckAsync(_lifetime.Token).ConfigureAwait(false);
        }
    }

    private void SetState(UpdateState state, string? error = null)
    {
        State = state;
        Error = error;
        FailedStep = state == UpdateState.Failed ? _activeStep : null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task CheckAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed || !await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return;

        using var linkedCancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        try
        {
            if (DateTimeOffset.UtcNow < _retryAfter)
            {
                SetState(UpdateState.Failed, "网络请求受限，请稍后重试");
                return;
            }

            _activeStep = UpdateStep.Check;
            SetState(UpdateState.Checking);
            LastChecked = DateTimeOffset.UtcNow;

            using var response = await GetAsync(_source, linkedCancel.Token).ConfigureAwait(false);
            var jsonString = await response.Content.ReadAsStringAsync(linkedCancel.Token).ConfigureAwait(false);

            using var doc = JsonDocument.Parse(jsonString);
            var root = doc.RootElement;

            Latest = null;
            _readyPath = null;
            _assetUrl = null;
            _sumsUrl = null;

            string tagName = root.GetProperty("tag_name").GetString() ?? "";
            string version = tagName.TrimStart('v', 'V');

            bool isDraft = root.TryGetProperty("draft", out var draftProp) && draftProp.GetBoolean();
            bool isPrerelease = root.TryGetProperty("prerelease", out var preProp) && preProp.GetBoolean();

            if (isDraft || isPrerelease || !VersionCompare.IsValid(version) ||
                VersionCompare.CompareVersions(version, CurrentVersion) <= 0)
            {
                SetState(UpdateState.UpToDate);
                return;
            }

            string body = root.TryGetProperty("body", out var bodyProp) ? bodyProp.GetString() ?? "" : "";
            var published = root.TryGetProperty("published_at", out var pubProp) && pubProp.TryGetDateTimeOffset(out var pubDate)
                ? pubDate
                : DateTimeOffset.UtcNow;
            string pageUrl = root.TryGetProperty("html_url", out var htmlProp)
                ? htmlProp.GetString() ?? _source.AbsoluteUri
                : _source.AbsoluteUri;

            long packageSize = 0;
            // 扫描资产中的安装包或校验码
            if (root.TryGetProperty("assets", out var assetsProp) && assetsProp.ValueKind == JsonValueKind.Array)
            {
                var files = assetsProp.EnumerateArray().ToArray();
                // 优先查找 Linux AppImage / deb / tar.gz 或测试包
                var package = files.FirstOrDefault(f =>
                {
                    var name = f.GetProperty("name").GetString() ?? "";
                    return name.Contains(version, StringComparison.OrdinalIgnoreCase) &&
                           (name.EndsWith(".AppImage", StringComparison.OrdinalIgnoreCase) ||
                            name.EndsWith(".deb", StringComparison.OrdinalIgnoreCase) ||
                            name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ||
                            name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
                });

                if (package.ValueKind != JsonValueKind.Undefined)
                {
                    if (package.TryGetProperty("size", out var sizeElem)) packageSize = sizeElem.GetInt64();
                    if (package.TryGetProperty("browser_download_url", out var downUrl))
                    {
                        _assetUrl = ValidateUrl(downUrl.GetString()!);
                    }
                }

                var sumsFile = files.FirstOrDefault(f =>
                {
                    var name = f.GetProperty("name").GetString() ?? "";
                    return name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase) ||
                           name.Equals("checksums.txt", StringComparison.OrdinalIgnoreCase);
                });

                if (sumsFile.ValueKind != JsonValueKind.Undefined &&
                    sumsFile.TryGetProperty("browser_download_url", out var sumsUrl))
                {
                    _sumsUrl = ValidateUrl(sumsUrl.GetString()!);
                }
            }

            var structuredNotes = ReleaseNotesCatalog.ParseMarkdown(body, version, published);
            Latest = new UpdateRelease(
                Version: version,
                Notes: PlainNotes(body),
                PublishedAt: published,
                SizeBytes: packageSize,
                PageUrl: pageUrl,
                Details: structuredNotes);

            SetState(UpdateState.Available);

            if (_automatic())
            {
                await DownloadCore(linkedCancel.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            SetState(linkedCancel.IsCancellationRequested
                ? (Latest == null ? UpdateState.Idle : UpdateState.Available)
                : UpdateState.Failed,
                linkedCancel.IsCancellationRequested ? null : "检查更新超时");
        }
        catch (Exception ex)
        {
            SetState(UpdateState.Failed, MapErrorMessage(ex));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DownloadAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed || !await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return;

        using var linkedCancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        try
        {
            if (Latest != null)
            {
                await DownloadCore(linkedCancel.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            SetState(linkedCancel.IsCancellationRequested ? UpdateState.Available : UpdateState.Failed,
                linkedCancel.IsCancellationRequested ? null : "下载更新超时");
        }
        catch (Exception ex)
        {
            SetState(UpdateState.Failed, MapErrorMessage(ex));
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task DownloadCore(CancellationToken token)
    {
        _activeStep = UpdateStep.Download;
        if (DateTimeOffset.UtcNow < _retryAfter)
        {
            SetState(UpdateState.Failed, "网络请求受限，请稍后重试");
            return;
        }

        Directory.CreateDirectory(_directory);
        DownloadProgress = 0;
        SetState(UpdateState.Downloading);

        // 若无特定资产下载或为 Linux 网页模式，直接就绪
        if (_assetUrl == null || _sumsUrl == null)
        {
            DownloadProgress = 1.0;
            SetState(UpdateState.Ready);
            return;
        }

        string fileName = Path.GetFileName(_assetUrl.LocalPath);
        string target = Path.Combine(_directory, fileName);
        string partial = target + ".partial";

        try
        {
            using var checksumResponse = await GetAsync(_sumsUrl, token).ConfigureAwait(false);
            string checksumText = await checksumResponse.Content.ReadAsStringAsync(token).ConfigureAwait(false);

            var match = checksumText.Split('\n')
                .Select(line => Regex.Match(line.Trim(), @"^([a-fA-F0-9]{64})\s+\*?(.+)$"))
                .FirstOrDefault(m => m.Success && m.Groups[2].Value == fileName);

            if (match == null || !match.Success)
            {
                throw new InvalidDataException("发布包校验和不存在或格式错误");
            }

            string expectedHash = match.Groups[1].Value;

            if (File.Exists(target) && await ValidateChecksum(target, expectedHash, token).ConfigureAwait(false))
            {
                CacheReady(target, expectedHash);
                return;
            }

            if (File.Exists(target)) File.Delete(target);

            using var assetResponse = await GetAsync(_assetUrl, token).ConfigureAwait(false);
            await using (var input = await assetResponse.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
            await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
            {
                byte[] buffer = new byte[65536];
                long received = 0;
                double notified = 0;
                var watch = Stopwatch.StartNew();
                int read;

                while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    received += read;

                    double progress = Latest!.SizeBytes > 0 ? Math.Min(1.0, (double)received / Latest.SizeBytes) : 0;
                    if (progress - notified >= 0.01 || watch.ElapsedMilliseconds >= 200)
                    {
                        DownloadProgress = progress;
                        notified = progress;
                        watch.Restart();
                        Changed?.Invoke(this, EventArgs.Empty);
                    }
                }
            }

            if (!await ValidateChecksum(partial, expectedHash, token).ConfigureAwait(false))
            {
                throw new InvalidDataException("下载文件哈希校验失败");
            }

            File.Move(partial, target, true);
            CacheReady(target, expectedHash);
        }
        finally
        {
            if (File.Exists(partial))
            {
                try { File.Delete(partial); } catch { }
            }
        }
    }

    public void InstallAndRestart()
    {
        if (_disposed || State != UpdateState.Ready) return;

        _activeStep = UpdateStep.Install;
        try
        {
            // 在 Linux 平台下无 Windows MSI 安装包：拉起浏览器打开 Release 页面并触发系统通知
            string destination = !string.IsNullOrEmpty(_readyPath) && File.Exists(_readyPath)
                ? _readyPath
                : (Latest?.PageUrl ?? "https://github.com/demgkp1/Pankin/releases");

            _openReleaseUrlOrInstall(destination);
        }
        catch (Exception ex)
        {
            SetState(UpdateState.Failed, $"启动更新失败: {ex.Message}");
        }
    }

    private async Task<bool> ValidateChecksum(string path, string expectedHash, CancellationToken token)
    {
        if (Latest != null && Latest.SizeBytes > 0 && new FileInfo(path).Length != Latest.SizeBytes)
        {
            return false;
        }

        await using var stream = File.OpenRead(path);
        var hashBytes = await SHA256.HashDataAsync(stream, token).ConfigureAwait(false);
        return Convert.ToHexString(hashBytes).Equals(expectedHash, StringComparison.OrdinalIgnoreCase);
    }

    private void CacheReady(string path, string hash)
    {
        _readyPath = path;
        DownloadProgress = 1.0;
        SetState(UpdateState.Ready);
    }

    private void RestoreReady()
    {
        // 尝试恢复先前的下载完成状态
        try
        {
            if (Directory.Exists(_directory))
            {
                var files = Directory.GetFiles(_directory);
                if (files.Length > 0)
                {
                    _readyPath = files[0];
                }
            }
        }
        catch { }
    }

    private async Task<HttpResponseMessage> GetAsync(Uri url, CancellationToken token)
    {
        for (int redirects = 0; redirects < 6; redirects++)
        {
            var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);

            if ((int)response.StatusCode is 403 or 429)
            {
                _retryAfter = DateTimeOffset.UtcNow.AddHours(1);
                if (response.Headers.RetryAfter?.Date is { } date && date > _retryAfter)
                {
                    _retryAfter = date;
                }
                else if (response.Headers.RetryAfter?.Delta is { } delay && DateTimeOffset.UtcNow + delay > _retryAfter)
                {
                    _retryAfter = DateTimeOffset.UtcNow + delay;
                }
                else if (response.Headers.TryGetValues("X-RateLimit-Reset", out var resetValues) &&
                         long.TryParse(resetValues.FirstOrDefault(), out long resetSeconds))
                {
                    var resetDate = DateTimeOffset.FromUnixTimeSeconds(resetSeconds);
                    if (resetDate > _retryAfter) _retryAfter = resetDate;
                }

                response.Dispose();
                throw new HttpRequestException("请求过于频繁受限", null, HttpStatusCode.TooManyRequests);
            }

            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location == null) throw new InvalidDataException("重定向缺少目标 URL");
                url = ValidateUrl(new Uri(url, location).AbsoluteUri);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                var status = response.StatusCode;
                response.Dispose();
                throw new HttpRequestException($"网络请求失败: {status}", null, status);
            }

            return response;
        }

        throw new HttpRequestException("重定向层数过多");
    }

    private Uri ValidateUrl(string text)
    {
        var uri = new Uri(text);
        bool isTest = _source.Host is "127.0.0.1" or "localhost";
        bool allowed = isTest
            ? (uri.Scheme is "http" or "https" && uri.Host is "127.0.0.1" or "localhost")
            : (uri.Scheme == "https" && (uri.Host == "github.com" || uri.Host == "api.github.com" ||
                                         uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase)));

        if (!allowed) throw new InvalidDataException($"非法重定向或下载链接域: {uri.Host}");
        return uri;
    }

    public static string PlainNotes(string notes)
    {
        if (string.IsNullOrWhiteSpace(notes)) return string.Empty;

        // 去除 Markdown 链接
        var text = Regex.Replace(notes, @"!?\[([^\]]*)\]\([^)]*\)", "$1");
        // 去除标题、引用和列表标记
        text = Regex.Replace(text, @"(?m)^\s{0,3}(#{1,6}\s*|>\s*|[-*+]\s+)", "");
        // 去除 HTML 标签与内联格式符
        text = Regex.Replace(text, @"<[^>]+>|[`*_~]", "").Trim();

        return text.Length <= 2000 ? text : text[..2000];
    }

    private static string MapErrorMessage(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } => "请求过于频繁，已被 GitHub 限流",
        HttpRequestException => "网络连接失败，请检查网络设置",
        InvalidDataException ivd => ivd.Message,
        UnauthorizedAccessException or IOException => "存储读写失败",
        _ => "获取更新信息失败"
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _timer?.Dispose();
        _lifetime.Cancel();
        _lifetime.Dispose();
        _gate.Dispose();

        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
    }
}
