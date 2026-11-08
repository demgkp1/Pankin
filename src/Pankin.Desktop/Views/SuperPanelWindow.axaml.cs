using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Pankin.Core.Selection;
using Pankin.Desktop.ViewModels;

namespace Pankin.Desktop.Views;

public partial class SuperPanelWindow : Window
{
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

        // 失焦/点击外部自动隐匿面板 (Hide-on-click-away)
        Deactivated += (_, _) =>
        {
            if (IsVisible)
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

            // X 轴翻转与计算
            int targetX;
            if (mouseX + offset + winWidth <= work.X + work.Width - margin)
            {
                targetX = (int)mouseX + offset;
            }
            else if (mouseX - offset - winWidth >= work.X + margin)
            {
                targetX = (int)mouseX - offset - winWidth;
            }
            else
            {
                targetX = Math.Clamp((int)mouseX + offset, work.X + margin, Math.Max(work.X + margin, work.X + work.Width - winWidth - margin));
            }

            // Y 轴翻转与计算
            int targetY;
            if (mouseY + offset + winHeight <= work.Y + work.Height - margin)
            {
                targetY = (int)mouseY + offset;
            }
            else if (mouseY - offset - winHeight >= work.Y + margin)
            {
                targetY = (int)mouseY - offset - winHeight;
            }
            else
            {
                targetY = Math.Clamp((int)mouseY + offset, work.Y + margin, Math.Max(work.Y + margin, work.Y + work.Height - winHeight - margin));
            }

            Position = new PixelPoint(targetX, targetY);
        }
        else
        {
            Position = new PixelPoint((int)mouseX + 12, (int)mouseY + 12);
        }
    }
}
