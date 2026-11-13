using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Pankin.Core.Clipboard;
using Pankin.Platform.Linux;
using Xunit;

namespace Pankin.Linux.Tests;

public sealed class LinuxRegressionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pankin-linux-tests-" + Guid.NewGuid().ToString("N"));

    public LinuxRegressionTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Executable(string relativePath, string script = "#!/bin/sh\nexit 0\n")
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, script);
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private LinuxAutostartService Autostart(params string[] command) => new(Path.Combine(_root, "autostart"), () => command);
    private string DesktopPath => Path.Combine(_root, "autostart", "pankin.desktop");

    [Fact]
    public void AutostartIsBomlessAndRunsPersistentAppImageInBackground()
    {
        var image = Executable("My Apps/Pankin.AppImage");
        var service = Autostart(image);
        service.SetAutostart(true);
        var bytes = File.ReadAllBytes(DesktopPath);
        Assert.Equal((byte)'[', bytes[0]);
        Assert.Contains($"Exec=\"{image}\" --autostart", Encoding.UTF8.GetString(bytes));
        Assert.DoesNotContain(".mount_", File.ReadAllText(DesktopPath));
        Assert.True(service.IsAutostartEnabled);
        service.SetAutostart(false);
        Assert.False(service.IsAutostartEnabled);
        Assert.False(File.Exists(DesktopPath));
    }

    [Fact]
    public void AutostartUpdatesMovedImageAndSupportsFrameworkHostedLaunch()
    {
        var image = Executable("Pankin.AppImage");
        Autostart(image).SetAutostart(true);
        var newer = Executable("New Pankin.AppImage");
        Autostart(newer).SetAutostart(true);
        Assert.Contains(newer, File.ReadAllText(DesktopPath));
        Autostart("/usr/bin/dotnet", "/opt/My Apps/Pankin.Desktop.dll").SetAutostart(true);
        Assert.Contains("Exec=\"/usr/bin/dotnet\" \"/opt/My Apps/Pankin.Desktop.dll\" --autostart", File.ReadAllText(DesktopPath));
    }

    [Fact]
    public void AutostartDoesNotPretendFailedRegistrationSucceeded()
    {
        var service = new LinuxAutostartService(Path.Combine(_root, "autostart"), () => throw new IOException("unavailable executable"));
        service.SetAutostart(true);
        Assert.False(service.IsAutostartEnabled);
    }

    [Theory]
    [InlineData("Hidden=true")]
    [InlineData("X-GNOME-Autostart-enabled=false")]
    public void DisabledDesktopEntryIsNotEnabled(string flag)
    {
        Autostart("/opt/pankin").SetAutostart(true);
        File.AppendAllText(DesktopPath, flag + "\n");
        Assert.False(Autostart("/opt/pankin").IsAutostartEnabled);
    }

    [Fact]
    public async Task AutostartExecSurvivesDesktopQuotingForSpacesQuotesDollarAndPercent()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/gio")) return;
        var image = Executable("My Apps/Pankin \"$cash%\".AppImage", "#!/bin/sh\nprintf '%s' \"$1\" > \"${0}.args\"\n");
        Autostart(image).SetAutostart(true);
        using var process = Process.Start(new ProcessStartInfo("/usr/bin/gio")
        {
            ArgumentList = { "launch", DesktopPath }, RedirectStandardError = true, UseShellExecute = false
        })!;
        var errors = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, errors);
        await WaitForAsync(() => File.Exists(image + ".args"));
        Assert.Equal("--autostart", File.ReadAllText(image + ".args"));
    }

    [Fact]
    public async Task ScannerFindsPortableImagesWithoutDesktopEntriesAndSearchesNames()
    {
        Executable("app/CC-Switch-v4.0.31-Linux-x86_64.AppImage");
        Executable("app/sub/Obsidian-1.13.7.APPIMAGE");
        var nonExecutable = Path.Combine(_root, "app", "NotExecutable.AppImage");
        File.WriteAllText(nonExecutable, "not executable");
        Executable("app/NotAnImage.txt");
        var scanner = new LinuxAppScanner([], [Path.Combine(_root, "app"), Path.Combine(_root, "app")]);
        var apps = await scanner.ScanAppsAsync();
        Assert.Equal(2, apps.Count);
        Assert.Equal("CC-Switch", Assert.Single(scanner.SearchApps("CC-Switch")).DisplayName);
        Assert.Equal("Obsidian", Assert.Single(scanner.SearchApps("Obsidian")).DisplayName);
        Assert.All(apps, app => Assert.StartsWith("appimage:", app.Id));
        var ids = apps.Select(app => app.Id).ToArray();
        Assert.Equal(ids, (await scanner.ScanAppsAsync()).Select(app => app.Id).ToArray());
    }

    [Fact]
    public async Task DesktopEntryTakesPrecedenceOverDiscoveredImage()
    {
        var image = Executable("app/My Tool.AppImage");
        var desktopDirectory = Path.Combine(_root, "desktops");
        Directory.CreateDirectory(desktopDirectory);
        File.WriteAllText(Path.Combine(desktopDirectory, "my-tool.desktop"),
            $"[Desktop Entry]\nType=Application\nName=My Tool\nName[zh_CN]=我的工具\nExec=\"{image}\" %U\nIcon=my-tool\n");
        var scanner = new LinuxAppScanner([desktopDirectory], [Path.Combine(_root, "app")]);
        var app = Assert.Single(await scanner.ScanAppsAsync());
        Assert.Equal("my-tool", app.Id);
        Assert.Equal("我的工具", app.DisplayName);
    }

    [Fact]
    public async Task StaleDesktopEntryDoesNotAbortAppImageDiscovery()
    {
        Executable("app/Obsidian.AppImage");
        var desktopDirectory = Path.Combine(_root, "desktops");
        Directory.CreateDirectory(desktopDirectory);
        File.WriteAllText(Path.Combine(desktopDirectory, "missing.desktop"),
            "[Desktop Entry]\nType=Application\nName=Missing\nExec=/does/not/exist.AppImage\n");
        var scanner = new LinuxAppScanner([desktopDirectory], [Path.Combine(_root, "app")]);
        await scanner.ScanAppsAsync();
        Assert.Single(scanner.SearchApps("Obsidian"));
    }

    [Fact]
    public async Task AppImageLaunchDoesNotInterpretFilenameAsShellCode()
    {
        if (!OperatingSystem.IsLinux()) return;
        Executable("app/My 'Tool' $(touch SHOULD_NOT_EXIST).AppImage", "#!/bin/sh\nprintf ok > launched\n");
        var scanner = new LinuxAppScanner([], [Path.Combine(_root, "app")]);
        var app = Assert.Single(await scanner.ScanAppsAsync());
        Assert.True(await scanner.LaunchAppAsync(app));
        await WaitForAsync(() => File.Exists(Path.Combine(_root, "app", "launched")));
        Assert.False(File.Exists(Path.Combine(_root, "app", "SHOULD_NOT_EXIST")));
    }

    [Fact]
    public async Task InitialNativeClipboardIsPersistedWithoutCommandDependencies()
    {
        using var clipboard = new LinuxClipboardService();
        var reads = 0;
        clipboard.ConfigureDesktopClipboard(_ => { Interlocked.Increment(ref reads); return Task.FromResult<string?>("already copied before startup"); }, (_, _) => Task.CompletedTask);
        using var history = new ClipboardHistoryManager(clipboard, new ClipboardStorage(Path.Combine(_root, "clipboard.db")));
        clipboard.StartMonitoring();
        await WaitForAsync(() => history.Storage.QueryEntries().Count == 1);
        await WaitForAsync(() => Volatile.Read(ref reads) >= 3);
        Assert.Equal("already copied before startup", Assert.Single(history.Storage.QueryEntries()).Text);
        clipboard.StopMonitoring();
    }

    [Fact]
    public async Task MonitorCapturesChangesAndRecopyAfterEmptyButSuppressesOwnWrites()
    {
        using var clipboard = new LinuxClipboardService();
        string? current = "first";
        var reads = 0;
        var captured = new ConcurrentQueue<string>();
        clipboard.ConfigureDesktopClipboard(_ => { Interlocked.Increment(ref reads); return Task.FromResult<string?>(Volatile.Read(ref current)); },
            (text, _) => { Volatile.Write(ref current, text); return Task.CompletedTask; });
        clipboard.ClipboardTextChanged += (_, text) => captured.Enqueue(text);
        clipboard.StartMonitoring();
        await WaitForAsync(() => captured.Count == 1);
        Volatile.Write(ref current, "");
        var before = Volatile.Read(ref reads);
        await WaitForAsync(() => Volatile.Read(ref reads) >= before + 2);
        Volatile.Write(ref current, "first");
        await WaitForAsync(() => captured.Count == 2);
        await clipboard.SetTextAsync("own paste");
        before = Volatile.Read(ref reads);
        await WaitForAsync(() => Volatile.Read(ref reads) >= before + 2);
        Assert.Equal(new[] { "first", "first" }, captured.ToArray());
        Volatile.Write(ref current, "another application copied");
        await WaitForAsync(() => captured.Count == 3);
        clipboard.StopMonitoring();
        Volatile.Write(ref current, "after stop");
        await Task.Delay(650);
        Assert.Equal(3, captured.Count);
        clipboard.StartMonitoring();
        await WaitForAsync(() => captured.Count == 4);
        Assert.Equal("after stop", captured.Last());
        clipboard.StopMonitoring();
    }

    [Fact]
    public async Task MonitorRetriesTransientNativeFailures()
    {
        using var clipboard = new LinuxClipboardService();
        var reads = 0;
        clipboard.ConfigureDesktopClipboard(_ => Interlocked.Increment(ref reads) == 1
            ? Task.FromException<string?>(new IOException("selection owner changed")) : Task.FromResult<string?>("recovered"), (_, _) => Task.CompletedTask);
        var captured = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        clipboard.ClipboardTextChanged += (_, text) => captured.TrySetResult(text);
        clipboard.StartMonitoring();
        Assert.Equal("recovered", await captured.Task.WaitAsync(TimeSpan.FromSeconds(6)));
        clipboard.StopMonitoring();
    }

    [Fact]
    public async Task NativeClipboardPollingAndExplicitOperationsAreSerialized()
    {
        using var clipboard = new LinuxClipboardService();
        var active = 0;
        var overlap = 0;
        async Task Touch(CancellationToken ct)
        {
            if (Interlocked.Increment(ref active) != 1) Interlocked.Increment(ref overlap);
            try { await Task.Delay(60, ct); }
            finally { Interlocked.Decrement(ref active); }
        }
        clipboard.ConfigureDesktopClipboard(async ct => { await Touch(ct); return "native text"; },
            async (_, ct) => await Touch(ct));
        clipboard.StartMonitoring();
        var reads = Enumerable.Range(0, 6).Select(_ => (Task)clipboard.GetTextAsync());
        await Task.WhenAll(reads.Append(clipboard.SetTextAsync("write")));
        clipboard.StopMonitoring();
        Assert.Equal(0, Volatile.Read(ref overlap));
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(6)) await Task.Delay(40);
        Assert.True(condition(), "Timed out waiting for asynchronous Linux service");
    }
}
