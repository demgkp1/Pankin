using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Pankin.Core.Abstractions;
using Pankin.Core.FolderJump;
using Pankin.Core.Launcher;
using Pankin.Core.Settings;
using Xunit;

namespace Pankin.Core.Tests.Launcher;

public class FolderJumpLauncherSourceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _storeFile;
    private readonly FolderJumpStore _store;

    private sealed class MockActionHost : ILauncherActionHost
    {
        public List<string> OpenedPaths { get; } = [];

        public Task<bool> OpenAsync(string pathOrUrl, bool elevated = false, CancellationToken ct = default)
        {
            OpenedPaths.Add(pathOrUrl);
            return Task.FromResult(true);
        }

        public Task<bool> RevealAsync(string path, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> RunInTerminalAsync(string command, bool elevated = false, string? workingDir = null, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> CopyTextAsync(string text, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> ExecuteSystemCommandAsync(string command, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> ShowPropertiesAsync(string path, CancellationToken ct = default) => Task.FromResult(true);
    }

    public FolderJumpLauncherSourceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "folderjump_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _storeFile = Path.Combine(_tempDir, "jump.json");
        _store = new FolderJumpStore(_storeFile);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch { }
    }

    private SourceContext CreateContext(string query, bool folderJumpEnabled = true)
    {
        var settings = new PankinSettings
        {
            Modules = new ModulesSettings { FolderJump = folderJumpEnabled }
        };

        return new SourceContext(
            query,
            [],
            settings.Launcher,
            new Dictionary<string, AppUsage>(),
            settings);
    }

    [Fact]
    public void Owns_ShouldReturnTrue_ForFolderJumpPrefix()
    {
        var host = new MockActionHost();
        var source = new FolderJumpLauncherSource(_store, host);

        var jumpItem = new LauncherItem("folder-jump:/home/user/code", LauncherItemKind.Folder, "folder-jump", "code", "/home/user/code", new LauncherIcon("folder"));
        var fileItem = new LauncherItem("file:/home/user/code", LauncherItemKind.Folder, "files", "code", "/home/user/code", new LauncherIcon("folder"));

        Assert.True(source.Owns(jumpItem));
        Assert.False(source.Owns(fileItem));
    }

    [Fact]
    public async Task SearchAsync_EmptyQuery_ShouldReturnEmpty()
    {
        var host = new MockActionHost();
        var source = new FolderJumpLauncherSource(_store, host);
        var context = CreateContext("");

        var results = await source.SearchAsync(context, CancellationToken.None);
        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchAsync_WhenModuleDisabled_ShouldReturnEmpty()
    {
        var host = new MockActionHost();
        var source = new FolderJumpLauncherSource(_store, host);
        await _store.RecordAccessAsync("/home/user/Projects");

        var context = CreateContext("Projects", folderJumpEnabled: false);
        var results = await source.SearchAsync(context, CancellationToken.None);
        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchAsync_WithQuery_ShouldMatchStoredFolders()
    {
        var host = new MockActionHost();
        var source = new FolderJumpLauncherSource(_store, host);
        await _store.RecordAccessAsync("/home/user/Projects");
        await _store.RecordAccessAsync("/home/user/Documents");

        var context = CreateContext("Proj");
        var results = await source.SearchAsync(context, CancellationToken.None);

        Assert.Single(results);
        Assert.Equal("Projects", results[0].Title);
        Assert.Equal("folder-jump:/home/user/Projects", results[0].Id);
        Assert.Equal(LauncherItemKind.Folder, results[0].Kind);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRecordAccessAndOpenPath()
    {
        var host = new MockActionHost();
        var source = new FolderJumpLauncherSource(_store, host);
        var item = new LauncherItem("folder-jump:/home/user/Downloads", LauncherItemKind.Folder, "folder-jump", "Downloads", "/home/user/Downloads", new LauncherIcon("folder"));
        var context = CreateContext("Downloads");

        await source.ExecuteAsync(item, context, CancellationToken.None);

        Assert.Contains("/home/user/Downloads", host.OpenedPaths);

        var entries = await _store.GetEntriesAsync();
        Assert.Contains(entries, e => e.Path == "/home/user/Downloads" && e.AccessCount >= 1);
    }
}
