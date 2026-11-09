using System.Text;
using Pankin.Core.Abstractions;

namespace Pankin.Platform.Linux;

/// <summary>
/// Linux 平台基于 XDG Desktop Autostart 标准 (~/.config/autostart/*.desktop) 的自启动服务实现
/// </summary>
public sealed class LinuxAutostartService : IAutostartService
{
    private const string DesktopFileName = "pankin.desktop";
    private readonly string _autostartFilePath;

    public LinuxAutostartService(string? customAutostartDir = null)
    {
        string autostartDir;
        if (!string.IsNullOrWhiteSpace(customAutostartDir))
        {
            autostartDir = customAutostartDir;
        }
        else
        {
            var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrWhiteSpace(configHome))
            {
                configHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            }
            autostartDir = Path.Combine(configHome, "autostart");
        }

        _autostartFilePath = Path.Combine(autostartDir, DesktopFileName);
    }

    public bool IsAutostartEnabled => File.Exists(_autostartFilePath);

    public void SetAutostart(bool enabled)
    {
        try
        {
            if (enabled)
            {
                var dir = Path.GetDirectoryName(_autostartFilePath);
                if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var appImagePath = Environment.GetEnvironmentVariable("APPIMAGE");
                var execPath = !string.IsNullOrWhiteSpace(appImagePath) && File.Exists(appImagePath)
                    ? appImagePath
                    : (Environment.ProcessPath ?? "pankin");

                var content = new StringBuilder()
                    .AppendLine("[Desktop Entry]")
                    .AppendLine("Type=Application")
                    .AppendLine("Name=Pankin")
                    .AppendLine("Comment=Pankin Desktop Assistant")
                    .AppendLine($"Exec=\"{execPath}\"")
                    .AppendLine("Terminal=false")
                    .AppendLine("StartupNotify=false")
                    .AppendLine("Categories=Utility;")
                    .AppendLine("X-GNOME-Autostart-enabled=true")
                    .ToString();

                File.WriteAllText(_autostartFilePath, content, Encoding.UTF8);
            }
            else
            {
                if (File.Exists(_autostartFilePath))
                {
                    File.Delete(_autostartFilePath);
                }
            }
        }
        catch
        {
            // 忽略非关键写入异常
        }
    }
}
