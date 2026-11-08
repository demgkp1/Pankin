using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pankin.Core.Rename;

namespace Pankin.Desktop.ViewModels;

public sealed partial class RenameRuleItemViewModel : ObservableObject
{
    public RenameRule Rule { get; }
    public string DisplayText { get; }

    public RenameRuleItemViewModel(RenameRule rule)
    {
        Rule = rule;
        DisplayText = rule.Kind switch
        {
            RenameRuleKind.Replace => $"替换: \"{rule.Find}\" -> \"{rule.Text}\"{(rule.Regex ? " (正则)" : "")}",
            RenameRuleKind.Insert => $"插入: \"{rule.Text}\" @ {rule.Position}",
            RenameRuleKind.Number => $"序号: 起始 {rule.Start}, 步长 {rule.Step}, {rule.Digits}位",
            RenameRuleKind.Remove => $"删除: 从第 {rule.Index} 字起删 {rule.Count} 字{(rule.FromEnd ? " (倒序)" : "")}",
            RenameRuleKind.Case => $"大小写: {rule.Case}{(rule.IncludeExtension ? " (含扩展名)" : "")}",
            RenameRuleKind.Extension => $"扩展名: {rule.Text}",
            RenameRuleKind.Date => $"日期: {rule.DateFormat} ({rule.DateSource})",
            _ => rule.Kind.ToString()
        };
    }
}

public sealed partial class RenamePreviewItemViewModel : ObservableObject
{
    public string Path { get; }
    public string OldName { get; }

    [ObservableProperty]
    private string _newName;

    [ObservableProperty]
    private RenameIssue _issue;

    public string IssueBadge => Issue switch
    {
        RenameIssue.None => "就绪",
        RenameIssue.Unchanged => "未变更",
        RenameIssue.Duplicate => "重名冲突",
        RenameIssue.Exists => "已存在",
        RenameIssue.Empty => "名称为空",
        RenameIssue.Invalid => "非法字符",
        RenameIssue.TooLong => "超长路径",
        RenameIssue.Missing => "文件不存在",
        RenameIssue.BadPattern => "正则错误",
        _ => Issue.ToString()
    };

    public string BadgeColor => Issue switch
    {
        RenameIssue.None => "#10B981",
        RenameIssue.Unchanged => "#6B7280",
        _ => "#EF4444"
    };

    public RenamePreviewItemViewModel(string path, string oldName, string newName, RenameIssue issue)
    {
        Path = path;
        OldName = oldName;
        _newName = newName;
        _issue = issue;
    }
}

public partial class RenameViewModel : ViewModelBase
{
    private readonly IBatchRenamer _renamer;
    private readonly List<string> _filePaths = [];

    public ObservableCollection<RenameRuleItemViewModel> Rules { get; } = [];
    public ObservableCollection<RenamePreviewItemViewModel> Previews { get; } = [];

    [ObservableProperty]
    private string _statusText = "请添加文件并设置规则进行重命名";

    [ObservableProperty]
    private string? _lastUndoId;

    [ObservableProperty]
    private bool _canUndo;

    [ObservableProperty]
    private bool _canApply;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private int _selectedRuleTypeIndex = 0; // 0: Replace, 1: Insert, 2: Number, 3: Case, 4: Extension

    [ObservableProperty]
    private string _findText = string.Empty;

    [ObservableProperty]
    private string _replaceText = string.Empty;

    [ObservableProperty]
    private bool _isRegex;

    [ObservableProperty]
    private bool _matchCase;

    [ObservableProperty]
    private int _startNumber = 1;

    [ObservableProperty]
    private int _digits = 2;

    [ObservableProperty]
    private string _separator = "_";

    public RenameViewModel(IBatchRenamer? renamer = null)
    {
        _renamer = renamer ?? new BatchRenamer();
    }

