using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Pankin.Core.Selection;

public enum SelectionFailure
{
    None = 0,
    Empty = 1,
    Unsupported = 2,
    Blacklisted = 3,
    Timeout = 4,
    ClipboardUnavailable = 5
}

public enum SelectionKind
{
    Text = 0,
    Url = 1,
    FilePath = 2,
    Files = 3,
    MultiLineText = 4
}

public sealed record SelectionContext(
    string? Text,
    IReadOnlyList<string>? Files = null,
    string? SourceAppName = null,
    string? WindowTitle = null,
    double PointerX = 0,
    double PointerY = 0,
    DateTime Timestamp = default,
    SelectionFailure Failure = SelectionFailure.None)
{

    public bool HasText => !string.IsNullOrWhiteSpace(Text);
    public bool HasFiles => Files is { Count: > 0 };
    public bool IsSuccess => Failure == SelectionFailure.None && (HasText || HasFiles);

    public bool IsUrl => DetectIsUrl(Text);
    public bool IsFilePath => DetectIsFilePath(Text);

    public SelectionKind Kind => DetectKind();

    public string PreviewText
    {
        get
        {
            if (HasFiles)
            {
                return Files!.Count == 1
                    ? Path.GetFileName(Files[0])
                    : $"{Files.Count} 个文件";
            }

            if (string.IsNullOrWhiteSpace(Text)) return string.Empty;

            var trimmed = Text.Trim();
            var firstLine = trimmed.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
            return firstLine.Length > 80 ? firstLine[..80] + "..." : firstLine;
        }
    }

    public int WordCount
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Text)) return 0;
            return Text.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Length;
        }
    }

    public static SelectionContext CreateText(
        string text,
        double x = 0,
        double y = 0,
        string? appName = null,
        string? title = null)
    {
        return new SelectionContext(
            Text: text,
            Files: null,
            SourceAppName: appName,
            WindowTitle: title,
            PointerX: x,
            PointerY: y,
            Timestamp: DateTime.UtcNow,
            Failure: string.IsNullOrWhiteSpace(text) ? SelectionFailure.Empty : SelectionFailure.None);
    }

    public static SelectionContext CreateFiles(
        IReadOnlyList<string> files,
        double x = 0,
        double y = 0,
        string? appName = null,
        string? title = null)
    {
        return new SelectionContext(
            Text: null,
            Files: files,
            SourceAppName: appName,
            WindowTitle: title,
            PointerX: x,
            PointerY: y,
            Timestamp: DateTime.UtcNow,
            Failure: files.Count == 0 ? SelectionFailure.Empty : SelectionFailure.None);
    }

    public static SelectionContext CreateFailure(
        SelectionFailure failure,
        double x = 0,
        double y = 0,
        string? appName = null,
        string? title = null)
    {
        return new SelectionContext(
            Text: null,
            Files: null,
            SourceAppName: appName,
            WindowTitle: title,
            PointerX: x,
            PointerY: y,
            Timestamp: DateTime.UtcNow,
            Failure: failure);
    }

    private SelectionKind DetectKind()
    {
        if (HasFiles) return SelectionKind.Files;
        if (IsUrl) return SelectionKind.Url;
        if (IsFilePath) return SelectionKind.FilePath;
        if (Text != null && (Text.Contains('\n') || Text.Contains('\r'))) return SelectionKind.MultiLineText;
        return SelectionKind.Text;
    }

    public static bool DetectIsUrl(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var trimmed = text.Trim();
        if (trimmed.Contains(' ') || trimmed.Contains('\t') || trimmed.Contains('\n')) return false;

        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase))
        {
            return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) &&
                   (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeFtp);
        }

        if (trimmed.StartsWith("www.", StringComparison.OrdinalIgnoreCase) && trimmed.Length > 6 && trimmed.Contains('.'))
        {
            return Uri.TryCreate("https://" + trimmed, UriKind.Absolute, out _);
        }

        return false;
    }

    public static bool DetectIsFilePath(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var trimmed = text.Trim();
        if (trimmed.Contains('\n') || trimmed.Contains('\r')) return false;

        if (trimmed.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (trimmed.StartsWith('/') && trimmed.Length >= 2)
        {
            // Verify path chars
            var invalid = Path.GetInvalidPathChars();
            if (trimmed.IndexOfAny(invalid) < 0)
            {
                return true;
            }
        }

        return false;
    }
}
