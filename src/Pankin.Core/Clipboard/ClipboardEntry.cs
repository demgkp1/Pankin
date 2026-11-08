using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Pankin.Core.Clipboard;

public enum ClipKind
{
    Text = 0,
    Code = 1,
    Link = 2,
    Color = 3,
    Image = 4,
    Files = 5
}

public sealed record ClipboardEntry(
    string Id,
    ClipKind Kind,
    string? Text,
    IReadOnlyList<string>? Files,
    string? ContentPath,
    int? ImageWidth,
    int? ImageHeight,
    string Source,
    DateTimeOffset CopiedAt,
    bool Pinned,
    string Fingerprint);

public static partial class ClipboardClassifier
{
    [GeneratedRegex(@"^#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$")]
    private static partial Regex HexColorRegex();

    [GeneratedRegex(@"^(rgb|rgba)\(\s*(\d{1,3})\s*,\s*(\d{1,3})\s*,\s*(\d{1,3})(?:\s*,\s*(0|1|0?\.\d+|1\.0+))?\s*\)$", RegexOptions.IgnoreCase)]
    private static partial Regex RgbColorRegex();

    [GeneratedRegex(@"\b(class|public|private|protected|internal|return|function|def|fn|const|let|var|if|else|for|while|import|export|using|namespace|struct|interface)\b")]
    private static partial Regex CodeKeywordRegex();

    public static ClipKind ClassifyText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return ClipKind.Text;

        string trimmed = value.Trim();

        // 链接判定
        if (!trimmed.Any(char.IsWhiteSpace) &&
            Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) &&
            uri.Scheme is "http" or "https" or "ftp" &&
            !string.IsNullOrEmpty(uri.Host))
        {
            return ClipKind.Link;
        }

        // 颜色判定
        if (HexColorRegex().IsMatch(trimmed))
            return ClipKind.Color;

        var rgbMatch = RgbColorRegex().Match(trimmed);
        if (rgbMatch.Success)
        {
            if (int.TryParse(rgbMatch.Groups[2].Value, out int r) && r <= 255 &&
                int.TryParse(rgbMatch.Groups[3].Value, out int g) && g <= 255 &&
                int.TryParse(rgbMatch.Groups[4].Value, out int b) && b <= 255)
            {
                return ClipKind.Color;
            }
        }

        // 代码判定：包含换行，具有缩进或花括号分号，且包含关键字
        if (trimmed.Contains('\n'))
        {
            var lines = value.Split('\n');
            bool hasIndentation = lines.Any(l => l.StartsWith("    ") || l.StartsWith('\t'));
            bool hasCodeStructure = (trimmed.Contains('{') && trimmed.Contains('}')) || trimmed.Contains(';') || lines[0].TrimEnd().EndsWith(':');
            if (hasIndentation && hasCodeStructure && CodeKeywordRegex().IsMatch(trimmed))
            {
                return ClipKind.Code;
            }
        }

        return ClipKind.Text;
    }

    public static string ComputeFingerprint(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var hash = SHA256.HashData(bytes);
        return "t:" + Convert.ToHexString(hash);
    }

    public static string ComputeFilesFingerprint(IEnumerable<string> files)
    {
        var combined = string.Join('\0', files);
        var bytes = Encoding.UTF8.GetBytes(combined);
        var hash = SHA256.HashData(bytes);
        return "f:" + Convert.ToHexString(hash);
    }
}
