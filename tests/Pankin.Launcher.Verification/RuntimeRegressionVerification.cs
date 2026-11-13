using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Pankin.Core.Settings;
using Pankin.Desktop;
using Pankin.Desktop.Services;
using Pankin.Desktop.ViewModels;
using SharpHook;
using SharpHook.Native;

internal static class RuntimeRegressionVerification
{
    public static int Run()
    {
        // Run only on an isolated X server. Never type into or overwrite the user's real clipboard.
        if (Environment.GetEnvironmentVariable("PANKIN_ISOLATED_DISPLAY") != "1")
            throw new InvalidOperationException("Runtime regressions require PANKIN_ISOLATED_DISPLAY=1 and an isolated X server");
        var isolation = Path.Combine(Path.GetTempPath(), "pankin-runtime-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(isolation, "config"));
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(isolation, "data"));
        Environment.SetEnvironmentVariable("HOME", isolation);
        var appDirectory = Path.Combine(isolation, "app");
        Directory.CreateDirectory(appDirectory);
        var image = Path.Combine(appDirectory, "Regression Portable-1.0.AppImage");
        File.WriteAllText(image, "#!/bin/sh\nexit 0\n");
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(image, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var store = new SettingsStore();
        store.Update(s => s with
        {
            General = s.General with { SetupCompleted = true, StartAtLogin = false, HideOnDeactivate = true, RememberWindowPosition = false },
            Modules = s.Modules with { SuperPanel = false },
            Launcher = s.Launcher with { WebSuggestions = false },
            Update = s.Update with { CheckOnStartup = false }
        });
        // A bare Xvfb server has no desktop clipboard atoms. Seed the standard
        // atoms a normal desktop creates before Avalonia 11.1.3 probes existing atoms.
        var display = XOpenDisplay(null);
        if (display == IntPtr.Zero) throw new InvalidOperationException("Cannot open isolated X display");
        foreach (var atom in new[] { "CLIPBOARD", "CLIPBOARD_MANAGER", "SAVE_TARGETS", "TARGETS", "MULTIPLE", "UTF8_STRING", "UTF16_STRING", "TEXT", "ATOM_PAIR", "INCR" })
            XInternAtom(display, atom, false);
        try
        {
            return AppBuilder.Configure<RegressionApp>().UsePlatformDetect().WithInterFont()
                .With(new X11PlatformOptions { WmClass = "pankin-regression" })
                .StartWithClassicDesktopLifetime(["--autostart"]);
        }
        finally { XCloseDisplay(display); Directory.Delete(isolation, recursive: true); }
    }

    [DllImport("libX11.so.6")]
    private static extern IntPtr XOpenDisplay(string? display);
    [DllImport("libX11.so.6")]
    private static extern IntPtr XInternAtom(IntPtr display, string name, bool onlyIfExists);
    [DllImport("libX11.so.6")]
    private static extern int XCloseDisplay(IntPtr display);

    private sealed class RegressionApp : App
    {
        private int _checks;
        private void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("FAIL: " + message);
            Console.WriteLine("PASS: " + message);
            _checks++;
        }

        private T Window<T>(string field) where T : Window =>
            (T)typeof(App).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(this)!;

        public override void OnFrameworkInitializationCompleted()
        {
            base.OnFrameworkInitializationCompleted();
            Dispatcher.UIThread.Post(async () =>
            {
                var lifetime = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;
                var code = 0;
                try { await Verify(); Console.WriteLine($"PASS: {_checks} runtime regression checks"); }
                catch (Exception ex) { Console.Error.WriteLine(ex); code = 1; }
                finally
                {
                    DesktopAppContext.Instance.Dispose();
                    lifetime.Shutdown(code);
                }
            });
        }

        private async Task Verify()
        {
            var main = Window<Window>("_mainWindow");
            var clipboardWindow = Window<Window>("_clipboardWindow");
            var context = DesktopAppContext.Instance;
            await Task.Delay(700);
            Check(!main.IsVisible && !clipboardWindow.IsVisible, "--autostart starts the actual app without showing either panel");
            Check(((IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!).MainWindow == null, "desktop lifetime cannot automatically show an autostart panel");

            // This is a separate native clipboard writer, not IClipboardService.SetTextAsync:
            // the production monitor must see it as another application's copy.
            var sender = new Window { Width = 100, Height = 100, ShowInTaskbar = false };
            sender.Show();
            await Task.Delay(200);
            await sender.Clipboard!.SetTextAsync("Pankin native clipboard regression");
            await WaitFor(() => context.ClipboardHistory.Storage.QueryEntries().Any(e => e.Text == "Pankin native clipboard regression"));
            Check(await context.ClipboardService.GetTextAsync() == "Pankin native clipboard regression", "native clipboard read works with no xclip or wl-paste installed");
            Check(context.ClipboardHistory.Storage.QueryEntries().Any(e => e.Text == "Pankin native clipboard regression"), "native clipboard copy reaches SQLite history");

            var pointerPresses = 0;
            context.InputHook.GlobalPointerPressed += (_, _) => Interlocked.Increment(ref pointerPresses);
            ToggleMainWindow(bypassThrottle: true);
            await Task.Delay(600);
            Check(main.IsVisible, "launcher opens from tray-only startup");
            Console.WriteLine($"RenderScaling={main.RenderScaling}");
            var bottomRight = main.PointToScreen(new Point(main.Bounds.Width, main.Bounds.Height));
            Check(PanelWindowBehavior.ContainsScreenPoint(main, bottomRight.X - 1, bottomRight.Y - 1), "physical bottom-right pixel is inside the logical panel at current DPI");
            Check(!PanelWindowBehavior.ContainsScreenPoint(main, bottomRight.X + 1, bottomRight.Y), "pixel beyond physical bounds is outside");
            var mainSearch = main.FindControl<TextBox>("SearchInput")!;
            await Click(mainSearch.PointToScreen(new Point(mainSearch.Bounds.Width - 12, mainSearch.Bounds.Height / 2)));
            Check(main.IsVisible, "real click inside launcher search does not hide it");
            mainSearch.Text = "Regression Portable";
            await WaitFor(() => ((MainViewModel)main.DataContext!).SearchResults.Any(i => i.CoreItem.Title.Contains("Regression Portable", StringComparison.OrdinalIgnoreCase)));
            Check(((MainViewModel)main.DataContext!).SearchResults.Any(i => i.CoreItem.Title.Contains("Regression Portable", StringComparison.OrdinalIgnoreCase)), "launcher discovers and searches a portable executable in the default ~/app directory");

            ToggleClipboardWindow(bypassThrottle: true);
            await Task.Delay(600);
            Check(clipboardWindow.IsVisible && !main.IsVisible, "clipboard panel replaces launcher");
            Check(((ClipboardViewModel)clipboardWindow.DataContext!).Items.Any(i => i.FullText == "Pankin native clipboard regression"), "clipboard panel displays captured native text");
            var clipboardSearch = clipboardWindow.FindControl<TextBox>("SearchInput")!;
            await Click(clipboardSearch.PointToScreen(new Point(clipboardSearch.Bounds.Width - 12, clipboardSearch.Bounds.Height / 2)));
            Check(clipboardWindow.IsVisible, "real click inside clipboard search does not hide it");
            await Click(new PixelPoint(10, 10));
            await WaitFor(() => !clipboardWindow.IsVisible);
            Check(!clipboardWindow.IsVisible, "real click outside clipboard hides the panel");
            Check(Volatile.Read(ref pointerPresses) >= 3, "test mouse clicks were observed by the real global hook");
            sender.Close();
        }

        private static async Task Click(PixelPoint point)
        {
            var simulator = new EventSimulator();
            simulator.SimulateMouseMovement((short)point.X, (short)point.Y);
            await Task.Delay(100);
            simulator.SimulateMousePress(MouseButton.Button1);
            simulator.SimulateMouseRelease(MouseButton.Button1);
            await Task.Delay(350);
        }

        private static async Task WaitFor(Func<bool> condition)
        {
            var timer = Stopwatch.StartNew();
            while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(8)) await Task.Delay(50);
            if (!condition()) throw new InvalidOperationException("Timed out waiting for runtime regression condition");
        }
    }
}
