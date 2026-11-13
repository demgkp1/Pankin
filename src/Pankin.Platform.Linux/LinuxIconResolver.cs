using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Pankin.Platform.Linux;

/// <summary>XDG icon lookup and Linux image decoding; no UI-framework dependency.</summary>
public sealed class LinuxIconResolver
{
    private readonly string[] _roots;
    private readonly string _theme;
    private readonly ConcurrentDictionary<string, Dictionary<string, Dictionary<string, string>>> _themes = new();
    private readonly ConcurrentDictionary<(string, int), string> _paths = new();

    public LinuxIconResolver(IEnumerable<string>? roots = null, string? theme = null)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME") ?? Path.Combine(home, ".local/share");
        var dataDirs = (Environment.GetEnvironmentVariable("XDG_DATA_DIRS") ?? "/usr/local/share:/usr/share")
            .Split(':', StringSplitOptions.RemoveEmptyEntries);
        _roots = roots?.ToArray() ?? new[] { Path.Combine(home, ".icons"), Path.Combine(dataHome, "icons") }
            .Concat(dataDirs.Select(p => Path.Combine(p, "icons")))
            .Concat(new[] { Path.Combine(dataHome, "flatpak/exports/share/icons"),
                "/var/lib/flatpak/exports/share/icons", "/var/lib/snapd/desktop/icons", "/usr/share/pixmaps" })
            .Distinct(StringComparer.Ordinal).ToArray();
        _theme = theme ?? ReadTheme();
    }

    public string? Resolve(string? name, int pixels = 96)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (Path.IsPathRooted(name)) return File.Exists(name) ? name : null;
        if (name.Contains('/') || name.Contains('\\')) return null;
        var path = _paths.GetOrAdd((name, pixels), key => Find(key.Item1, key.Item2) ?? string.Empty);
        return path.Length == 0 ? null : path;
    }

    private string? Find(string name, int pixels)
    {
        var names = new[] { ".png", ".svg", ".xpm" }.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase)
            ? new[] { name } : new[] { name + ".png", name + ".svg", name + ".xpm" };
        var visited = new HashSet<string>(StringComparer.Ordinal);
        string? InTheme(string theme)
        {
            if (!visited.Add(theme) || theme.Contains('/') || theme.Contains("..")) return null;
            var metadata = _themes.GetOrAdd(theme, ReadMetadata);
            metadata.TryGetValue("Icon Theme", out var header);
            var dirs = (header?.GetValueOrDefault("Directories", "") + "," + header?.GetValueOrDefault("ScaledDirectories", ""))
                .Split(',', StringSplitOptions.RemoveEmptyEntries).Distinct()
                .Where(d => !Path.IsPathRooted(d) && !d.Split('/').Contains(".."))
                .OrderBy(d => Distance(metadata.GetValueOrDefault(d), pixels));
            foreach (var dir in dirs)
                foreach (var root in _roots)
                    foreach (var file in names)
                    {
                        var path = Path.Combine(root, theme, dir, file);
                        if (File.Exists(path)) return path;
                    }
            foreach (var parent in (header?.GetValueOrDefault("Inherits", "") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var result = InTheme(parent.Trim());
                if (result != null) return result;
            }
            return null;
        }
        var themed = InTheme(_theme) ?? InTheme("hicolor");
        if (themed != null) return themed;
        // Exported apps sometimes omit index.theme. Limit the fallback to standard app sizes.
        foreach (var root in _roots)
            foreach (var dir in new[] { "", "hicolor/96x96/apps", "hicolor/128x128/apps", "hicolor/64x64/apps",
                         "hicolor/48x48/apps", "hicolor/256x256/apps", "hicolor/scalable/apps", "hicolor/32x32/apps", "hicolor/16x16/apps" })
                foreach (var file in names)
                {
                    var path = Path.Combine(root, dir, file);
                    if (File.Exists(path)) return path;
                }
        return null;
    }

    private Dictionary<string, Dictionary<string, string>> ReadMetadata(string theme)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var root in _roots)
        {
            try
            {
                var file = Path.Combine(root, theme, "index.theme");
                if (!File.Exists(file)) continue;
                Dictionary<string, string>? section = null;
                foreach (var line in File.ReadLines(file).Select(l => l.Trim()))
                {
                    if (line.StartsWith('[') && line.EndsWith(']'))
                        result[line[1..^1]] = section = new(StringComparer.Ordinal);
                    else if (section != null && !line.StartsWith('#') && line.IndexOf('=') is var eq && eq > 0)
                        section[line[..eq].Trim()] = line[(eq + 1)..].Trim();
                }
                return result; // The first index has priority; files may live in any root.
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return result;
    }

    private static int Distance(Dictionary<string, string>? section, int pixels)
    {
        int Read(string key, int fallback) => section != null && int.TryParse(section.GetValueOrDefault(key), out var value) ? value : fallback;
        int size = Read("Size", 48), scale = Read("Scale", 1);
        var type = section?.GetValueOrDefault("Type", "Threshold");
        int min = type == "Scalable" ? Read("MinSize", size) : type == "Fixed" ? size : size - Read("Threshold", 2);
        int max = type == "Scalable" ? Read("MaxSize", size) : type == "Fixed" ? size : size + Read("Threshold", 2);
        return Math.Max(min * scale - pixels, Math.Max(0, pixels - max * scale));
    }

    private static string ReadTheme()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("gsettings")
            {
                ArgumentList = { "get", "org.gnome.desktop.interface", "icon-theme" },
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
            });
            if (process != null)
            {
                if (!process.WaitForExit(500)) { process.Kill(); return "hicolor"; }
                var theme = process.StandardOutput.ReadToEnd().Trim().Trim('\'');
                if (process.ExitCode == 0 && theme.Length > 0) return theme;
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        return "hicolor";
    }

    /// <summary>GdkPixbuf uses the installed librsvg loader for SVG, and also supports PNG/XPM.
    /// Missing native loaders or malformed files return null rather than breaking the launcher.</summary>
    public byte[]? LoadPng(string name, int pixels = 96)
    {
        var path = Resolve(name, pixels);
        if (path == null) return null;
        IntPtr pixbuf = IntPtr.Zero, buffer = IntPtr.Zero, error = IntPtr.Zero;
        try
        {
            pixbuf = gdk_pixbuf_new_from_file_at_scale(path, Math.Clamp(pixels, 16, 256), Math.Clamp(pixels, 16, 256), true, out error);
            if (pixbuf == IntPtr.Zero) return null;
            if (!gdk_pixbuf_save_to_bufferv(pixbuf, out buffer, out var length, "png", IntPtr.Zero, IntPtr.Zero, out error)
                || length == 0 || length > 4 * 1024 * 1024) return null;
            var bytes = new byte[(int)length];
            Marshal.Copy(buffer, bytes, 0, bytes.Length);
            return bytes;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) { return null; }
        finally
        {
            if (buffer != IntPtr.Zero) g_free(buffer);
            if (error != IntPtr.Zero) g_error_free(error);
            if (pixbuf != IntPtr.Zero) g_object_unref(pixbuf);
        }
    }

    [DllImport("libgdk_pixbuf-2.0.so.0")] private static extern IntPtr gdk_pixbuf_new_from_file_at_scale(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string filename, int width, int height, [MarshalAs(UnmanagedType.Bool)] bool preserveAspectRatio, out IntPtr error);
    [DllImport("libgdk_pixbuf-2.0.so.0")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool gdk_pixbuf_save_to_bufferv(IntPtr pixbuf, out IntPtr buffer, out nuint size,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string type, IntPtr keys, IntPtr values, out IntPtr error);
    [DllImport("libgobject-2.0.so.0")] private static extern void g_object_unref(IntPtr obj);
    [DllImport("libglib-2.0.so.0")] private static extern void g_free(IntPtr memory);
    [DllImport("libglib-2.0.so.0")] private static extern void g_error_free(IntPtr error);
}
