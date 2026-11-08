using System.Globalization;

namespace Pankin.Core.Launcher;

/// <summary>
/// 纯数学算式词法/语法解析与求值服务
/// </summary>
public static class CalculatorService
{
    private const int MaxExpressionLength = 512;

    /// <summary>
    /// 尝试计算数学表达式
    /// </summary>
    /// <param name="expression">数学表达式字符串</param>
    /// <param name="formattedResult">格式化计算结果字符串</param>
    /// <param name="numericValue">计算结果双精度浮点数值</param>
    /// <returns>若计算成功返回 true，否则返回 false</returns>
    public static bool TryEvaluate(string expression, out string formattedResult, out double numericValue)
    {
        formattedResult = string.Empty;
        numericValue = 0;

        if (string.IsNullOrWhiteSpace(expression) || expression.Length > MaxExpressionLength)
            return false;

        // 统一字符规范
        var sanitized = expression
            .Replace('×', '*')
            .Replace('÷', '/')
            .Replace('，', ',')
            .Replace('（', '(')
            .Replace('）', ')');

        // 至少包含一个数字或常量
        bool hasOperand = sanitized.Any(char.IsDigit) ||
                          sanitized.Contains("pi", StringComparison.OrdinalIgnoreCase) ||
                          sanitized.Contains('π') ||
                          sanitized.Contains("sqrt", StringComparison.OrdinalIgnoreCase) ||
                          sanitized.Contains("sin", StringComparison.OrdinalIgnoreCase) ||
                          sanitized.Contains("cos", StringComparison.OrdinalIgnoreCase) ||
                          sanitized.Contains("tan", StringComparison.OrdinalIgnoreCase) ||
                          sanitized.Contains("abs", StringComparison.OrdinalIgnoreCase) ||
                          sanitized.Contains("log", StringComparison.OrdinalIgnoreCase) ||
                          sanitized.Contains("ln", StringComparison.OrdinalIgnoreCase) ||
                          sanitized.Contains('e');

        if (!hasOperand)
            return false;

        // 至少需要一个计算操作符或函数调用，避免纯数字误触发计算器
        bool hasOperation = sanitized.IndexOfAny(['+', '-', '*', '/', '%', '^', '(', ')', 'π']) >= 0 ||
                            sanitized.Contains("sqrt", StringComparison.OrdinalIgnoreCase) ||
                            sanitized.Contains("sin", StringComparison.OrdinalIgnoreCase) ||
                            sanitized.Contains("cos", StringComparison.OrdinalIgnoreCase) ||
                            sanitized.Contains("tan", StringComparison.OrdinalIgnoreCase) ||
                            sanitized.Contains("abs", StringComparison.OrdinalIgnoreCase) ||
                            sanitized.Contains("log", StringComparison.OrdinalIgnoreCase) ||
                            sanitized.Contains("ln", StringComparison.OrdinalIgnoreCase);

        if (!hasOperation)
            return false;

        try
        {
            var parser = new ExpressionParser(sanitized);
            double result = parser.Parse();

            if (double.IsNaN(result) || double.IsInfinity(result))
                return false;

            numericValue = result;
            formattedResult = double.Parse(result.ToString("G12", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)
                                    .ToString(CultureInfo.InvariantCulture);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 计算数学表达式，失败返回 null
    /// </summary>
    public static string? Evaluate(string expression)
    {
        return TryEvaluate(expression, out var formattedResult, out _) ? formattedResult : null;
    }

    private sealed class ExpressionParser
    {
        private readonly string _src;
        private int _pos;

        public ExpressionParser(string src)
        {
            _src = src;
            _pos = 0;
        }

        public double Parse()
        {
            SkipWhitespace();
            double value = ParseExpression();
            SkipWhitespace();
            if (_pos < _src.Length)
                throw new FormatException($"Unexpected character at position {_pos}: {_src[_pos]}");
            return value;
        }

        private void SkipWhitespace()
        {
            while (_pos < _src.Length && char.IsWhiteSpace(_src[_pos]))
                _pos++;
        }

        private char Peek() => _pos < _src.Length ? _src[_pos] : '\0';

        private double ParseExpression()
        {
            double left = ParseTerm();

            while (true)
            {
                SkipWhitespace();
                char op = Peek();
                if (op != '+' && op != '-') break;

                _pos++;
                double right = ParseTerm();
                left = op == '+' ? left + right : left - right;
            }

            return left;
        }

        private double ParseTerm()
        {
            double left = ParseFactor();

            while (true)
            {
                SkipWhitespace();
                char op = Peek();

                // 区分二元取模 % 与后缀百分比 %
                if (op == '%')
                {
                    // 检查 % 后面是否跟随操作数（如果是，则是取模）
                    int saved = _pos;
                    _pos++;
                    SkipWhitespace();
                    if (_pos < _src.Length && (char.IsDigit(_src[_pos]) || _src[_pos] == '(' || char.IsLetter(_src[_pos])))
                    {
                        // 是取模运算: a % b
                        double right = ParseFactor();
                        if (right == 0) throw new DivideByZeroException();
                        left %= right;
                        continue;
                    }
                    else
                    {
                        // 不是取模，回退并由后缀百分比处理
                        _pos = saved;
                        break;
                    }
                }

                if (op != '*' && op != '/') break;

                _pos++;
                double rightFactor = ParseFactor();
                if (op == '/' && rightFactor == 0) throw new DivideByZeroException();
                left = op == '*' ? left * rightFactor : left / rightFactor;
            }

            return left;
        }

        private double ParseFactor()
        {
            double left = ParseUnary();

            SkipWhitespace();
            if (Peek() == '^')
            {
                _pos++;
                double right = ParseFactor(); // 右结合
                return Math.Pow(left, right);
            }

            return left;
        }

        private double ParseUnary()
        {
            SkipWhitespace();
            char op = Peek();
            if (op == '+')
            {
                _pos++;
                return ParseUnary();
            }
            if (op == '-')
            {
                _pos++;
                return -ParseUnary();
            }

            return ParsePostfix();
        }

        private double ParsePostfix()
        {
            double value = ParsePrimary();

            while (true)
            {
                SkipWhitespace();
                if (Peek() == '%')
                {
                    // 检查是否为后缀百分比（后面跟运算符、括号或结束）
                    int nextPos = _pos + 1;
                    while (nextPos < _src.Length && char.IsWhiteSpace(_src[nextPos])) nextPos++;
                    if (nextPos == _src.Length || _src[nextPos] is '+' or '-' or '*' or '/' or '^' or ')' or ',')
                    {
                        _pos = nextPos;
                        value /= 100.0;
                        continue;
                    }
                }
                break;
            }

            return value;
        }

        private double ParsePrimary()
        {
            SkipWhitespace();
            if (_pos >= _src.Length) throw new FormatException("Unexpected end of expression");

            char c = _src[_pos];

            // 括号
            if (c == '(')
            {
                _pos++;
                double val = ParseExpression();
                SkipWhitespace();
                if (Peek() != ')') throw new FormatException("Missing closing parenthesis ')'");
                _pos++;
                return val;
            }

            // 数字字面量
            if (char.IsDigit(c) || c == '.')
            {
                int start = _pos;
                bool hasDot = false;
                while (_pos < _src.Length && (char.IsDigit(_src[_pos]) || _src[_pos] == '.'))
                {
                    if (_src[_pos] == '.')
                    {
                        if (hasDot) break;
                        hasDot = true;
                    }
                    _pos++;
                }

                var numStr = _src[start.._pos];
                if (!double.TryParse(numStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double num))
                    throw new FormatException($"Invalid number format '{numStr}'");

                return num;
            }

            // 常量 π
            if (c == 'π')
            {
                _pos++;
                return Math.PI;
            }

            // 标识符（函数或常量）
            if (char.IsLetter(c))
            {
                int start = _pos;
                while (_pos < _src.Length && (char.IsLetterOrDigit(_src[_pos]) || _src[_pos] == '_'))
                    _pos++;

                string ident = _src[start.._pos].ToLowerInvariant();

                // 常量
                if (ident == "pi") return Math.PI;
                if (ident == "e") return Math.E;

                // 函数调用
                SkipWhitespace();
                if (Peek() != '(') throw new FormatException($"Expected '(' after function '{ident}'");
                _pos++;

                double arg = ParseExpression();
                SkipWhitespace();
                if (Peek() != ')') throw new FormatException($"Missing ')' for function '{ident}'");
                _pos++;

                return ident switch
                {
                    "sin" => Math.Sin(arg),
                    "cos" => Math.Cos(arg),
                    "tan" => Math.Tan(arg),
                    "sqrt" => arg < 0 ? throw new FormatException("Square root of negative number") : Math.Sqrt(arg),
                    "abs" => Math.Abs(arg),
                    "log" or "log10" => arg <= 0 ? throw new FormatException("Logarithm of non-positive number") : Math.Log10(arg),
                    "ln" => arg <= 0 ? throw new FormatException("Ln of non-positive number") : Math.Log(arg),
                    _ => throw new FormatException($"Unknown function '{ident}'")
                };
            }

            throw new FormatException($"Unexpected character '{c}' at position {_pos}");
        }
    }
}
