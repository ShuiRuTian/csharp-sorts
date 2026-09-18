using System;
using System.Collections.Generic;
using Sorts;
using Xunit;

public class ApiContractTests
{
    // Keyed lambdas: one entry per sort class per array-taking public overload, so
    // each guard theory body stays one line per behavior.
    private static readonly Dictionary<string, Action<int[]>> SortAll = new()
    {
        ["QuadSort"] = static a => Sorts.QuadSort.Sort(a),
        ["GlideSort"] = static a => Sorts.GlideSort.Sort(a),
        ["DriftSort"] = static a => Sorts.DriftSort.Sort(a),
    };

    private static readonly Dictionary<string, Action<int[], int, int>> SortRange = new()
    {
        ["QuadSort"] = static (a, i, l) => Sorts.QuadSort.Sort(a, i, l),
        ["GlideSort"] = static (a, i, l) => Sorts.GlideSort.Sort(a, i, l),
        ["DriftSort"] = static (a, i, l) => Sorts.DriftSort.Sort(a, i, l),
    };

    private static readonly Dictionary<string, Action<int[]>> SortWithNullComparer = new()
    {
        ["QuadSort"] = static a => Sorts.QuadSort.Sort(a, (IComparer<int>?)null),
        ["GlideSort"] = static a => Sorts.GlideSort.Sort(a, (IComparer<int>?)null),
        ["DriftSort"] = static a => Sorts.DriftSort.Sort(a, (IComparer<int>?)null),
    };

    private static readonly Dictionary<string, Action<int[]>> SortWithComparison = new()
    {
        ["QuadSort"] = static a => Sorts.QuadSort.Sort(a, static (int x, int y) => x.CompareTo(y)),
        ["GlideSort"] = static a => Sorts.GlideSort.Sort(a, static (int x, int y) => x.CompareTo(y)),
        ["DriftSort"] = static a => Sorts.DriftSort.Sort(a, static (int x, int y) => x.CompareTo(y)),
    };

    public static IEnumerable<object[]> AllSorts() // reflect over the 3 classes
        => new object[][] { new[] { "QuadSort" }, new[] { "GlideSort" }, new[] { "DriftSort" } };

    [Theory]
    [MemberData(nameof(AllSorts))]
    public void NullArrayThrows(string sortName)
    {
        Assert.Throws<ArgumentNullException>(() => SortAll[sortName](null!));
        Assert.Throws<ArgumentNullException>(() => SortRange[sortName](null!, 0, 0));
        Assert.Throws<ArgumentNullException>(() => SortWithNullComparer[sortName](null!));
        Assert.Throws<ArgumentNullException>(() => SortWithComparison[sortName](null!));
    }

    [Theory]
    [MemberData(nameof(AllSorts))]
    public void BadRangeThrows(string sortName)
    {
        var sort = SortRange[sortName];
        Assert.Throws<ArgumentOutOfRangeException>(() => sort(new int[5], -1, 2)); // index < 0
        Assert.Throws<ArgumentOutOfRangeException>(() => sort(new int[5], 0, -1)); // length < 0
        Assert.Throws<ArgumentOutOfRangeException>(() => sort(new int[5], 3, 3));  // index + length > array.Length
    }

    [Theory]
    [MemberData(nameof(AllSorts))]
    public void ZeroAndOneElementNoop(string sortName)
    {
        var sort = SortAll[sortName];

        var empty = Array.Empty<int>();
        sort(empty);
        Assert.Empty(empty);

        var single = new[] { 42 };
        sort(single);
        Assert.Equal(new[] { 42 }, single);
    }

    [Fact]
    public void IComparerNullMeansDefault()
    {
        // Sort(int[], (IComparer<int>?)null) sorts ascending — needs a working kernel;
        // kept intentionally RED (NotImplementedException) until the first algorithm task lands.
        var array = new[] { 3, 1, 2 };
        Sorts.QuadSort.Sort(array, (IComparer<int>?)null);
        Assert.Equal(new[] { 1, 2, 3 }, array);
    }
}
