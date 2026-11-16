using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Pankin.Platform.Linux;

/// <summary>
/// 将 Pankin 全局快捷键自动同步到 Niri 配置文件 (~/.config/niri/config.kdl) 并热重载。
/// </summary>
public sealed class NiriShortcutSync
{
    public const string BeginMarker = "// BEGIN PANKIN SHORTCUTS";
    public const string EndMarker = "// END PANKIN SHORTCUTS";

    private readonly string _configPath;
    private readonly Func<string?> _executablePathProvider;
    private readonly Func<bool> _isNiriSessionProvider;
    private readonly Func<string, bool> _configValidator;
    private readonly Action _configReloader;

    public NiriShortcutSync(
        string? configPath = null,
        Func<string?>? executablePathProvider = null,
        Func<bool>? isNiriSessionProvider = null,
        Func<string, bool>? configValidator = null,
        Action? configReloader = null)
    {
        _configPath = string.IsNullOrWhiteSpace(configPath) ? GetDefaultConfigPath() : configPath;
        _executablePathProvider = executablePathProvider ?? ResolvePersistentExecutablePath;
        _isNiriSessionProvider = isNiriSessionProvider ?? IsNiriSession;
        _configValidator = configValidator ?? ValidateWithNiri;
        _configReloader = configReloader ?? ReloadNiriConfig;
    }

