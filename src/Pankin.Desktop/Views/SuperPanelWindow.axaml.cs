using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Pankin.Core.Selection;
using Pankin.Desktop.ViewModels;

using Pankin.Desktop.Services;

namespace Pankin.Desktop.Views;

public partial class SuperPanelWindow : Window
{
    private long _lastActivatedMs;

    public SuperPanelWindow()
    {
        InitializeComponent();

        TopDragArea.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                BeginMoveDrag(e);
            }
        };

        CloseBtn.Click += (_, _) => Hide();

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Hide();
                e.Handled = true;
            }
        };

        Activated += (_, _) => _lastActivatedMs = Environment.TickCount64;
        Opened += (_, _) => _lastActivatedMs = Environment.TickCount64;

        // 失焦/点击外部自动隐匿面板 (Hide-on-click-away)
        Deactivated += (_, _) =>
        {
            if (IsVisible &&
                Environment.TickCount64 - _lastActivatedMs > 180 &&
                DesktopAppContext.Instance.SettingsStore.Current.General.HideOnDeactivate)
            {
                Hide();
            }
        };
    }

    public void ShowNearPointer(SelectionContext context, string mode = "translate")
    {
        if (DataContext is SuperPanelViewModel vm)
        {
            vm.SetSelection(context, mode);
        }

        PositionWindowNear(context.PointerX, context.PointerY);
        Show();
        Activate();
    }

    public void ShowNearPointer(double mouseX, double mouseY, string? selectedText = null, string mode = "translate")
    {
        var ctx = new SelectionContext(
            Text: selectedText,
            Files: null,
            SourceAppName: null,
            WindowTitle: null,
            PointerX: mouseX,
            PointerY: mouseY,
            Timestamp: DateTime.UtcNow,
            Failure: string.IsNullOrWhiteSpace(selectedText) ? SelectionFailure.Empty : SelectionFailure.None);

        ShowNearPointer(ctx, mode);
    }

    /// <summary>
    /// 边缘感知智能定位（复刻 Pane-Win GlassPanel）：
    /// 1. 优先出现在鼠标右下方 (+12px 偏移)；
    /// 2. 若超出当前屏幕工作区右边界，自动翻转到光标左侧；
    /// 3. 若超出当前屏幕工作区下边界，自动翻转到光标上方；
    /// 4. 最终以 8px 边距安全夹紧在 WorkingArea 内，绝不出界或被任务栏遮挡。
    /// </summary>
    private void PositionWindowNear(double mouseX, double mouseY)
    {
        var screens = Screens;
        var screen = screens.ScreenFromPoint(new PixelPoint((int)mouseX, (int)mouseY)) ?? screens.Primary;

        int winWidth = (int)Width;
        int winHeight = (int)Height;

        if (screen != null)
        {
            var work = screen.WorkingArea;
            const int margin = 8;
            const int offset = 12;
            const int shadowPadding = 12;

            // X 轴翻转与计算（考虑 12px 投影留白，确保可见面板精准贴合光标）
            int visibleWidth = winWidth - shadowPadding * 2;
            int targetX;
            if (mouseX + offset + visibleWidth <= work.X + work.Width - margin)
            {
                targetX = (int)mouseX + offset - shadowPadding;
            }
            else if (mouseX - offset - visibleWidth >= work.X + margin)
            {
                targetX = (int)mouseX - offset - winWidth + shadowPadding;
            }
            else
            {
                targetX = Math.Clamp((int)mouseX + offset - shadowPadding, work.X + margin - shadowPadding, Math.Max(work.X + margin - shadowPadding, work.X + work.Width - winWidth - margin + shadowPadding));
            }

            // Y 轴翻转与计算
            int visibleHeight = winHeight - shadowPadding * 2;
            int targetY;
            if (mouseY + offset + visibleHeight <= work.Y + work.Height - margin)
            {
                targetY = (int)mouseY + offset - shadowPadding;
            }
            else if (mouseY - offset - visibleHeight >= work.Y + margin)
            {
                targetY = (int)mouseY - offset - winHeight + shadowPadding;
            }
            else
            {
                targetY = Math.Clamp((int)mouseY + offset - shadowPadding, work.Y + margin - shadowPadding, Math.Max(work.Y + margin - shadowPadding, work.Y + work.Height - winHeight - margin + shadowPadding));
            }

            Position = new PixelPoint(targetX, targetY);
        }
        else
        {
            Position = new PixelPoint((int)mouseX, (int)mouseY);
        }
    }
}
