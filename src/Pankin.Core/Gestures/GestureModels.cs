using Pankin.Core.Abstractions;

namespace Pankin.Core.Gestures;

public enum GestureDirection
{
    Up = 0,
    UpRight = 1,
    Right = 2,
    DownRight = 3,
    Down = 4,
    DownLeft = 5,
    Left = 6,
    UpLeft = 7
}

public static class GestureDirectionExtensions
{
    public static string ToShortCode(this GestureDirection direction) => direction switch
    {
        GestureDirection.Up => "U",
        GestureDirection.Down => "D",
        GestureDirection.Left => "L",
        GestureDirection.Right => "R",
        GestureDirection.UpLeft => "UL",
        GestureDirection.UpRight => "UR",
        GestureDirection.DownLeft => "DL",
        GestureDirection.DownRight => "DR",
        _ => string.Empty
    };

    public static GestureDirection? FromShortCode(string code) => code.ToUpperInvariant() switch
    {
        "U" => GestureDirection.Up,
        "D" => GestureDirection.Down,
        "L" => GestureDirection.Left,
        "R" => GestureDirection.Right,
        "UL" or "LU" => GestureDirection.UpLeft,
        "UR" or "RU" => GestureDirection.UpRight,
        "DL" or "LD" => GestureDirection.DownLeft,
        "DR" or "RD" => GestureDirection.DownRight,
        _ => null
    };
}