    public void AddFiles(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (File.Exists(path) || Directory.Exists(path))
            {
                if (!_filePaths.Contains(path, StringComparer.OrdinalIgnoreCase))
                {
                    _filePaths.Add(path);
                }
            }
        }
        _ = RefreshPreviewAsync();
    }

    [RelayCommand]
    public void AddRule()
    {
        RenameRule rule = SelectedRuleTypeIndex switch
        {
            0 => new RenameRule(RenameRuleKind.Replace)
            {
                Find = FindText,
                Text = ReplaceText,
                Regex = IsRegex,
                MatchCase = MatchCase
            },
            1 => new RenameRule(RenameRuleKind.Insert)
            {
                Text = ReplaceText,
                Position = RenamePosition.Start
            },
            2 => new RenameRule(RenameRuleKind.Number)
            {
                Start = StartNumber,
                Digits = Digits,
                Separator = Separator,
                Position = RenamePosition.End
            },
            3 => new RenameRule(RenameRuleKind.Case)
            {
                Case = RenameCase.Lower
            },
            4 => new RenameRule(RenameRuleKind.Extension)
            {
                Text = ReplaceText.TrimStart(".".ToCharArray())
            },
            _ => new RenameRule(RenameRuleKind.Replace) { Find = FindText, Text = ReplaceText }
        };

        Rules.Add(new RenameRuleItemViewModel(rule));
        _ = RefreshPreviewAsync();
    }

    [RelayCommand]
    public void RemoveRule(RenameRuleItemViewModel? item)
    {
        if (item != null && Rules.Remove(item))
        {
            _ = RefreshPreviewAsync();
        }
    }

    [RelayCommand]
    public void ClearRules()
    {
        Rules.Clear();
        _ = RefreshPreviewAsync();
    }

    [RelayCommand]
    public async Task RefreshPreviewAsync()
    {
        if (_filePaths.Count == 0)
        {
            Previews.Clear();
            CanApply = false;
            StatusText = "请先添加待重命名的文件";
            return;
        }

        IsBusy = true;
        try
        {
            var rulesList = Rules.Select(r => r.Rule).ToList();
            var results = await _renamer.PreviewAsync(_filePaths, rulesList).ConfigureAwait(true);

            Previews.Clear();
            bool hasErrors = false;
            int changedCount = 0;

            foreach (var preview in results)
            {
                var oldName = System.IO.Path.GetFileName(preview.Path);
                Previews.Add(new RenamePreviewItemViewModel(preview.Path, oldName, preview.NewName, preview.Issue));

                if (preview.Issue is not (RenameIssue.None or RenameIssue.Unchanged))
                {
                    hasErrors = true;
                }
                if (preview.Issue == RenameIssue.None && preview.NewName != oldName)
                {
                    changedCount++;
                }
            }

            CanApply = !hasErrors && changedCount > 0;
            StatusText = hasErrors ? $"发现命名冲突或错误，请修正规则"
                : $"预览完成：共 {_filePaths.Count} 项，{changedCount} 项待修改";
        }
        catch (Exception ex)
        {
            StatusText = $"预览失败: {ex.Message}";
            CanApply = false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task ApplyRenameAsync()
    {
        if (!CanApply || Previews.Count == 0) return;

        IsBusy = true;
        try
        {
            var plan = Previews.Select(p => new RenamePreview(p.Path, p.NewName, p.Issue)).ToList();
            var result = await _renamer.ApplyAsync(plan).ConfigureAwait(true);

            if (result.Failed.Count == 0 && result.UndoId != null)
            {
                LastUndoId = result.UndoId;
                CanUndo = true;
                StatusText = $"重命名成功！已成功重命名 {result.Renamed} 个项目。按 Ctrl+Z 可撤销";

                // 更新文件路径为新路径
                _filePaths.Clear();
                foreach (var p in Previews)
                {
                    var dir = System.IO.Path.GetDirectoryName(p.Path)!;
                    _filePaths.Add(System.IO.Path.Combine(dir, p.NewName));
                }

                await RefreshPreviewAsync().ConfigureAwait(true);
            }
            else
            {
                StatusText = $"部分或全部重命名失败：成功 {result.Renamed} 项，失败 {result.Failed.Count} 项";
            }
        }
        catch (Exception ex)
        {
            StatusText = $"执行失败: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task UndoAsync()
    {
        if (string.IsNullOrEmpty(LastUndoId)) return;

        IsBusy = true;
        try
        {
            var result = await _renamer.UndoAsync(LastUndoId).ConfigureAwait(true);
            StatusText = $"撤销完成：已恢复 {result.Renamed} 个文件";
            LastUndoId = result.UndoId;
            CanUndo = !string.IsNullOrEmpty(LastUndoId);

            // 重新刷新
            await RefreshPreviewAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusText = $"撤销失败: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
