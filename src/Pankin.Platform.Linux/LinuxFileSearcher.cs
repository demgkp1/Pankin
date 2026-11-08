using System.Diagnostics;
using System.Runtime.CompilerServices;
using Pankin.Core.Abstractions;

namespace Pankin.Platform.Linux;

/// <summary>
/// 基于 Ubuntu 24.04 plocate 的本地文件毫秒级流式检索实现
/// </summary>
public sealed class LinuxFileSearcher : IFileSearcher
{
    public async IAsyncEnumerable<FileSearchResult> SearchFilesAsync(
        string query,
        int limit = 50,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            yield break;
        }

        var trimmed = query.Trim();
        var effectiveLimit = Math.Clamp(limit, 1, 500);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "plocate",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        // plocate -i (忽略大小写) -l <limit> (限制返回条数) -- <pattern>
        process.StartInfo.ArgumentList.Add("-i");
        process.StartInfo.ArgumentList.Add("-l");
        process.StartInfo.ArgumentList.Add(effectiveLimit.ToString());
        process.StartInfo.ArgumentList.Add("--");
        process.StartInfo.ArgumentList.Add(trimmed);

        bool started;
        try
        {
            started = process.Start();
        }
        catch
        {
            yield break;
        }

        if (!started)
        {
            yield break;
        }

        using var registration = ct.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Ignore race condition on process exit
            }
        });

        var reader = process.StandardOutput;
        int count = 0;

        while (!ct.IsCancellationRequested && count < effectiveLimit)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
            catch
            {
                yield break;
            }

            if (line is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var fullPath = line.Trim();
            var fileName = Path.GetFileName(fullPath);
            if (string.IsNullOrEmpty(fileName))
            {
                fileName = fullPath;
            }

            var dirPath = Path.GetDirectoryName(fullPath) ?? "/";
            var isDir = Directory.Exists(fullPath);

            count++;
            yield return new FileSearchResult(fullPath, fileName, dirPath, isDir);
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Ignore
        }
    }
}
