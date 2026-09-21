using System;

namespace BaseCases;

/// <summary>与库内 src/Sorts/Comparers.cs 相同的比较器形态：谓词写作
/// <c>y.CompareTo(x) &gt; 0</c>（参数交换、取大于）。所有 case 都用同一形态，
/// 以便各自隔离被测代码形状本身。</summary>
internal interface IIsLess<T>
{
    bool IsLess(in T x, in T y);
}

internal readonly struct Cmp<T> : IIsLess<T> where T : IComparable<T>
{
    public bool IsLess(in T x, in T y) => y.CompareTo(x) > 0;
}

internal static class Data
{
    internal static int[] RandomInts(int n, int seed = 42)
    {
        var rnd = new Random(seed);
        var a = new int[n];
        for (int i = 0; i < n; i++)
            a[i] = rnd.Next();
        return a;
    }

    /// <summary>两段各自有序的数组（前半 + 后半），供 merge 类 case 使用。</summary>
    internal static int[] TwoSortedRuns(int half, int seed = 42)
    {
        var a = RandomInts(half * 2, seed);
        Array.Sort(a, 0, half);
        Array.Sort(a, half, half);
        return a;
    }
}
