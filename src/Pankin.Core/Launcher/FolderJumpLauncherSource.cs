using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Pankin.Core.Abstractions;
using Pankin.Core.FolderJump;
using Pankin.Core.Settings;

namespace Pankin.Core.Launcher;

/// <summary>
/// 文件夹跳转搜索来源 (基于 FolderJumpStore 拼音模糊检索，通过 xdg-open 跳转)
/// </summary>
public sealed class FolderJumpLauncherSource : LauncherSource
{
    private readonly FolderJumpStore _store;
    private readonly ILauncherActionHost _actionHost;

    public FolderJumpLauncherSource(FolderJumpStore store, ILauncherActionHost actionHost)
        : base(LauncherGroups.FolderJump, 2, "folder-jump:")
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _actionHost = actionHost ?? throw new ArgumentNullException(nameof(actionHost));
    }

    public override int Limit(SourceContext context) => 5;

    public override TimeSpan Timeout => TimeSpan.FromMilliseconds(300);

    public override async Task<IReadOnlyList<LauncherItem>> SearchAsync(SourceContext context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (context.Query.Length == 0 || context.Query.StartsWith('>'))
        {
            return Array.Empty<LauncherItem>();
        }

        if (!context.AllSettings.Modules.FolderJump)
        {
            return Array.Empty<LauncherItem>();
        }

        try
        {
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var items = new List<LauncherItem>();

            var entries = await _store.SearchAsync(context.Query, Limit(context), token).ConfigureAwait(false);
            foreach (var entry in entries)
            {
                if (seenPaths.Add(entry.Path))
                {
                    items.Add(new LauncherItem(
                        "folder-jump:" + entry.Path,
                        LauncherItemKind.Folder,
                        Group,
                        entry.Name,
                        entry.Path,
                        new LauncherIcon("folder"),
                        Score: entry.IsPinned ? 85 : 70));
                }
            }

            if (context.Settings?.FavoriteFolders != null)
            {
                var matcher = PinyinMatcher.Instance;
                foreach (var fav in context.Settings.FavoriteFolders)
                {
                    if (string.IsNullOrWhiteSpace(fav)) continue;
                    var normFav = Path.GetFullPath(fav).TrimEnd(Path.DirectorySeparatorChar);
                    if (seenPaths.Contains(normFav) || !Directory.Exists(normFav)) continue;

                    var name = Path.GetFileName(normFav);
                    if (string.IsNullOrEmpty(name)) name = normFav;

                    var match = matcher.Match(name, context.Query);
                    int score = 0;
                    if (match != null)
                    {
                        score = match.Score + 20;
                    }
                    else if (normFav.Contains(context.Query, StringComparison.OrdinalIgnoreCase))
                    {
                        score = 60;
                    }

                    if (score > 0 && seenPaths.Add(normFav))
                    {
                        items.Add(new LauncherItem(
                            "folder-jump:" + normFav,
                            LauncherItemKind.Folder,
                            Group,
                            name,
                            normFav,
                            new LauncherIcon("folder"),
                            Score: score));
                    }
                }
            }

            return items.OrderByDescending(i => i.Score).Take(Limit(context)).ToList();
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return Array.Empty<LauncherItem>();
        }
        catch
        {
            return Array.Empty<LauncherItem>();
        }
    }

    public override async Task ExecuteAsync(LauncherItem item, SourceContext context, CancellationToken token)
    {
        var path = item.Id.StartsWith("folder-jump:", StringComparison.Ordinal)
            ? item.Id[12..]
            : (item.Subtitle ?? item.Title);

        await _store.RecordAccessAsync(path, token).ConfigureAwait(false);
        await _actionHost.OpenAsync(path, false, token).ConfigureAwait(false);
    }
}
