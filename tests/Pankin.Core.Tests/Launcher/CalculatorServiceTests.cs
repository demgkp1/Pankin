using Pankin.Core.Launcher;
using Xunit;

namespace Pankin.Core.Tests.Launcher;

public class CalculatorServiceTests
{
    [Theory]
    [InlineData("1 + 2", 3)]
    [InlineData("10 - 4", 6)]
    [InlineData("3 * 5", 15)]
    [InlineData("20 / 4", 5)]
    [InlineData("6 × 7 ÷ 2", 21)] // 全角乘除号支持
    public void TryEvaluate_BasicArithmetic_ShouldCalculateCorrectly(string expr, double expected)
    {
        bool success = CalculatorService.TryEvaluate(expr, out var formatted, out var value);
        Assert.True(success);
        Assert.Equal(expected, value, 6);
    }

    [Theory]
    [InlineData("1 + 2 * 3", 7)]
    [InlineData("2 * 3 + 4 * 5", 26)]
    [InlineData("10 - 2 * 3", 4)]
    [InlineData("2 ^ 3 ^ 2", 512)] // 幂运算右结合: 2 ^ (3 ^ 2) = 2 ^ 9 = 512
    [InlineData("4 * 2 ^ 3", 32)]  // 幂运算优先级高于乘法
    public void TryEvaluate_Precedence_ShouldHonorOperatorPrecedence(string expr, double expected)
    {
        bool success = CalculatorService.TryEvaluate(expr, out _, out var value);
        Assert.True(success);
        Assert.Equal(expected, value, 6);
    }

    [Theory]
    [InlineData("(1 + 2) * 3", 9)]
    [InlineData("（1 + 2） * 3", 9)] // 全角括号
    [InlineData("((2 + 3) * (4 - 1)) / 5", 3)]
    [InlineData("(10 + 20) / (2 * 3)", 5)]
    public void TryEvaluate_Parentheses_ShouldRespectGrouping(string expr, double expected)
    {
        bool success = CalculatorService.TryEvaluate(expr, out _, out var value);
        Assert.True(success);
        Assert.Equal(expected, value, 6);
    }

    [Theory]
    [InlineData("0.5 + 0.25", 0.75)]
    [InlineData(".5 * 4", 2.0)]
    [InlineData("1.5 * 2.5", 3.75)]
    [InlineData("10.5 / 2", 5.25)]
    public void TryEvaluate_Decimals_ShouldHandleFloatingPoint(string expr, double expected)
    {
        bool success = CalculatorService.TryEvaluate(expr, out _, out var value);
        Assert.True(success);
        Assert.Equal(expected, value, 6);
    }

    [Theory]
    [InlineData("-5 + 10", 5)]
    [InlineData("+5 * -2", -10)]
    [InlineData("-(-5)", 5)]
    [InlineData("10 % 3", 1)]
    [InlineData("50% * 200", 100)]
    public void TryEvaluate_UnaryAndModulo_ShouldComputeExpected(string expr, double expected)
    {
        bool success = CalculatorService.TryEvaluate(expr, out _, out var value);
        Assert.True(success);
        Assert.Equal(expected, value, 6);
    }

    [Theory]
    [InlineData("sqrt(16)", 4)]
    [InlineData("abs(-42)", 42)]
    [InlineData("sin(0)", 0)]
    [InlineData("cos(0)", 1)]
    [InlineData("tan(0)", 0)]
    [InlineData("log(100)", 2)]
    [InlineData("ln(e)", 1)]
    [InlineData("pi * 2", Math.PI * 2)]
    [InlineData("π * 2", Math.PI * 2)]
    public void TryEvaluate_FunctionsAndConstants_ShouldComputeAccurately(string expr, double expected)
    {
        bool success = CalculatorService.TryEvaluate(expr, out _, out var value);
        Assert.True(success);
        Assert.Equal(expected, value, 5);
    }

    [Theory]
    [InlineData("1 / 0")]
    [InlineData("5 % 0")]
    [InlineData("10 / (2 - 2)")]
    public void TryEvaluate_DivisionByZero_ShouldReturnFalse(string expr)
    {
        bool success = CalculatorService.TryEvaluate(expr, out var formatted, out _);
        Assert.False(success);
        Assert.Equal(string.Empty, formatted);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("42")]            // 纯数字不应触发计算器，避免误触发
    [InlineData("3.14")]          // 纯浮点数无操作符
    [InlineData("hello world")]   // 非法文本
    [InlineData("(1 + 2")]        // 缺少右括号
    [InlineData("1 + 2)")]        // 多余右括号
    [InlineData("1 + * 2")]       // 操作符错位
    [InlineData("sqrt(-1)")]      // 负数开方
    [InlineData("log(0)")]        // 非正数对数
    [InlineData("log(-10)")]      // 负数对数
    [InlineData("ln(0)")]         // ln 0
    [InlineData("unknown(5)")]    // 未知函数
    public void TryEvaluate_InvalidInputs_ShouldReturnFalse(string? expr)
    {
        bool success = CalculatorService.TryEvaluate(expr!, out var formatted, out _);
        Assert.False(success);
        Assert.Equal(string.Empty, formatted);
    }

    [Fact]
    public void TryEvaluate_ExceedingMaxLength_ShouldReturnFalse()
    {
        string longExpr = "1+" + new string('1', 520);
        bool success = CalculatorService.TryEvaluate(longExpr, out _, out _);
        Assert.False(success);
    }

    [Fact]
    public void Evaluate_ValidExpression_ReturnsFormattedString()
    {
        string? result = CalculatorService.Evaluate("2 + 3 * 4");
        Assert.Equal("14", result);
    }

    [Fact]
    public void Evaluate_InvalidExpression_ReturnsNull()
    {
        string? result = CalculatorService.Evaluate("1 / 0");
        Assert.Null(result);
    }
}
