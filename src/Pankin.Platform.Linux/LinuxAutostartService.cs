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
    private readonly string _applicationDesktopFilePath;
    private readonly Func<IReadOnlyList<string>> _launchCommandProvider;

    public LinuxAutostartService(
        string? customAutostartDir = null,
        Func<IReadOnlyList<string>>? launchCommandProvider = null,
        string? customApplicationsDir = null)
    {
        _launchCommandProvider = launchCommandProvider ?? ResolveLaunchCommand;
        _autostartFilePath = ResolveXdgDesktopFilePath(customAutostartDir, "XDG_CONFIG_HOME", ".config", "autostart");
        _applicationDesktopFilePath = ResolveXdgDesktopFilePath(
            customApplicationsDir,
            "XDG_DATA_HOME",
            Path.Combine(".local", "share"),
            "applications");
    }

    private static string ResolveXdgDesktopFilePath(
        string? customDir,
        string xdgEnvVar,
        string defaultBaseRelative,
        string subDir)
    {
        string dir;
        if (!string.IsNullOrWhiteSpace(customDir))
        {
            dir = customDir;
        }
        else
        {
            var xdgBase = Environment.GetEnvironmentVariable(xdgEnvVar);
            if (string.IsNullOrWhiteSpace(xdgBase))
            {
                xdgBase = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), defaultBaseRelative);
            }
            dir = Path.Combine(xdgBase, subDir);
        }

        return Path.Combine(dir, DesktopFileName);
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

    private static string BuildExecValue(IReadOnlyList<string> command, string flag)
    {
        // GLib checks the first executable token before expanding %% field codes.
        // For a literal % in the filename, make env the executable and the image an argument.
        var arguments = command[0].Contains('%')
            ? new[] { "/usr/bin/env", "--" }.Concat(command)
            : command;
        return string.Join(" ", arguments.Select(QuoteExecArgument)) + " " + flag;
    }

    private static void WriteDesktopFileIfChanged(string path, string content)
    {
        var utf8NoBom = new UTF8Encoding(false);
        var desiredBytes = utf8NoBom.GetBytes(content);
        if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(desiredBytes))
        {
            return;
        }

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        // No UTF-8 BOM: the group header must be the first bytes in a desktop entry.
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, desiredBytes);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string ResolveApplicationIcon(string desktopFilePath, bool exists)
    {
        if (!exists) return "pankin";

        var iconLine = File.ReadLines(desktopFilePath)
            .FirstOrDefault(l => l.StartsWith("Icon=", StringComparison.Ordinal));
        if (iconLine is null) return "pankin";

        var existingIcon = iconLine["Icon=".Length..].Trim();
        if (string.IsNullOrWhiteSpace(existingIcon)) return "pankin";

        return !Path.IsPathRooted(existingIcon) || File.Exists(existingIcon)
            ? existingIcon
            : "pankin";
    }

    public void SetAutostart(bool enabled)
    {
        try
        {
            if (enabled)
            {
                var command = _launchCommandProvider();
                if (command.Count == 0) throw new InvalidOperationException("Empty autostart command");
                var exec = BuildExecValue(command, "--autostart");

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

                WriteDesktopFileIfChanged(_autostartFilePath, content);
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

    public void RefreshApplicationDesktopEntry()
    {
        try
        {
            var command = _launchCommandProvider();
            if (command.Count == 0 ||
                Path.GetFileNameWithoutExtension(command[0]).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            bool exists = File.Exists(_applicationDesktopFilePath);
            bool isAppImage = command[0].EndsWith(".AppImage", StringComparison.OrdinalIgnoreCase);
            if (!exists && !isAppImage)
            {
                return;
            }

            var icon = ResolveApplicationIcon(_applicationDesktopFilePath, exists);
            var exec = BuildExecValue(command, "--toggle");

            var content = new StringBuilder()
                .AppendLine("[Desktop Entry]")
                .AppendLine("Type=Application")
                .AppendLine("Name=Pankin")
                .AppendLine("Comment=Pankin Desktop Assistant")
                .AppendLine($"Exec={exec}")
                .AppendLine($"Icon={icon}")
                .AppendLine("Terminal=false")
                .AppendLine("StartupNotify=false")
                .AppendLine("Categories=Utility;")
                .AppendLine("StartupWMClass=pankin")
                .ToString();

            WriteDesktopFileIfChanged(_applicationDesktopFilePath, content);
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Unable to update Pankin application desktop entry: {ex.Message}");
        }
    }
}
