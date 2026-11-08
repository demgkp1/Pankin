using Pankin.Core.Modules.Hosting;
using Xunit;

namespace Pankin.Core.Tests.Modules;

public class HostingTests
{
    [Fact]
    public void LaunchArguments_Parse_ShouldIdentifyValidCommands()
    {
        Assert.Equal(LaunchCommand.Show, LaunchArguments.Parse(Array.Empty<string>()));
        Assert.Equal(LaunchCommand.Exit, LaunchArguments.Parse(new[] { "--exit" }));
        Assert.Equal(LaunchCommand.Autostart, LaunchArguments.Parse(new[] { "--autostart" }));
        Assert.Equal(LaunchCommand.Invalid, LaunchArguments.Parse(new[] { "--unknown" }));
        Assert.Equal(LaunchCommand.Invalid, LaunchArguments.Parse(new[] { "--exit", "--extra" }));
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
}
