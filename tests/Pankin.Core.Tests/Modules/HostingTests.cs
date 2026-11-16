using System.Net.Sockets;
using System.Text;
using Pankin.Core.Modules.Hosting;
using Xunit;

namespace Pankin.Core.Tests.Modules;

public class HostingTests
{
    [Fact]
    public void LaunchArguments_Parse_ShouldIdentifyValidCommands()
    {
        Assert.Equal(LaunchCommand.Show, LaunchArguments.Parse(null));
        Assert.Equal(LaunchCommand.Show, LaunchArguments.Parse(Array.Empty<string>()));
        Assert.Equal(LaunchCommand.Show, LaunchArguments.Parse(new[] { "--show" }));
        Assert.Equal(LaunchCommand.Toggle, LaunchArguments.Parse(new[] { "--toggle" }));
        Assert.Equal(LaunchCommand.Clipboard, LaunchArguments.Parse(new[] { "--clipboard" }));
        Assert.Equal(LaunchCommand.SuperPanel, LaunchArguments.Parse(new[] { "--superpanel" }));
        Assert.Equal(LaunchCommand.Settings, LaunchArguments.Parse(new[] { "--settings" }));
        Assert.Equal(LaunchCommand.Exit, LaunchArguments.Parse(new[] { "--exit" }));
        Assert.Equal(LaunchCommand.Autostart, LaunchArguments.Parse(new[] { "--autostart" }));
        Assert.Equal(LaunchCommand.Invalid, LaunchArguments.Parse(new[] { "--unknown" }));
        Assert.Equal(LaunchCommand.Invalid, LaunchArguments.Parse(new[] { "--exit", "--extra" }));
    }

    [Fact]
    public void SingleInstanceCoordinator_ToWireCommand_ShouldMapCommandsCorrectly()
    {
        Assert.Equal("exit", SingleInstanceCoordinator.ToWireCommand(LaunchCommand.Exit));
        Assert.Equal("toggle-launcher", SingleInstanceCoordinator.ToWireCommand(LaunchCommand.Toggle));
        Assert.Equal("toggle-clipboard", SingleInstanceCoordinator.ToWireCommand(LaunchCommand.Clipboard));
        Assert.Equal("toggle-superpanel", SingleInstanceCoordinator.ToWireCommand(LaunchCommand.SuperPanel));
        Assert.Equal("open-settings", SingleInstanceCoordinator.ToWireCommand(LaunchCommand.Settings));
        Assert.Equal("autostart", SingleInstanceCoordinator.ToWireCommand(LaunchCommand.Autostart));
        Assert.Equal("show-launcher", SingleInstanceCoordinator.ToWireCommand(LaunchCommand.Show));
        Assert.Equal("show-launcher", SingleInstanceCoordinator.ToWireCommand(LaunchCommand.Invalid));
    }

    [Fact]
    public void ShellHotkeyBindings_DoubleTapKeys_ShouldArbitrateCorrectly()
    {
        // 两个模块都配置了双击 Ctrl (VirtualKey = 162)
        var (launcherKey, clipboardKey) = ShellHotkeyBindings.DoubleTapKeys(
            launcherHasDoubleTap: true,
            launcherKey: 162u,
            clipboardHasDoubleTap: true,
            clipboardKey: 162u,
            clipboardEnabled: true,
            suspended: false,
            launcherEnabled: true);

        // 启动器优先，剪贴板让路
        Assert.Equal(162u, launcherKey);
        Assert.Equal(0u, clipboardKey);

        // 当启动器停用时，剪贴板接管该键位
        var (lDisabledKey, cTakenKey) = ShellHotkeyBindings.DoubleTapKeys(
            launcherHasDoubleTap: true,
            launcherKey: 162u,
            clipboardHasDoubleTap: true,
            clipboardKey: 162u,
            clipboardEnabled: true,
            suspended: false,
            launcherEnabled: false);

        Assert.Equal(0u, lDisabledKey);
        Assert.Equal(162u, cTakenKey);
    }

    [Fact]
    public void ShellHotkeyBindings_Rebind_ShouldUpdateRegisteredList()
    {
        var registered = new List<int> { 1, 2 };
        var removed = new List<int>();
        var added = new List<(int, string)>();

        ShellHotkeyBindings.Rebind(
            registered,
            id => removed.Add(id),
            (id, name) => added.Add((id, name)),
            launcherEnabled: true,
            clipboardEnabled: false,
            suspended: false);

        Assert.Equal(new[] { 1, 2 }, removed);
        Assert.Single(added);
        Assert.Equal(1, added[0].Item1);
        Assert.Equal("launcher", added[0].Item2);
        Assert.Equal(new[] { 1 }, registered);
    }

    [Fact]
    public async Task SingleInstanceCoordinator_EnterListenAndRelease_ShouldHandleFileLockAndUds()
    {
        var previousXdg = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        var tempRuntimeDir = Path.Combine(Path.GetTempPath(), $"pankin-sic-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRuntimeDir);
        Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", tempRuntimeDir);

        try
        {
            Assert.True(SingleInstanceCoordinator.Enter(LaunchCommand.Show));
            Assert.False(SingleInstanceCoordinator.Enter(LaunchCommand.Invalid));

            var lockFiles = Directory.GetFiles(tempRuntimeDir, "*.lock");
            var lockPath = Assert.Single(lockFiles);
            Assert.Throws<IOException>(() =>
                new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None));

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var listenTask = SingleInstanceCoordinator.ListenAsync(cmd => received.TrySetResult(cmd), cts.Token);

            string? sockPath = null;
            for (int i = 0; i < 100; i++)
            {
                var sockFiles = Directory.GetFiles(tempRuntimeDir, "*.sock");
                if (sockFiles.Length == 1)
                {
                    sockPath = sockFiles[0];
                    break;
                }
                await Task.Delay(20, cts.Token);
            }

            Assert.NotNull(sockPath);

            using (var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
            {
                await client.ConnectAsync(new UnixDomainSocketEndPoint(sockPath!), cts.Token);
                await using var stream = new NetworkStream(client, ownsSocket: false);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);

                var wireCommand = SingleInstanceCoordinator.ToWireCommand(LaunchCommand.Settings);
                await writer.WriteLineAsync(wireCommand);
                var reply = await reader.ReadLineAsync(cts.Token);
                Assert.Equal("ok", reply);
            }

            var receivedCommand = await received.Task.WaitAsync(cts.Token);
            Assert.Equal("open-settings", receivedCommand);

            cts.Cancel();
            await listenTask;

            SingleInstanceCoordinator.Release();
            Assert.Empty(Directory.GetFiles(tempRuntimeDir, "*.sock"));

            using var reopened = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.True(reopened.CanWrite);
        }
        finally
        {
            SingleInstanceCoordinator.Release();
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", previousXdg);
            try
            {
                if (Directory.Exists(tempRuntimeDir))
                {
                    Directory.Delete(tempRuntimeDir, recursive: true);
                }
            }
            catch
            {
                // 忽略临时目录清理异常
            }
        }
    }
}
