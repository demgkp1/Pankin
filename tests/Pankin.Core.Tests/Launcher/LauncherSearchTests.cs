using Pankin.Core.Abstractions;
using Pankin.Core.Launcher;
using Pankin.Core.Settings;
using Xunit;

namespace Pankin.Core.Tests.Launcher;

public class LauncherSearchTests
{
    private sealed class FakeAppScanner(IReadOnlyList<AppEntry> apps) : IAppScanner
    {
        public Task<IReadOnlyList<AppEntry>> ScanAppsAsync(CancellationToken ct = default) =>
            Task.FromResult(apps);

        public IReadOnlyList<AppEntry> SearchApps(string query, int maxResults = 20) =>
            apps.Take(maxResults).ToList();

        public Task<bool> LaunchAppAsync(AppEntry app, CancellationToken ct = default) =>
            Task.FromResult(true);
    }

    private sealed class FakeFileSearcher : IFileSearcher
    {
        public async IAsyncEnumerable<FileSearchResult> SearchFilesAsync(string query, int limit = 50, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            yield return new FileSearchResult("/home/user/Documents/report.pdf", "report.pdf", "/home/user/Documents");
        }
    }

    private sealed class FakeLauncherActionHost : ILauncherActionHost
    {
        public Task<bool> OpenAsync(string pathOrUrl, bool elevated = false, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> RevealAsync(string path, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> RunInTerminalAsync(string command, bool elevated = false, string? workingDir = null, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> CopyTextAsync(string text, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> ExecuteSystemCommandAsync(string command, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> ShowPropertiesAsync(string path, CancellationToken ct = default) => Task.FromResult(true);
    }

    private static LauncherSearch CreateLauncherSearch(string tempConfigPath, IReadOnlyList<AppEntry>? apps = null)
    {
        var sampleApps = apps ??
        [
            new("google-chrome", "Google Chrome", "谷歌浏览器", "google-chrome-stable", null, "/usr/share/applications/google-chrome.desktop"),
            new("org.gnome.Calculator", "Calculator", "计算器", "gnome-calculator", null, "/usr/share/applications/org.gnome.Calculator.desktop"),
            new("code", "Visual Studio Code", "代码编辑器", "code", null, "/usr/share/applications/code.desktop")
        ];

        var appScanner = new FakeAppScanner(sampleApps);
        var fileSearcher = new FakeFileSearcher();
        var actionHost = new FakeLauncherActionHost();
        var settingsStore = new SettingsStore(tempConfigPath);

        var search = new LauncherSearch(
            appScanner,
            fileSearcher,
            actionHost,
            settingsStore);

        // 等待初始扫描加载完成
        search.RefreshAppsAsync().GetAwaiter().GetResult();
        return search;
    }

    [Fact]
    public async Task SearchAsync_EmptyQuery_ShouldReturnHomeItems()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "pankin-test-" + Guid.NewGuid(), "settings.json");
        try
        {
            using var search = CreateLauncherSearch(tempFile);
            var result = await search.SearchAsync("", CancellationToken.None);

            Assert.True(result.IsHome);
            Assert.NotNull(result.Items);
        }
        finally
        {
            var dir = Path.GetDirectoryName(tempFile);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task SearchAsync_MathExpression_ShouldReturnCalculationCard()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "pankin-test-" + Guid.NewGuid(), "settings.json");
        try
        {
            using var search = CreateLauncherSearch(tempFile);
            var result = await search.SearchAsync("1024 * 2 + 512", CancellationToken.None);

            Assert.False(result.IsHome);
            var calcItem = result.Items.FirstOrDefault(i => i.Kind == LauncherItemKind.Calculation);
            Assert.NotNull(calcItem);
            Assert.Equal("2560", calcItem.Title);
            Assert.Equal(LauncherGroups.Calculation, calcItem.Group);
        }
        finally
        {
            var dir = Path.GetDirectoryName(tempFile);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task SearchAsync_Pinyin_ShouldMatchApp()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "pankin-test-" + Guid.NewGuid(), "settings.json");
        try
        {
            using var search = CreateLauncherSearch(tempFile);
            // "jsq" 对应 "计算器"
            var result = await search.SearchAsync("jsq", CancellationToken.None);

            var appItem = result.Items.FirstOrDefault(i => i.Kind == LauncherItemKind.App);
            Assert.NotNull(appItem);
            Assert.Equal("计算器", appItem.Title);
            Assert.Equal(LauncherGroups.Applications, appItem.Group);
        }
        finally
        {
            var dir = Path.GetDirectoryName(tempFile);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task SearchAsync_SystemCommand_ShouldMatchExtraCommand()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "pankin-test-" + Guid.NewGuid(), "settings.json");
        try
        {
            using var search = CreateLauncherSearch(tempFile);
            var result = await search.SearchAsync("锁屏", CancellationToken.None);

            var cmdItem = result.Items.FirstOrDefault(i => i.Kind == LauncherItemKind.Command);
            Assert.NotNull(cmdItem);
            Assert.Equal("command:lock", cmdItem.Id);
            Assert.Equal("锁定屏幕", cmdItem.Title);
        }
        finally
        {
            var dir = Path.GetDirectoryName(tempFile);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task SearchAsync_PartialStreaming_ShouldInvokeCallbackWithFastResults()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "pankin-test-" + Guid.NewGuid(), "settings.json");
        try
        {
            using var search = CreateLauncherSearch(tempFile);
            LauncherResult? partialResult = null;

            var finalResult = await search.SearchAsync("10 + 20", r => partialResult = r, CancellationToken.None);

            Assert.NotNull(partialResult);
            Assert.NotEmpty(partialResult.Items);
            Assert.NotNull(finalResult);
        }
        finally
        {
            var dir = Path.GetDirectoryName(tempFile);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task GetActionsAsync_ForApp_ShouldReturnSupportedActions()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "pankin-test-" + Guid.NewGuid(), "settings.json");
        try
        {
            using var search = CreateLauncherSearch(tempFile);
            var item = new LauncherItem("google-chrome", LauncherItemKind.App, LauncherGroups.Applications, "Chrome", "/usr/bin/google-chrome", new LauncherIcon("app"));

            var actions = await search.GetActionsAsync(item, CancellationToken.None);

            Assert.Contains(LauncherItemAction.RunAsAdmin, actions);
            Assert.Contains(LauncherItemAction.OpenLocation, actions);
            Assert.Contains(LauncherItemAction.CopyPath, actions);
            Assert.Contains(LauncherItemAction.Properties, actions);
        }
        finally
        {
            var dir = Path.GetDirectoryName(tempFile);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
