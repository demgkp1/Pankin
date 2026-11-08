using System;
using Avalonia.Data;
using Pankin.Core.Localization;

namespace Pankin.Desktop.Localization;

/// <summary>
/// Avalonia XAML 本地化标记扩展：{loc:Loc KeyName}
/// 返回动态绑定至 Strings.Current[KeyName] 的 Binding，支持运行时语言热切换。
/// </summary>
public sealed class LocExtension
{
    public string Key { get; set; } = string.Empty;

    public LocExtension()
    {
    }

    public LocExtension(string key)
    {
        Key = key;
    }

    public IBinding ProvideValue(IServiceProvider serviceProvider)
    {
        return new Binding($"[{Key}]")
        {
            Source = Strings.Current,
            Mode = BindingMode.OneWay
        };
    }
}
