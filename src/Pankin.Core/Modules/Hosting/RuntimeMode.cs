using Pankin.Core.Settings;

namespace Pankin.Core.Modules.Hosting;

/// <summary>
/// 运行时模式与数据存储目录判定（开发测试区 vs 生产环境）
/// </summary>
public sealed record RuntimeMode(bool IsProduction, string DataDirectory)
{
    public static RuntimeMode Current { get; } = Resolve(
        AppContext.BaseDirectory,
        Environment.GetEnvironmentVariable("PANKIN_DATA_DIR") ?? Environment.GetEnvironmentVariable("PANE_DATA_DIR"),
        SettingsStore.GetDefaultConfigPath());

    public static RuntimeMode Resolve(string executableDirectory, string? configured, string productionConfigPath)
    {
        DirectoryInfo? project = null;
        for (var dir = new DirectoryInfo(executableDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Pankin.sln")) ||
                File.Exists(Path.Combine(dir.FullName, "Pankin.Desktop.csproj")))
            {
                project = dir;
                break;
            }
        }

        var isProduction = project == null;
        var dataDir = !string.IsNullOrWhiteSpace(configured)
            ? Path.GetFullPath(configured)
            : isProduction
                ? Path.GetDirectoryName(productionConfigPath) ?? productionConfigPath
                : Path.Combine(project!.FullName, "bin", "data");

        return new RuntimeMode(isProduction, dataDir);
    }
}

public enum LaunchCommand
{
    Show,
    Autostart,
    Exit,
    Invalid
}

public static class LaunchArguments
{
    public static LaunchCommand Parse(string[] args)
    {
        if (args.Length == 0) return LaunchCommand.Show;
        if (args.Length != 1) return LaunchCommand.Invalid;

        return args[0] switch
        {
            "--exit" => LaunchCommand.Exit,
            "--autostart" => LaunchCommand.Autostart,
            _ => LaunchCommand.Invalid
        };
    }
}
