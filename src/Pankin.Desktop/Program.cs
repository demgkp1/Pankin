using Avalonia;
using Avalonia.Media;
using System;

namespace Pankin.Desktop;

public sealed class Program
{
    private const string InterFontUri = "avares://Avalonia.Fonts.Inter/Assets#Inter";

    [STAThread]
    public static void Main(string[] args)
    {
        ConfigureLinuxFontEnvironment();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        ConfigureLinuxFontEnvironment();
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .With(CreateFontManagerOptions())
            .With(new X11PlatformOptions { WmClass = "pankin" })
            .LogToTrace();
    }

    public static FontManagerOptions CreateFontManagerOptions() => new()
    {
        DefaultFamilyName = InterFontUri,
        FontFallbacks =
        [
            new FontFallback { FontFamily = new FontFamily("Noto Sans CJK SC") },
            new FontFallback { FontFamily = new FontFamily("Noto Sans SC") }
        ]
    };

    private static void ConfigureLinuxFontEnvironment()
    {
        // Under CJK locales (e.g. LANG=zh_CN.UTF-8) with fonts-noto-cjk installed,
        // fontconfig resolves default sans queries to NotoSansCJK-Regular.ttc, which
        // Avalonia/Skia cannot open as $Default. Setting FC_LANG=en only when unset
        // keeps native Skia/GTK fontconfig lookups on a single TTF while CJK text still
        // renders via explicit FontFallbacks.
        if (OperatingSystem.IsLinux() && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FC_LANG")))
        {
            Environment.SetEnvironmentVariable("FC_LANG", "en");
        }
    }
}
