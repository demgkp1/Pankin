using Pankin.Core.Abstractions;
using Pankin.Core.Gestures;
using Xunit;

namespace Pankin.Core.Tests.Gestures;

public class GestureRecognizerTests
{
    [Theory]
    [InlineData(100, 0, GestureDirection.Right)]
    [InlineData(-100, 0, GestureDirection.Left)]
    [InlineData(0, 100, GestureDirection.Down)]
    [InlineData(0, -100, GestureDirection.Up)]
    [InlineData(100, 30, GestureDirection.Right)]  // 水平分量主导
    [InlineData(30, 100, GestureDirection.Down)]   // 垂直分量主导
    [InlineData(-100, -30, GestureDirection.Left)]
    [InlineData(-30, -100, GestureDirection.Up)]
    public void Classify4_ShouldClassifyIntoFourDirections(double dx, double dy, GestureDirection expected)
    {
        var result = GestureRecognizer.Classify4(dx, dy);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(0, -100, GestureDirection.Up)]         // 0°
    [InlineData(100, -100, GestureDirection.UpRight)]  // 45°
    [InlineData(100, 0, GestureDirection.Right)]       // 90°
    [InlineData(100, 100, GestureDirection.DownRight)] // 135°
    [InlineData(0, 100, GestureDirection.Down)]        // 180°
    [InlineData(-100, 100, GestureDirection.DownLeft)] // 225°
    [InlineData(-100, 0, GestureDirection.Left)]       // 270°
    [InlineData(-100, -100, GestureDirection.UpLeft)]  // 315°
    public void Classify8_ShouldClassifyIntoEightDirections(double dx, double dy, GestureDirection expected)
    {
        var result = GestureRecognizer.Classify8(dx, dy);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(0, 0, 50, 0, "R")]
    [InlineData(0, 0, -50, 0, "L")]
    [InlineData(0, 0, 0, 50, "D")]
    [InlineData(0, 0, 0, -50, "U")]
    public void Recognize_StraightLines_FourDirections(double x1, double y1, double x2, double y2, string expected)
    {
        var trajectory = new List<GesturePoint>
        {
            new(x1, y1, 0),
            new(x2, y2, 10)
        };

        var result = GestureRecognizer.Recognize(trajectory, minimumDistance: 28.0, allowDiagonal: false);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(0, 0, 50, -50, "UR")]
    [InlineData(0, 0, 50, 50, "DR")]
    [InlineData(0, 0, -50, 50, "DL")]
    [InlineData(0, 0, -50, -50, "UL")]
    public void Recognize_StraightLines_EightDirectionsDiagonal(double x1, double y1, double x2, double y2, string expected)
    {
        var trajectory = new List<GesturePoint>
        {
            new(x1, y1, 0),
            new(x2, y2, 10)
        };

        var result = GestureRecognizer.Recognize(trajectory, minimumDistance: 28.0, allowDiagonal: true);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Recognize_MultiSegment_DownThenRight()
    {
        // 经典的向下拉后向右拉（L手势）
        var trajectory = new List<GesturePoint>
        {
            new(0, 0, 0),
            new(0, 20, 5),   // 抖动/中间点
            new(0, 50, 10),  // 触发 Down
            new(20, 50, 15),
            new(60, 50, 20)  // 触发 Right
        };

        var result = GestureRecognizer.Recognize(trajectory, minimumDistance: 28.0, allowDiagonal: false);
        Assert.Equal("DR", result);
    }

    [Fact]
    public void Recognize_MultiSegment_ThreeStepZigzag()
    {
        // 右 -> 下 -> 左 (开箱手势)
        var trajectory = new List<GesturePoint>
        {
            new(0, 0, 0),
            new(50, 0, 10),
            new(50, 50, 20),
            new(0, 50, 30)
        };

        var result = GestureRecognizer.Recognize(trajectory, minimumDistance: 28.0, allowDiagonal: false);
        Assert.Equal("RDL", result);
    }

    [Fact]
    public void GestureRecognizer_JitterSuppression_ConsecutiveDuplicateIgnored()
    {
        var recognizer = new GestureRecognizer(0, 0, threshold: 30.0, allowDiagonal: false);

        // 第一次微小位移，未达阈值
        bool added1 = recognizer.Add(10, 0);
        Assert.False(added1);
        Assert.Empty(recognizer.Directions);

        // 达到阈值，触发 Right
        bool added2 = recognizer.Add(40, 0);
        Assert.True(added2);
        Assert.Equal("R", recognizer.DirectionSequence);

        // 继续往右移动，由于方向相同，去重不增加序列长度
        bool added3 = recognizer.Add(80, 0);
        Assert.False(added3);
        Assert.Equal("R", recognizer.DirectionSequence);
    }

    [Fact]
    public void GestureRecognizer_Reset_ClearsDirectionsAndUpdatesAnchor()
    {
        var recognizer = new GestureRecognizer(0, 0, threshold: 30.0);
        recognizer.Add(50, 0);
        Assert.Equal("R", recognizer.DirectionSequence);

        recognizer.Reset(100, 100);
        Assert.Empty(recognizer.Directions);
        Assert.Equal(string.Empty, recognizer.DirectionSequence);

        // 新起点往上移动
        recognizer.Add(100, 50);
        Assert.Equal("U", recognizer.DirectionSequence);
    }

    [Fact]
    public void Recognize_EmptyOrNullTrajectory_ReturnsEmptyString()
    {
        Assert.Equal(string.Empty, GestureRecognizer.Recognize(null!));
        Assert.Equal(string.Empty, GestureRecognizer.Recognize(new List<GesturePoint>()));
        Assert.Equal(string.Empty, GestureRecognizer.Recognize(new List<GesturePoint> { new(0, 0, 0) }));
    }

    [Theory]
    [InlineData(GestureDirection.Up, "U")]
    [InlineData(GestureDirection.Down, "D")]
    [InlineData(GestureDirection.Left, "L")]
    [InlineData(GestureDirection.Right, "R")]
    [InlineData(GestureDirection.UpLeft, "UL")]
    [InlineData(GestureDirection.UpRight, "UR")]
    [InlineData(GestureDirection.DownLeft, "DL")]
    [InlineData(GestureDirection.DownRight, "DR")]
    public void DirectionExtensions_ShortCodeRoundtrip(GestureDirection direction, string code)
    {
        Assert.Equal(code, direction.ToShortCode());
        var parsed = GestureDirectionExtensions.FromShortCode(code);
        Assert.Equal(direction, parsed);
    }

    [Theory]
    [InlineData("LU", GestureDirection.UpLeft)]
    [InlineData("RU", GestureDirection.UpRight)]
    [InlineData("LD", GestureDirection.DownLeft)]
    [InlineData("RD", GestureDirection.DownRight)]
    public void DirectionExtensions_FromShortCode_AlternativeAliases(string alias, GestureDirection expected)
    {
        var parsed = GestureDirectionExtensions.FromShortCode(alias);
        Assert.Equal(expected, parsed);
    }

    [Fact]
    public void DirectionExtensions_FromShortCode_Invalid_ReturnsNull()
    {
        Assert.Null(GestureDirectionExtensions.FromShortCode("INVALID"));
        Assert.Null(GestureDirectionExtensions.FromShortCode(""));
    }
}
