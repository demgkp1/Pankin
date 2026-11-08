using System;
using System.Collections.Generic;
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
            var entries = await _store.SearchAsync(context.Query, Limit(context), token).ConfigureAwait(false);
            if (entries.Count == 0) return Array.Empty<LauncherItem>();

            var items = new List<LauncherItem>(entries.Count);
            foreach (var entry in entries)
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

            return items;
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
