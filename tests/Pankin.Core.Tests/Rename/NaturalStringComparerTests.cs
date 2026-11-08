using System.Collections.Generic;
using Pankin.Core.Rename;
using Xunit;

namespace Pankin.Core.Tests.Rename;

public class NaturalStringComparerTests
{
    private readonly NaturalStringComparer _comparer = NaturalStringComparer.OrdinalIgnoreCase;

    [Theory]
    [InlineData("file1.txt", "file2.txt", -1)]
    [InlineData("file2.txt", "file10.txt", -1)]
    [InlineData("file10.txt", "file2.txt", 1)]
    [InlineData("item20", "item100", -1)]
    [InlineData("a", "b", -1)]
    [InlineData("b", "a", 1)]
    [InlineData("same", "same", 0)]
    [InlineData("SAME", "same", 0)]
    public void Compare_ShouldSortNaturally(string left, string right, int expectedSign)
    {
        int result = _comparer.Compare(left, right);
        if (expectedSign == 0)
        {
            Assert.Equal(0, result);
        }
        else if (expectedSign < 0)
        {
            Assert.True(result < 0, $"Expected {left} < {right}, got {result}");
        }
        else
        {
            Assert.True(result > 0, $"Expected {left} > {right}, got {result}");
        }
    }

    [Fact]
    public void Sort_ListWithNumbers_ShouldBeInNaturalOrder()
    {
        var list = new List<string> { "img12.png", "img1.png", "img2.png", "img10.png", "img20.png" };
        list.Sort(_comparer);

        Assert.Equal(new[] { "img1.png", "img2.png", "img10.png", "img12.png", "img20.png" }, list);
    }

    [Fact]
    public void Compare_Nulls_ShouldHandleSafely()
    {
        Assert.Equal(0, _comparer.Compare(null, null));
        Assert.True(_comparer.Compare(null, "a") < 0);
        Assert.True(_comparer.Compare("a", null) > 0);
    }
}
