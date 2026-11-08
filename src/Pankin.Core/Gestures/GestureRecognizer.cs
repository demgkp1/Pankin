using Pankin.Core.Abstractions;

namespace Pankin.Core.Gestures;

/// <summary>
/// 鼠标手势轨迹纯几何方向序列识别器
/// </summary>
public sealed class GestureRecognizer
{
    private readonly List<GestureDirection> _directions = [];
    private readonly double _threshold;
    private readonly bool _allowDiagonal;
    private double _anchorX;
    private double _anchorY;
    private double _firstDx;
    private double _firstDy;

    public IReadOnlyList<GestureDirection> Directions => _directions;

    public string DirectionSequence => string.Join("", _directions.Select(d => d.ToShortCode()));

    public GestureRecognizer(
        double startX,
        double startY,
        double threshold = 32.0,
        bool allowDiagonal = true)
    {
        _anchorX = startX;
        _anchorY = startY;
        _threshold = Math.Max(10.0, threshold);
        _allowDiagonal = allowDiagonal;
    }

    public GestureRecognizer(GesturePoint start, double threshold = 32.0, bool allowDiagonal = true)
        : this(start.X, start.Y, threshold, allowDiagonal)
    {
    }

    /// <summary>
    /// 添加轨迹采样点并更新方向序列
    /// </summary>
    /// <returns>若识别出新方向则返回 true，若未跨过阈值或与上一方向相同返回 false</returns>
    public bool Add(double x, double y)
    {
        double dx = x - _anchorX;
        double dy = y - _anchorY;

        // 抖动抑制：位移未达到阈值
        if (dx * dx + dy * dy < _threshold * _threshold)
            return false;

        var direction = _allowDiagonal ? Classify8(dx, dy) : Classify4(dx, dy);

        if (_directions.Count == 0)
        {
            _firstDx = dx;
            _firstDy = dy;
        }

        // 更新锚点
        _anchorX = x;
        _anchorY = y;

        // 相同方向去重（连续同向运动不重复记录）
        if (_directions.Count > 0 && _directions[^1] == direction)
            return false;

        _directions.Add(direction);
        return true;
    }

    public bool Add(GesturePoint point) => Add(point.X, point.Y);

    public void Reset(double newStartX, double newStartY)
    {
        _anchorX = newStartX;
        _anchorY = newStartY;
        _directions.Clear();
        _firstDx = 0;
        _firstDy = 0;
    }

    /// <summary>
    /// 从完整轨迹列表中静态识别方向序列
    /// </summary>
    public static string Recognize(
        IReadOnlyList<GesturePoint> trajectory,
        double minimumDistance = 28.0,
        bool allowDiagonal = true)
        => RecognizeTrajectory(trajectory, minimumDistance, allowDiagonal);

    /// <summary>
    /// 从完整轨迹列表中静态识别方向序列
    /// </summary>
    public static string RecognizeTrajectory(
        IReadOnlyList<GesturePoint> trajectory,
        double threshold = 32.0,
        bool allowDiagonal = true)
    {
        if (trajectory == null || trajectory.Count < 2)
            return string.Empty;

        var recognizer = new GestureRecognizer(trajectory[0], threshold, allowDiagonal);
        for (int i = 1; i < trajectory.Count; i++)
        {
            recognizer.Add(trajectory[i]);
        }

        return recognizer.DirectionSequence;
    }

    /// <summary>
    /// 4 方向（上、下、左、右）分类
    /// </summary>
    public static GestureDirection Classify4(double dx, double dy)
    {
        return Math.Abs(dx) > Math.Abs(dy)
            ? (dx > 0 ? GestureDirection.Right : GestureDirection.Left)
            : (dy > 0 ? GestureDirection.Down : GestureDirection.Up);
    }

    /// <summary>
    /// 8 方向（含对角线）几何分类
    /// </summary>
    public static GestureDirection Classify8(double dx, double dy)
    {
        // 顺时针角度：0° 为上，90° 为右，180° 为下，270° 为左
        double angle = (Math.Atan2(dx, -dy) * 180.0 / Math.PI + 360.0) % 360.0;
        int quadrant = (int)(angle / 90.0);
        double local = angle % 90.0;

        // 20° 盲区内归为正向，20°~70° 归为对角斜向，70°~90° 归为相邻正向
        int offset = local <= 22.5 ? 0 : local <= 67.5 ? 1 : 2;
        return (GestureDirection)((quadrant * 2 + offset) % 8);
    }
}
