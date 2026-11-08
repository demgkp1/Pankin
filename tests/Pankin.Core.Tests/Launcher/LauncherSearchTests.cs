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

    private sealed class FakeFileSearcher(IReadOnlyList<FileSearchResult>? results = null) : IFileSearcher
    {
        private readonly IReadOnlyList<FileSearchResult> _results = results ??
        [
            new("/home/user/Documents/report.pdf", "report.pdf", "/home/user/Documents")
        ];

        public async IAsyncEnumerable<FileSearchResult> SearchFilesAsync(string query, int limit = 50, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            foreach (var item in _results)
            {
                ct.ThrowIfCancellationRequested();
                yield return item;
            }
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

    private static LauncherSearch CreateLauncherSearch(
        string tempConfigPath,
        IReadOnlyList<AppEntry>? apps = null,
        IReadOnlyList<FileSearchResult>? files = null)
    {
        var sampleApps = apps ??
        [
            new("google-chrome", "Google Chrome", "谷歌浏览器", "google-chrome-stable", null, "/usr/share/applications/google-chrome.desktop"),
            new("org.gnome.Calculator", "Calculator", "计算器", "gnome-calculator", null, "/usr/share/applications/org.gnome.Calculator.desktop"),
            new("code", "Visual Studio Code", "代码编辑器", "code", null, "/usr/share/applications/code.desktop")
        ];

        var appScanner = new FakeAppScanner(sampleApps);
        var fileSearcher = new FakeFileSearcher(files);
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

    [Fact]
    public void GetRecentApps_ShouldPrioritizeRecentlyUsedApps()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "pankin-test-" + Guid.NewGuid(), "settings.json");
        try
        {
            using var search = CreateLauncherSearch(tempFile);
            bool eventFired = false;
            search.UsageChanged += (_, _) => eventFired = true;

            search.RecordUsage("code");

            Assert.True(eventFired);
            var recents = search.GetRecentApps(5);
            Assert.NotEmpty(recents);
            Assert.Equal("code", recents[0].Id);
        }
        finally
        {
            var dir = Path.GetDirectoryName(tempFile);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void RecordExternalAppActivation_ShouldMatchAndTriggerUsage()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "pankin-test-" + Guid.NewGuid(), "settings.json");
        try
        {
            using var search = CreateLauncherSearch(tempFile);
            int usageCount = 0;
            search.UsageChanged += (_, _) => usageCount++;

            // Pankin itself should be ignored
            search.RecordExternalAppActivation("Pankin");
            Assert.Equal(0, usageCount);

            // Empty or whitespace ignored
            search.RecordExternalAppActivation("   ");
            Assert.Equal(0, usageCount);

            // Valid app activation matching Exec / App Name
            search.RecordExternalAppActivation("gnome-calculator");
            Assert.Equal(1, usageCount);
            var recents = search.GetRecentApps(5);
            Assert.Equal("org.gnome.Calculator", recents[0].Id);

            // Repeated activation for the same app should not record again
            search.RecordExternalAppActivation("Calculator");
            Assert.Equal(1, usageCount);

            // Different app activation
            search.RecordExternalAppActivation("Google Chrome");
            Assert.Equal(2, usageCount);
            recents = search.GetRecentApps(5);
            Assert.Equal("google-chrome", recents[0].Id);
        }
        finally
        {
            var dir = Path.GetDirectoryName(tempFile);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Theory]
    [InlineData("", false, false)]
    [InlineData("   ", false, false)]
    [InlineData("a", false, false)]
    [InlineData("a", true, true)]
    [InlineData("f ", false, false)]
    [InlineData("f a", false, true)]
    [InlineData("ab", false, true)]
    [InlineData("表", false, true)]
    [InlineData("表", true, true)]
    public void IsValidFileQuery_ShouldValidateCorrectly(string query, bool isExplicit, bool expected)
    {
        var actual = LauncherSearch.IsValidFileQuery(query, isExplicit);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task SearchAsync_SingleChineseCharacter_ShouldTriggerFileSearch()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "pankin-test-" + Guid.NewGuid(), "settings.json");
        try
        {
            var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var file = new FileSearchResult(Path.Combine(homeDir, "Documents", "报表.xlsx"), "报表.xlsx", Path.Combine(homeDir, "Documents"));
            using var search = CreateLauncherSearch(tempFile, files: [file]);

            var result = await search.SearchAsync("表", CancellationToken.None);

            var fileItem = result.Items.FirstOrDefault(i => i.Kind == LauncherItemKind.File);
            Assert.NotNull(fileItem);
            Assert.Equal("报表.xlsx", fileItem.Title);
            Assert.True(fileItem.Score > 0);
        }
        finally
        {
            var dir = Path.GetDirectoryName(tempFile);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task SearchAsync_IsFileExplicit_ShouldTriggerSearchForSingleAsciiChar()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "pankin-test-" + Guid.NewGuid(), "settings.json");
        try
        {
            var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var file = new FileSearchResult(Path.Combine(homeDir, "Documents", "app.log"), "app.log", Path.Combine(homeDir, "Documents"));
            using var search = CreateLauncherSearch(tempFile, files: [file]);

            // When isFileExplicit is false, single character "a" should not trigger file search
            var implicitResult = await search.SearchAsync("a", isFileExplicit: false, CancellationToken.None);
            Assert.DoesNotContain(implicitResult.Items, i => i.Kind == LauncherItemKind.File);

            // When isFileExplicit is true, single character "a" should trigger file search
            var explicitResult = await search.SearchAsync("a", isFileExplicit: true, CancellationToken.None);
            var fileItem = explicitResult.Items.FirstOrDefault(i => i.Kind == LauncherItemKind.File);
            Assert.NotNull(fileItem);
            Assert.Equal("app.log", fileItem.Title);
        }
        finally
        {
            var dir = Path.GetDirectoryName(tempFile);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task SearchAsync_HomeDirPrioritizedOverUsrDir()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "pankin-test-" + Guid.NewGuid(), "settings.json");
        try
        {
            var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var userFile = new FileSearchResult(Path.Combine(homeDir, "Documents", "report.txt"), "report.txt", Path.Combine(homeDir, "Documents"));
            var usrFile = new FileSearchResult("/usr/share/doc/report.txt", "report.txt", "/usr/share/doc");

            // Intentionally provide usrFile before userFile
            using var search = CreateLauncherSearch(tempFile, files: [usrFile, userFile]);

            var result = await search.SearchAsync("report", CancellationToken.None);
            var fileItems = result.Items.Where(i => i.Kind == LauncherItemKind.File).ToList();

            Assert.True(fileItems.Count >= 2);
            // userFile has homeDir bonus (+100) vs usrFile (+0), both have exact name match (+80)
            Assert.Equal("file:" + userFile.FullPath, fileItems[0].Id);
            Assert.Equal("file:" + usrFile.FullPath, fileItems[1].Id);
            Assert.True(fileItems[0].Score > fileItems[1].Score);
        }
        finally
        {
            var dir = Path.GetDirectoryName(tempFile);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task SearchAsync_WorkspaceHiddenDirs_NotPenalizedAsNoise()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "pankin-test-" + Guid.NewGuid(), "settings.json");
        try
        {
            var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var workspaceFile = new FileSearchResult(
                Path.Combine(homeDir, ".gemini", "antigravity", "settings.json"),
                "settings.json",
                Path.Combine(homeDir, ".gemini", "antigravity"));
            var noiseFile = new FileSearchResult(
                Path.Combine(homeDir, ".cache", "thumbnails", "settings.json"),
                "settings.json",
                Path.Combine(homeDir, ".cache", "thumbnails"));

            // Intentionally provide noiseFile before workspaceFile
            using var search = CreateLauncherSearch(tempFile, files: [noiseFile, workspaceFile]);

            var result = await search.SearchAsync("settings.json", CancellationToken.None);
            var fileItems = result.Items.Where(i => i.Kind == LauncherItemKind.File).ToList();

            Assert.True(fileItems.Count >= 2);
            var workspaceItem = fileItems.FirstOrDefault(i => i.Id == "file:" + workspaceFile.FullPath);
            var noiseItem = fileItems.FirstOrDefault(i => i.Id == "file:" + noiseFile.FullPath);
            Assert.NotNull(workspaceItem);
            Assert.NotNull(noiseItem);

            // Workspace hidden dir gets 100 path bonus + 80 exact name match = 180
            // Noise path gets 20 path bonus + 80 exact name match = 100
            Assert.Equal(180, workspaceItem.Score);
            Assert.Equal(100, noiseItem.Score);
            Assert.Equal("file:" + workspaceFile.FullPath, fileItems[0].Id);
            Assert.True(workspaceItem.Score > noiseItem.Score);
        }
        finally
        {
            var dir = Path.GetDirectoryName(tempFile);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
