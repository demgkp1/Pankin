using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Threading;
using Pankin.Core.Abstractions;

namespace Pankin.Desktop.Views;

public partial class GestureOverlayWindow : Window
{
    public GestureOverlayWindow()
    {
        InitializeComponent();
    }

    public void UpdateProgress(double mouseX, double mouseY, string direction, string actionText, IReadOnlyList<GesturePoint> trajectory)
    {
        Dispatcher.UIThread.Post(() =>
        {
            ArrowText.Text = GetArrowGlyph(direction);
            CodeLabel.Text = string.IsNullOrEmpty(direction) ? "..." : direction;
            ActionLabel.Text = string.IsNullOrWhiteSpace(actionText) ? "未绑定动作" : actionText;

            // 智能避开屏幕边缘（扣除 12px 阴影留白，让可见胶囊精准位于鼠标旁）
            int posX = (int)mouseX + 12;
            int posY = (int)mouseY + 12;
            Position = new PixelPoint(Math.Max(0, posX), Math.Max(0, posY));

            // 绘制轨迹线条
            if (trajectory.Count >= 2)
            {
                double minX = double.MaxValue, maxX = double.MinValue;
                double minY = double.MaxValue, maxY = double.MinValue;

                foreach (var pt in trajectory)
                {
                    if (pt.X < minX) minX = pt.X;
                    if (pt.X > maxX) maxX = pt.X;
                    if (pt.Y < minY) minY = pt.Y;
                    if (pt.Y > maxY) maxY = pt.Y;
                }

                double spanX = Math.Max(1.0, maxX - minX);
                double spanY = Math.Max(1.0, maxY - minY);

                double canvasW = TrailCanvas.Bounds.Width > 10 ? TrailCanvas.Bounds.Width : 260.0;
                double canvasH = TrailCanvas.Bounds.Height > 10 ? TrailCanvas.Bounds.Height : 34.0;

                double scale = Math.Min((canvasW - 20) / spanX, (canvasH - 8) / spanY);
                double offsetX = (canvasW - spanX * scale) / 2.0;
                double offsetY = (canvasH - spanY * scale) / 2.0;

                var points = new Points();
                foreach (var pt in trajectory)
                {
                    double x = offsetX + (pt.X - minX) * scale;
                    double y = offsetY + (pt.Y - minY) * scale;
                    points.Add(new Point(x, y));
                }

                TrailPolyline.Points = points;
            }
            else
            {
                TrailPolyline.Points?.Clear();
            }

            if (!IsVisible)
            {
                Show();
                Topmost = false;
                Topmost = true;
            }
        });
    }

    public void HideOverlay()
    {
        Dispatcher.UIThread.Post(() =>
        {
            Hide();
            TrailPolyline.Points?.Clear();
        });
    }

    private static string GetArrowGlyph(string direction) => direction switch
    {
        "L" => "←",
        "R" => "→",
        "U" => "↑",
        "D" => "↓",
        "DR" => "↘",
        "DL" => "↙",
        "UL" => "↖",
        "UR" => "↗",
        "Hold" => "✋",
        "UD" => "↕",
        "DU" => "↕",
        "LR" => "↔",
        "RL" => "↔",
        _ => "→"
    };
}
