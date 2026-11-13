using System.Diagnostics;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Avalonia.Platform;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pankin.Core.Abstractions;
using Pankin.Core.Launcher;
using Pankin.Core.Settings;
using Pankin.Desktop;
using Pankin.Desktop.Services;
using Pankin.Desktop.ViewModels;
using Pankin.Desktop.Views;
using Pankin.Platform.Linux;

// A native-window integration check, not a second launcher or HTML mockup.
// All settings/data are isolated; hooks, clipboard monitoring and app startup are not started.
internal static class Program
{
    private static int _checks;
    private static string _output = "";
    private static readonly CancellationTokenSource Exit = new();

    [STAThread]
    public static int Main(string[] args)
    {
        _output = Path.GetFullPath(args.FirstOrDefault() ?? "artifacts/launcher-verification");
        Directory.CreateDirectory(_output);
        var isolation = Path.Combine(Path.GetTempPath(), "pankin-verify-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(isolation, "config"));
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(isolation, "data"));
        var code = 0;
        AppBuilder.Configure<VerificationApp>().UsePlatformDetect().WithInterFont()
            .With(new X11PlatformOptions { WmClass = "pankin-verification" }).LogToTrace().SetupWithoutStarting();
        Dispatcher.UIThread.Post(async () =>
        {
            try { await Verify(); Console.WriteLine($"PASS: {_checks} checks; output={_output}"); }
            catch (Exception ex) { Console.Error.WriteLine(ex); code = 1; }
            finally { DesktopAppContext.Instance.Dispose(); Exit.Cancel(); }
        });
        Dispatcher.UIThread.MainLoop(Exit.Token);
        Directory.Delete(isolation, recursive: true);
        return code;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + message);
        Console.WriteLine("PASS: " + message);
        _checks++;
    }

    private static async Task Verify()
    {
        VerifyIcons();
        var context = DesktopAppContext.Instance;
        context.SettingsStore.Update(s => s with
        {
            General = s.General with { HideOnDeactivate = false, RememberWindowPosition = false, Theme = ThemeMode.Dark },
            Launcher = s.Launcher with { WebSuggestions = false }
        });
        var vm = new MainViewModel();
        await context.LauncherSearch.RefreshAppsAsync();
        vm.LoadDefaultApps();
        var window = new MainWindow { DataContext = vm, Title = "Pankin · visual verification" };
        App.ApplyAppearance(context.SettingsStore.Current.General);
        window.Show();
        window.Activate();
        await Task.Delay(600);
        Console.WriteLine($"Backend={window.TryGetPlatformHandle()?.HandleDescriptor}; transparency={window.ActualTransparencyLevel}; scale={window.RenderScaling}; session={Environment.GetEnvironmentVariable("XDG_SESSION_TYPE")}");
        Check(window.TryGetPlatformHandle()?.HandleDescriptor == "XID", "native X11 window created");
        Check(window.FindControl<Border>("ReadabilityShield")!.IsVisible ==
              (window.ActualTransparencyLevel != WindowTransparencyLevel.Blur && window.ActualTransparencyLevel != WindowTransparencyLevel.AcrylicBlur), "material follows reported capability, not hint");
        Check(vm.RecentApps.Count > 0, "real Linux applications scanned");
        Console.WriteLine($"Icons: {vm.RecentApps.Count(a => a.IconBitmap != null)}/{vm.RecentApps.Count} recent apps decoded");
        await Capture(window, "home-dark");

        var recent = window.FindControl<ItemsControl>("RecentItems")!;
        var pins = window.FindControl<ItemsControl>("PinnedItems")!;
        var tiles = recent.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("app-tile")).ToArray();
        Check(tiles.Length > 6 && tiles.Take(7).Select(t => t.TranslatePoint(default, recent)!.Value.Y).Distinct().Count() == 1,
            "seven home tiles per row at current DPI");
        Check(recent.TranslatePoint(default, window)!.Value.Y < pins.TranslatePoint(default, window)!.Value.Y, "recent applications precede pins");
        Check(Math.Abs(tiles[0].Bounds.Width - 82) <= 1 / window.RenderScaling && Math.Abs(tiles[0].Bounds.Height - 86) <= 1 / window.RenderScaling, "logical tile size is 82×86 DIP within one physical rounding pixel");
        var search = window.FindControl<TextBox>("SearchInput")!;
        window.FocusSearchInput();
        SendKey(window, Key.Down);
        Check(window.FocusManager!.GetFocusedElement() is Button { DataContext: AppTileItem }, "Down enters home grid");
        await Capture(window, "home-keyboard-focus");
        var focusedPresenter = ((Button)window.FocusManager.GetFocusedElement()!).GetVisualDescendants()
            .OfType<Avalonia.Controls.Presenters.ContentPresenter>().First();
        Check(focusedPresenter.BorderBrush is ISolidColorBrush focusBrush && focusBrush.Color == Color.Parse("#8B8DFF"),
            "keyboard focus has an accent outline distinct from hover");
        SendKey(window, Key.Right);
        Check(ReferenceEquals(window.FocusManager.GetFocusedElement(), tiles[1]), "Right moves one tile");
        SendKey(window, Key.Up);
        Check(ReferenceEquals(window.FocusManager.GetFocusedElement(), search), "Up returns to search");
        SendKey(window, Key.Down);
        var pinId = ((AppTileItem)((Button)window.FocusManager.GetFocusedElement()!).DataContext!).Id;
        SendKey(window, Key.P, KeyModifiers.Control);
        await Task.Delay(120);
        Check(vm.IsAppPinned(pinId), "Ctrl+P pins focused home app");
        vm.TogglePin(pinId);
        await Task.Delay(100);
        Check(!vm.IsAppPinned(pinId), "pin toggle can be reversed");

        while (vm.RecentApps.Count > 3) vm.RecentApps.RemoveAt(vm.RecentApps.Count - 1);
        await Task.Delay(100);
        window.FocusSearchInput();
        SendKey(window, Key.Down);
        SendKey(window, Key.Right);
        SendKey(window, Key.Right);
        SendKey(window, Key.Down);
        Check(window.FocusManager.GetFocusedElement() is Button { DataContext: AppTileItem crossGroup }
            && crossGroup.Id == vm.PinnedTools[2].Id, "Down aligns by column across an incomplete recent row");
        vm.LoadDefaultApps();
        vm.SearchQuery = "config.toml";
        window.FocusSearchInput();
        await WaitForResults(vm);
        Check(vm.SearchResults.Count > 0, "real search returns results");
        var selectedContainer = (ListBoxItem)window.FindControl<ListBox>("ResultListBox")!.ContainerFromIndex(vm.SelectedIndex)!;
        var presenter = selectedContainer.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>().First();
        Check(presenter.Background is ISolidColorBrush background && background.Color.A == 0,
            "selection background excludes group heading (no Fluent selection paint)");
        await Capture(window, "search-dark");
        int before = vm.SelectedIndex;
        SendKey(window, Key.Down);
        Check(vm.SelectedIndex == Math.Min(before + 1, vm.SearchResults.Count - 1), "Down navigates search results");
        SendKey(window, Key.Up);
        Check(vm.SelectedIndex == Math.Max(0, Math.Min(before + 1, vm.SearchResults.Count - 1) - 1), "Up navigates search results");

        var fakeSearch = new RecordingSearch();
        var item = new LauncherItem("verify:action", LauncherItemKind.Action, LauncherGroups.Commands,
            "验证动作（不会启动外部程序）", "", new LauncherIcon(""));
        vm.SearchResults.Clear();
        vm.SearchResults.Add(new LauncherListItem(item, "指令", "#888888", "Alt 1", "指令", true,
            fakeSearch, context.ActionHost, null));
        vm.SelectedIndex = 0;
        SendKey(window, Key.Enter);
        await Task.Delay(100);
        Check(fakeSearch.Executions == 1, "Enter dispatches selected search command once");
        SendKey(window, Key.D1, KeyModifiers.Alt);
        await Task.Delay(100);
        Check(fakeSearch.Executions == 2, "Alt+1 dispatches indexed command");

        foreach (var theme in new[] { ThemeMode.Light, ThemeMode.Dark })
        {
            foreach (var (opacity, radius) in new[] { (0.4, 0d), (0.94, 16d), (1d, 28d) })
            {
                var general = context.SettingsStore.Current.General with { Theme = theme, PanelOpacity = opacity, PanelRadius = radius, AccentColor = "#5588AA" };
                context.SettingsStore.Update(s => s with { General = general });
                App.ApplyAppearance(general);
                await Task.Delay(100);
                var brush = (SolidColorBrush)Application.Current!.Resources["Pk.LauncherSurface"]!;
                Check(brush.Color.A == (byte)Math.Round(opacity * 255), $"{theme}: saved tint alpha {opacity} retained");
                Check(window.FindControl<Border>("LauncherPanel")!.CornerRadius.TopLeft == radius, $"{theme}: radius {radius} applied");
                Check(search.Opacity == 1, "text opacity is independent of material");
                Check(((SolidColorBrush)Application.Current.Resources["Pk.Accent"]!).Color == Color.Parse("#5588AA"), "custom accent retained");
                await Capture(window, $"material-{theme.ToString().ToLowerInvariant()}-{opacity:0.00}-r{radius}");
            }
        }
        context.SettingsStore.Update(s => s with { General = s.General with { Theme = ThemeMode.Light, PanelOpacity = .94, PanelRadius = 16, AccentColor = "#8B8DFF" } });
        App.ApplyAppearance(context.SettingsStore.Current.General);
        vm.SearchQuery = "config.toml ";
        await WaitForResults(vm);
        await Capture(window, "search-light");
        vm.SearchQuery = "";
        await Task.Delay(100);
        vm.LoadDefaultApps();
        await Capture(window, "home-light");

        int launches = 0;
        var longName = "这是一个用于验证长名称与图标缺失的应用 — Very Long Application Name";
        vm.RecentApps.Insert(0, new AppTileItem("verification:missing", longName, "", "#888888", "?", null, () => launches++));
        await Capture(window, "edge-long-name-missing-icon");
        var edge = recent.GetVisualDescendants().OfType<Button>().First(b => b.DataContext is AppTileItem a && a.Id == "verification:missing");
        Check(Math.Abs(edge.Bounds.Width - 82) <= 1 / window.RenderScaling, "long name does not widen tile");
        edge.Focus(NavigationMethod.Tab);
        edge.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        edge.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = Key.Enter });
        Check(launches == 1, "Enter on home button dispatches its command");
        int openedSettings = 0;
        vm.RequestOpenSettings += _ => openedSettings++;
        SendKey(window, Key.OemComma, KeyModifiers.Alt);
        Check(openedSettings == 1, "Alt+, opens settings entry");
        SendKey(window, Key.Escape);
        Check(!window.IsVisible, "Escape hides window");
        window.Close();
    }

    private static void SendKey(Window window, Key key, KeyModifiers modifiers = KeyModifiers.None) =>
        window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers });

    private static async Task WaitForResults(MainViewModel vm)
    {
        await Task.Delay(1200);
        for (int i = 0; i < 30 && vm.SearchResults.Count == 0; i++) await Task.Delay(100);
    }

    private static async Task Capture(Window window, string name)
    {
        await Task.Delay(200);
        foreach (var control in window.GetVisualDescendants().OfType<Control>()) ToolTip.SetIsOpen(control, false);
        await Task.Delay(60);
        // xwd captures only this actual application window, never used to simulate blur.
        var xid = window.TryGetPlatformHandle()!.Handle;
        using var process = Process.Start(new ProcessStartInfo("xwd")
        {
            ArgumentList = { "-silent", "-id", $"0x{xid.ToInt64():x}", "-out", Path.Combine(_output, name + ".xwd") }, UseShellExecute = false
        });
        if (process == null) throw new InvalidOperationException("xwd is required for native screenshots.");
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException("xwd capture failed.");
        var bytes = File.ReadAllBytes(Path.Combine(_output, name + ".xwd"));
        int Field(int index) => (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(index * 4, 4));
        if (Field(1) != 7 || Field(2) != 2 || Field(7) != 0 || (Field(11) != 32 && Field(11) != 24) || Field(14) != 0xff0000)
            throw new NotSupportedException("Expected X11 little-endian 24/32-bit truecolor XWD.");
        int offset = Field(0) + Field(19) * 12;
        using var bitmap = new WriteableBitmap(new PixelSize(Field(4), Field(5)), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var locked = bitmap.Lock())
            for (int row = 0; row < Field(5); row++)
            {
                if (Field(11) == 32)
                    Marshal.Copy(bytes, offset + row * Field(12), locked.Address + row * locked.RowBytes, Field(4) * 4);
                else
                {
                    var rgba = new byte[Field(4) * 4];
                    for (int x = 0; x < Field(4); x++)
                    {
                        Array.Copy(bytes, offset + row * Field(12) + x * 3, rgba, x * 4, 3);
                        rgba[x * 4 + 3] = 255;
                    }
                    Marshal.Copy(rgba, 0, locked.Address + row * locked.RowBytes, rgba.Length);
                }
            }
        bitmap.Save(Path.Combine(_output, name + ".png"));
    }

    private static void VerifyIcons()
    {
        var root = Path.Combine(Path.GetTempPath(), "pankin-icon-test-" + Guid.NewGuid().ToString("N"));
        void Write(string path, string text) { path = Path.Combine(root, path); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text); }
        const string svg = "<svg xmlns='http://www.w3.org/2000/svg' width='48' height='48'><rect width='48' height='48' rx='8' fill='#5588aa'/></svg>";
        try
        {
            Write("user/Test/index.theme", "[Icon Theme]\nDirectories=48x48/apps,96x96/apps\nInherits=Parent\n[48x48/apps]\nSize=48\nType=Fixed\n[96x96/apps]\nSize=96\nType=Fixed\n");
            Write("system/Parent/index.theme", "[Icon Theme]\nDirectories=scalable/apps\nInherits=Test\n[scalable/apps]\nSize=48\nType=Scalable\nMinSize=16\nMaxSize=256\n");
            Write("system/Parent/scalable/apps/inherited.svg", svg);
            Write("user/Test/96x96/apps/legacy.xpm", "/* XPM */\nstatic char *icon[] = {\n\"2 2 1 1\",\n\"x c #5588aa\",\n\"xx\",\n\"xx\"};");
            Write("user/Test/48x48/apps/size.svg", svg);
            Write("user/Test/96x96/apps/size.svg", svg);
            Write("system/Test/96x96/apps/size.svg", svg);
            Write("system/hicolor/48x48/apps/fallback.svg", svg);
            Write("user/Test/96x96/apps/corrupt.svg", "not an svg");
            var resolver = new LinuxIconResolver(new[] { Path.Combine(root, "user"), Path.Combine(root, "system") }, "Test");
            Check(resolver.Resolve("inherited")!.EndsWith("Parent/scalable/apps/inherited.svg"), "theme inheritance and scalable directory");
            Check(resolver.Resolve("size", 96)!.EndsWith("user/Test/96x96/apps/size.svg"), "nearest size and user root priority");
            Check(resolver.Resolve("fallback") != null, "hicolor fallback without index");
            Check(resolver.Resolve("missing") == null, "inheritance cycles terminate on missing icon");
            Check(resolver.LoadPng("inherited") is { Length: > 16 }, "real SVG decoded to PNG through librsvg");
            Check(resolver.LoadPng("corrupt") == null, "malformed icon fails softly");
            Check(resolver.LoadPng("legacy.xpm") is { Length: > 16 }, "legacy XPM icon decoded");
            using (var decoded = new Bitmap(new MemoryStream(resolver.LoadPng("inherited", 96)!)))
                Check(decoded.PixelSize.Width == 96 && decoded.PixelSize.Height == 96, "SVG rasterized at high-DPI target size");
            var png = resolver.LoadPng("size")!;
            File.WriteAllBytes(Path.Combine(root, "absolute.png"), png);
            Check(resolver.LoadPng(Path.Combine(root, "absolute.png")) != null, "absolute PNG path decoded");
            Check(resolver.Resolve("../size") == null, "relative traversal rejected");
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class RecordingSearch : ILauncherSearch
    {
        public int Executions { get; private set; }
        public Task<LauncherResult> SearchAsync(string query, CancellationToken cancellationToken = default) => Task.FromResult(new LauncherResult([], false));
        public Task<LauncherResult> SearchAsync(string query, Action<LauncherResult> partial, CancellationToken cancellationToken = default) => SearchAsync(query, cancellationToken);
        public Task ExecuteAsync(LauncherItem item, CancellationToken cancellationToken = default) { Executions++; return Task.CompletedTask; }
        public Task ExecuteElevatedAsync(LauncherItem item, CancellationToken cancellationToken = default) => ExecuteAsync(item, cancellationToken);
    }
}

internal sealed class VerificationApp : App
{
    public override void OnFrameworkInitializationCompleted() { /* Do not start global services. */ }
}