    public static string GetDefaultConfigPath()
    {
        var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(configHome))
        {
            configHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        }
        return Path.Combine(configHome, "niri", "config.kdl");
    }

    public static bool IsNiriSession()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NIRI_SOCKET")))
        {
            return true;
        }

        var currentDesktop = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? string.Empty;
        if (currentDesktop.Contains("niri", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var sessionDesktop = Environment.GetEnvironmentVariable("XDG_SESSION_DESKTOP") ?? string.Empty;
        return sessionDesktop.Contains("niri", StringComparison.OrdinalIgnoreCase);
    }

    public static string? ResolvePersistentExecutablePath()
    {
        var appImage = Environment.GetEnvironmentVariable("APPIMAGE");
        if (!string.IsNullOrWhiteSpace(appImage) && File.Exists(appImage))
        {
            return Path.GetFullPath(appImage);
        }

        var executable = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(executable) &&
            !executable.StartsWith("/tmp/.mount_", StringComparison.Ordinal) &&
            !Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase) &&
            File.Exists(executable))
        {
            return Path.GetFullPath(executable);
        }

        return FindExecutableOnPath("pankin", "/usr/bin/pankin", "/usr/local/bin/pankin");
    }

    public static string? FormatNiriHotkey(string? hotkey)
    {
        if (string.IsNullOrWhiteSpace(hotkey)) return null;

        var parts = hotkey.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return null;

        bool ctrl = false;
        bool alt = false;
        bool shift = false;
        bool super = false;
        string? keyPart = null;

        foreach (var part in parts)
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    ctrl = true;
                    break;
                case "alt":
                case "opt":
                case "option":
                    alt = true;
                    break;
                case "shift":
                    shift = true;
                    break;
                case "super":
                case "win":
                case "windows":
                case "meta":
                    super = true;
                    break;
                default:
                    if (keyPart != null)
                    {
                        return null;
                    }
                    keyPart = part;
                    break;
            }
        }

        if (string.IsNullOrEmpty(keyPart)) return null;

        var niriKey = MapNiriKey(keyPart);
        if (niriKey == null) return null;

        var tokens = new List<string>(5);
        if (ctrl) tokens.Add("Ctrl");
        if (alt) tokens.Add("Alt");
        if (shift) tokens.Add("Shift");
        if (super) tokens.Add("Super");
        tokens.Add(niriKey);

        return string.Join('+', tokens);
    }

    public bool Sync(
        string launcherHotkey,
        string clipboardHotkey,
        string superPanelHotkey,
        string settingsHotkey = "Alt+,")
    {
        if (!_isNiriSessionProvider()) return false;

        var executablePath = _executablePathProvider();
        if (string.IsNullOrWhiteSpace(executablePath)) return false;

        if (!File.Exists(_configPath)) return false;

        var entries = new (string? Hotkey, string Flag, string Title)[]
        {
            (launcherHotkey, "--toggle", "Pankin: Toggle Launcher"),
            (clipboardHotkey, "--clipboard", "Pankin: Toggle Clipboard"),
            (superPanelHotkey, "--superpanel", "Pankin: Toggle Super Panel"),
            (settingsHotkey, "--settings", "Pankin: Open Settings")
        };

        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var escapedExe = EscapeKdlString(executablePath);
        var blockBuilder = new StringBuilder();
        blockBuilder.Append("    ").AppendLine(BeginMarker);

        int bindingCount = 0;
        foreach (var (hotkey, flag, title) in entries)
        {
            var niriKey = FormatNiriHotkey(hotkey);
            if (niriKey == null || !seenKeys.Add(niriKey))
            {
                continue;
            }

            blockBuilder.Append("    ")
                .Append(niriKey)
                .Append(" hotkey-overlay-title=\"")
                .Append(EscapeKdlString(title))
                .Append("\" { spawn \"")
                .Append(escapedExe)
                .Append("\" \"")
                .Append(flag)
                .AppendLine("\"; }");
            bindingCount++;
        }

        if (bindingCount == 0) return false;

        blockBuilder.Append("    ").Append(EndMarker);
        var blockContent = blockBuilder.ToString();

        var existingContent = File.ReadAllText(_configPath);
        var updatedContent = ApplyShortcutsBlock(existingContent, blockContent);

        if (string.Equals(updatedContent, existingContent, StringComparison.Ordinal))
        {
            return true;
        }

        var dir = Path.GetDirectoryName(_configPath);
        if (string.IsNullOrEmpty(dir))
        {
            dir = ".";
        }

        var tempPath = Path.Combine(dir, $".{Path.GetFileName(_configPath)}.pankin.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(tempPath, updatedContent, new UTF8Encoding(false));

            if (!_configValidator(tempPath))
            {
                return false;
            }

            File.Move(tempPath, _configPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
        }

        try
        {
            _configReloader();
        }
        catch
        {
            // Best-effort reload
        }

        return true;
    }

    public static string ApplyShortcutsBlock(string configContent, string blockContent)
    {
        var nl = configContent.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var normalizedBlock = NormalizeLineEndings(blockContent.TrimEnd('\r', '\n'), nl);

        if (TryFindMarkerRange(configContent, out var beginLineStart, out var endLineEnd))
        {
            if (string.IsNullOrEmpty(normalizedBlock))
            {
                var removeEnd = endLineEnd;
                if (removeEnd < configContent.Length && configContent[removeEnd] == '\r') removeEnd++;
                if (removeEnd < configContent.Length && configContent[removeEnd] == '\n') removeEnd++;
                return configContent.Remove(beginLineStart, removeEnd - beginLineStart);
            }

            return string.Concat(
                configContent.AsSpan(0, beginLineStart),
                normalizedBlock,
                configContent.AsSpan(endLineEnd));
        }

        if (TryFindTopLevelBindsBlock(configContent, out var openBraceIndex, out var closeBraceIndex))
        {
            var inner = configContent.Substring(openBraceIndex + 1, closeBraceIndex - openBraceIndex - 1);
            var cleanedInner = StripLegacyPankinLines(inner, nl);

            var closingIndentLength = 0;
            while (closingIndentLength < cleanedInner.Length &&
                   cleanedInner[cleanedInner.Length - 1 - closingIndentLength] is ' ' or '\t')
            {
                closingIndentLength++;
            }

            var closingIndent = closingIndentLength > 0
                ? cleanedInner.Substring(cleanedInner.Length - closingIndentLength)
                : string.Empty;
            var bodyBeforeClose = cleanedInner.Substring(0, cleanedInner.Length - closingIndentLength);

            var insertion = new StringBuilder();
            if (bodyBeforeClose.Length > 0 && !bodyBeforeClose.EndsWith('\n'))
            {
                insertion.Append(nl);
            }
            else if (bodyBeforeClose.Length == 0)
            {
                insertion.Append(nl);
            }

            insertion.Append(normalizedBlock).Append(nl).Append(closingIndent);

            return string.Concat(
                configContent.AsSpan(0, openBraceIndex + 1),
                bodyBeforeClose,
                insertion.ToString(),
                configContent.AsSpan(closeBraceIndex));
        }

        var appended = new StringBuilder(configContent);
        if (appended.Length > 0)
        {
            if (!configContent.EndsWith('\n'))
            {
                appended.Append(nl);
            }
            if (!configContent.EndsWith("\n\n", StringComparison.Ordinal) &&
                !configContent.EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                appended.Append(nl);
            }
        }

        appended.Append("binds {").Append(nl)
            .Append(normalizedBlock).Append(nl)
            .Append('}').Append(nl);

        return appended.ToString();
    }

    private static bool TryFindMarkerRange(string content, out int beginLineStart, out int endLineEnd)
    {
        beginLineStart = -1;
        endLineEnd = -1;

        int pos = 0;
        while (pos <= content.Length)
        {
            int lineEnd = content.IndexOf('\n', pos);
            int lineContentEnd = lineEnd >= 0 ? lineEnd : content.Length;
            if (lineContentEnd > pos && content[lineContentEnd - 1] == '\r')
            {
                lineContentEnd--;
            }

            var line = content.AsSpan(pos, lineContentEnd - pos).Trim();
            if (beginLineStart < 0)
            {
                if (line.Equals(BeginMarker, StringComparison.Ordinal))
                {
                    beginLineStart = pos;
                }
            }
            else if (line.Equals(EndMarker, StringComparison.Ordinal))
            {
                endLineEnd = lineContentEnd;
                return true;
            }

            if (lineEnd < 0) break;
            pos = lineEnd + 1;
        }

        beginLineStart = -1;
        endLineEnd = -1;
        return false;
    }

    private static bool TryFindTopLevelBindsBlock(string content, out int openBraceIndex, out int closeBraceIndex)
    {
        openBraceIndex = -1;
        closeBraceIndex = -1;

        int depth = 0;
        int i = 0;
        while (i < content.Length)
        {
            char c = content[i];

            if (c == '/' && i + 1 < content.Length)
            {
                if (content[i + 1] == '/')
                {
                    i = SkipLineComment(content, i + 2);
                    continue;
                }
                if (content[i + 1] == '*')
                {
                    i = SkipBlockComment(content, i + 2);
                    continue;
                }
            }

            if (c == '"')
            {
                i = SkipStringLiteral(content, i + 1);
                continue;
            }

            if (depth == 0 && c == 'b' &&
                i + 5 <= content.Length &&
                content.AsSpan(i, 5).Equals("binds", StringComparison.Ordinal) &&
                (i == 0 || !IsKdlIdentifierChar(content[i - 1])) &&
                (i + 5 == content.Length || !IsKdlIdentifierChar(content[i + 5])))
            {
                int lookahead = SkipWhitespaceAndComments(content, i + 5);
                if (lookahead < content.Length && content[lookahead] == '{')
                {
                    openBraceIndex = lookahead;
                    closeBraceIndex = FindMatchingBrace(content, lookahead);
                    return closeBraceIndex > openBraceIndex;
                }

                i += 5;
                continue;
            }

            if (c == '{')
            {
                depth++;
            }
            else if (c == '}' && depth > 0)
            {
                depth--;
            }

            i++;
        }

        return false;
    }

    private static int FindMatchingBrace(string content, int openBraceIndex)
    {
        int depth = 1;
        int i = openBraceIndex + 1;

        while (i < content.Length)
        {
            char c = content[i];

            if (c == '/' && i + 1 < content.Length)
            {
                if (content[i + 1] == '/')
                {
                    i = SkipLineComment(content, i + 2);
                    continue;
                }
                if (content[i + 1] == '*')
                {
                    i = SkipBlockComment(content, i + 2);
                    continue;
                }
            }

            if (c == '"')
            {
                i = SkipStringLiteral(content, i + 1);
                continue;
            }

            if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }

            i++;
        }

        return -1;
    }

    private static int SkipWhitespaceAndComments(string content, int index)
    {
        int i = index;
        while (i < content.Length)
        {
            if (char.IsWhiteSpace(content[i]))
            {
                i++;
                continue;
            }

            if (content[i] == '/' && i + 1 < content.Length)
            {
                if (content[i + 1] == '/')
                {
                    i = SkipLineComment(content, i + 2);
                    continue;
                }
                if (content[i + 1] == '*')
                {
                    i = SkipBlockComment(content, i + 2);
                    continue;
                }
            }

            break;
        }

        return i;
    }

    private static int SkipLineComment(string content, int index)
    {
        int nl = content.IndexOf('\n', index);
        return nl >= 0 ? nl + 1 : content.Length;
    }

    private static int SkipBlockComment(string content, int index)
    {
        int depth = 1;
        int i = index;
        while (i < content.Length - 1)
        {
            if (content[i] == '/' && content[i + 1] == '*')
            {
                depth++;
                i += 2;
            }
            else if (content[i] == '*' && content[i + 1] == '/')
            {
                depth--;
                i += 2;
                if (depth == 0) return i;
            }
            else
            {
                i++;
            }
        }
        return content.Length;
    }

    private static int SkipStringLiteral(string content, int index)
    {
        int i = index;
        while (i < content.Length)
        {
            if (content[i] == '\\')
            {
                i += 2;
                continue;
            }
            if (content[i] == '"')
            {
                return i + 1;
            }
            i++;
        }
        return content.Length;
    }

    private static bool IsKdlIdentifierChar(char c)
        => char.IsLetterOrDigit(c) || c == '_' || c == '-';

    private static string StripLegacyPankinLines(string inner, string nl)
    {
        var sb = new StringBuilder(inner.Length);
        int pos = 0;
        while (pos < inner.Length)
        {
            int nextNl = inner.IndexOf('\n', pos);
            bool hasNewline = nextNl >= 0;
            int rawEnd = hasNewline ? nextNl + 1 : inner.Length;
            int contentEnd = hasNewline ? nextNl : inner.Length;
            if (contentEnd > pos && inner[contentEnd - 1] == '\r')
            {
                contentEnd--;
            }

            var line = inner.AsSpan(pos, contentEnd - pos);
            if (!IsLegacyPankinLine(line))
            {
                sb.Append(inner.AsSpan(pos, rawEnd - pos));
            }

            pos = rawEnd;
        }

        return sb.ToString();
    }

    private static bool IsLegacyPankinLine(ReadOnlySpan<char> line)
    {
        var trimmed = line.Trim();
        if (trimmed.StartsWith("// Pankin Desktop Assistant", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (trimmed.Contains("spawn", StringComparison.Ordinal) &&
            trimmed.Contains("pankin", StringComparison.OrdinalIgnoreCase) &&
            (trimmed.Contains("--toggle", StringComparison.Ordinal) ||
             trimmed.Contains("--clipboard", StringComparison.Ordinal) ||
             trimmed.Contains("--superpanel", StringComparison.Ordinal) ||
             trimmed.Contains("--settings", StringComparison.Ordinal)))
        {
            return true;
        }

        return false;
    }

    private static string? MapNiriKey(string keyPart)
    {
        var lower = keyPart.ToLowerInvariant();
        switch (lower)
        {
            case "space": return "Space";
            case ",":
            case "comma": return "Comma";
            case ".":
            case "period": return "Period";
            case "-":
            case "minus": return "Minus";
            case "=":
            case "equals":
            case "equal": return "Equal";
            case ";":
            case "semicolon": return "Semicolon";
            case "/":
            case "slash": return "Slash";
            case "`":
            case "backquote":
            case "grave": return "Grave";
            case "[":
            case "openbracket": return "BracketLeft";
            case "\\":
            case "backslash": return "Backslash";
            case "]":
            case "closebracket": return "BracketRight";
            case "'":
            case "quote": return "Apostrophe";
            case "tab": return "Tab";
            case "enter":
            case "return": return "Return";
            case "esc":
            case "escape": return "Escape";
            case "backspace":
            case "back": return "BackSpace";
        }

        var upper = keyPart.ToUpperInvariant();
        if (Regex.IsMatch(upper, @"^F(\d{1,2})$") &&
            int.TryParse(upper.AsSpan(1), out int fNum) &&
            fNum is >= 1 and <= 24)
        {
            return "F" + fNum;
        }

        if (keyPart.Length == 1 && char.IsAsciiDigit(keyPart[0]))
        {
            return keyPart;
        }

        if (keyPart.Length == 2 && (keyPart[0] is 'D' or 'd') && char.IsAsciiDigit(keyPart[1]))
        {
            return keyPart[1..2];
        }

        if (keyPart.Length == 1 && char.IsAsciiLetter(keyPart[0]))
        {
            return char.ToUpperInvariant(keyPart[0]).ToString();
        }

        return null;
    }

    private static string EscapeKdlString(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\"", "\\\"", StringComparison.Ordinal);

    private static string NormalizeLineEndings(string text, string nl)
        => text.Replace("\r\n", "\n", StringComparison.Ordinal)
               .Replace("\n", nl, StringComparison.Ordinal);

    private static string? FindExecutableOnPath(string name, params string[] fallbackCandidates)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathEnv))
        {
            foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var candidate = Path.Combine(dir, name);
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
        }

        foreach (var candidate in fallbackCandidates)
        {
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    private static bool ValidateWithNiri(string tempConfigPath)
    {
        var niriPath = FindExecutableOnPath("niri", "/usr/bin/niri", "/usr/local/bin/niri");
        if (niriPath == null)
        {
            return true;
        }

        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = niriPath,
                ArgumentList = { "validate", "-c", tempConfigPath },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (proc == null) return false;

            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();

            if (!proc.WaitForExit(5000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return false;
            }

            Task.WaitAll([stdoutTask, stderrTask], 500);
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static void ReloadNiriConfig()
    {
        var niriPath = FindExecutableOnPath("niri", "/usr/bin/niri", "/usr/local/bin/niri");
        if (niriPath == null) return;

        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = niriPath,
                ArgumentList = { "msg", "action", "load-config-file" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (proc == null) return;

            if (!proc.WaitForExit(3000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
            }
        }
        catch
        {
            // Ignore reload errors
        }
    }
}
