namespace Pankin.Core.Modules.Hosting;

/// <summary>
/// 快捷键与双击修饰键冲突仲裁器：在多模块（如启动器与剪贴板）同时启用或部分停用时协调按键监听分配
/// </summary>
public static class ShellHotkeyBindings
{
    public static string? Command(nuint id) => id switch
    {
        1 => "hotkey-launcher",
        2 or 3 => "hotkey-clipboard",
        _ => null
    };

    public static bool ShouldHide(bool isShown, bool isForeground) => isShown && isForeground;

    /// <summary>
    /// 双击修饰键（如双击 Ctrl）仲裁：当两者配置相同键位时，若启动器启用则启动器优先，否则交由剪贴板接管
    /// </summary>
    public static (uint LauncherKey, uint ClipboardKey) DoubleTapKeys(
        bool launcherHasDoubleTap,
        uint launcherKey,
        bool clipboardHasDoubleTap,
        uint clipboardKey,
        bool clipboardEnabled,
        bool suspended,
        bool launcherEnabled = true)
    {
        if (suspended) return (0, 0);

        uint a = launcherEnabled && launcherHasDoubleTap ? launcherKey : 0u;
        uint b = clipboardEnabled && clipboardHasDoubleTap ? clipboardKey : 0u;

        // 若双击键位完全相同，优先供给启动器；剪贴板让路
        return (a, b == a ? 0 : b);
    }

    /// <summary>
    /// 根据各模块启用状态重新计算需注册与注销的热键 ID
    /// </summary>
    public static void Rebind(
        IList<int> currentRegistered,
        Action<int> unregister,
        Action<int, string> register,
        bool launcherEnabled,
        bool clipboardEnabled,
        bool suspended = false)
    {
        foreach (var id in currentRegistered)
        {
            unregister(id);
        }
        currentRegistered.Clear();

        if (suspended) return;

        if (launcherEnabled)
        {
            register(1, "launcher");
            currentRegistered.Add(1);
        }

        if (clipboardEnabled)
        {
            register(2, "clipboard");
            currentRegistered.Add(2);
        }
    }
}
