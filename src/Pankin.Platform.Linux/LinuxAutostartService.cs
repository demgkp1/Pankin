using System.Text;
using System.Diagnostics;
using System.Reflection;
using Pankin.Core.Abstractions;

namespace Pankin.Platform.Linux;

/// <summary>
/// Linux 平台基于 XDG Desktop Autostart 标准 (~/.config/autostart/*.desktop) 的自启动服务实现
/// </summary>
public sealed class LinuxAutostartService : IAutostartService
{
    private const string DesktopFileName = "pankin.desktop";
    private readonly string _autostartFilePath;
    private readonly Func<IReadOnlyList<string>> _launchCommandProvider;

    public LinuxAutostartService(string? customAutostartDir = null, Func<IReadOnlyList<string>>? launchCommandProvider = null)
    {
        _launchCommandProvider = launchCommandProvider ?? ResolveLaunchCommand;
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

    public bool IsAutostartEnabled
    {
        get
        {
            try
            {
                var lines = File.ReadAllLines(_autostartFilePath);
                return lines.Contains("Type=Application") &&
                    lines.Any(l => l.StartsWith("Exec=", StringComparison.Ordinal) && l.Length > 5) &&
                    !lines.Any(l => l.Equals("Hidden=true", StringComparison.OrdinalIgnoreCase) ||
                        l.Equals("X-GNOME-Autostart-enabled=false", StringComparison.OrdinalIgnoreCase));
            }
            catch { return false; }
        }
    }

    private static IReadOnlyList<string> ResolveLaunchCommand()
    {
        // APPIMAGE is the persistent downloaded file; ProcessPath points into a temporary mount.
        var appImage = Environment.GetEnvironmentVariable("APPIMAGE");
        if (!string.IsNullOrWhiteSpace(appImage))
        {
            if (!File.Exists(appImage)) throw new FileNotFoundException("AppImage has moved or been deleted", appImage);
            return [Path.GetFullPath(appImage)];
        }

        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot resolve executable");
        if (executable.StartsWith("/tmp/.mount_", StringComparison.Ordinal))
            throw new InvalidOperationException("Cannot register a temporary AppImage mount for autostart");
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            return [executable, Assembly.GetEntryAssembly()?.Location ?? throw new InvalidOperationException("Cannot resolve entry assembly")];
        return [executable];
    }

    private static string QuoteExecArgument(string argument)
    {
        if (argument.IndexOfAny(['\r', '\n', '\0']) >= 0)
            throw new ArgumentException("Invalid desktop entry argument", nameof(argument));
        // Desktop Entry string escaping is applied before Exec quoting is parsed.
        var quoted = "\"" + argument.Replace("%", "%%").Replace("\\", "\\\\")
            .Replace("\"", "\\\"").Replace("`", "\\`").Replace("$", "\\$") + "\"";
        return quoted.Replace("\\", "\\\\");
    }

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

                var command = _launchCommandProvider();
                if (command.Count == 0) throw new InvalidOperationException("Empty autostart command");
                // GLib checks the first executable token before expanding %% field codes.
                // For a literal % in the filename, make env the executable and the image an argument.
                var arguments = command[0].Contains('%')
                    ? new[] { "/usr/bin/env", "--" }.Concat(command)
                    : command;
                var exec = string.Join(" ", arguments.Select(QuoteExecArgument)) + " --autostart";

                var content = new StringBuilder()
                    .AppendLine("[Desktop Entry]")
                    .AppendLine("Type=Application")
                    .AppendLine("Name=Pankin")
                    .AppendLine("Comment=Pankin Desktop Assistant")
                    .AppendLine($"Exec={exec}")
                    .AppendLine("Terminal=false")
                    .AppendLine("StartupNotify=false")
                    .AppendLine("Categories=Utility;")
                    .AppendLine("X-GNOME-Autostart-enabled=true")
                    .ToString();

                // No UTF-8 BOM: the group header must be the first bytes in a desktop entry.
                var temporary = _autostartFilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllText(temporary, content, new UTF8Encoding(false));
                    File.Move(temporary, _autostartFilePath, overwrite: true);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            else
            {
                if (File.Exists(_autostartFilePath))
                {
                    File.Delete(_autostartFilePath);
                }
            }
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Unable to update Pankin autostart: {ex.Message}");
        }
    }
}
