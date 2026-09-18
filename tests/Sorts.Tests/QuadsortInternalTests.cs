using System;
using System.Linq;
using Sorts;
using Sorts.TestData;
using Xunit;

public class QuadsortInternalTests
{
    private static void AssertSorted<T, TC>(T[] a, TC cmp) where TC : struct, IIsLess<T>
    {
        for (int i = 1; i < a.Length; i++)
            Assert.False(cmp.IsLess(in a[i], in a[i - 1]));
    }

    [Fact]
    public void TinySortHandles0Through31()
    {
        for (int n = 0; n <= 31; n++)
        {
            var a = DataGen.Ints(Distribution.Random, Math.Max(n, 1), n + 1)[..n];
            var copy = a.ToArray();
            QuadsortImpl.TinySort<int, ComparableCmp<int>>(a, new int[64], new());
            AssertSorted(a, new ComparableCmp<int>());
            Assert.Equal(copy.OrderBy(x => x), a);
        }
    }

    [Theory]
    [InlineData(4096)]
    [InlineData(4104)]
    public void QuadSwapProducesSorted32BlocksOnRandomData(int n)
    {
        var a = DataGen.Ints(Distribution.Random, n, 42);
        QuadsortImpl.QuadSwap<int, ComparableCmp<int>>(a, new int[64], new());

        // every full 32-block is sorted
        int blocks = n / 32;
        for (int b = 0; b < blocks; b++)
            for (int i = 1; i < 32; i++)
                Assert.False(a[b * 32 + i] < a[b * 32 + i - 1],
                    $"block {b} descending pair at {i} for n={n}");

        // remaining tail (n % 32 ≤ 8 here): sorted among itself, not merged into a block
        int tailStart = blocks * 32;
        for (int i = tailStart + 1; i < n; i++)
            Assert.False(a[i] < a[i - 1], $"tail descending pair at {i} for n={n}");

        // whole-array multiset preserved
        Assert.Equal(a.OrderBy(x => x), DataGen.Ints(Distribution.Random, n, 42).OrderBy(x => x));
    }

    [Fact]
    public void QuadSwapDetectsFullDescending()
    {
        const int n = 1000;
        var a = Enumerable.Range(0, n).Select(i => n - i).ToArray(); // strictly descending
        int ret = QuadsortImpl.QuadSwap<int, ComparableCmp<int>>(a, new int[64], new());
        Assert.Equal(1, ret);
        Assert.Equal(Enumerable.Range(1, n), a);
    }

    // Task 6: rotate_merge fallback — 64-element scratch forces rotate_merge for deep merges.

    [Fact]
    public void RotateMergePathSortsCorrectlyWithTinyScratch()
    {
        var a = DataGen.Ints(Distribution.Random, 5000, 42);
        var expected = a.OrderBy(x => x).ToArray();
        QuadsortImpl.QuadsortWithScratch<int, ComparableCmp<int>>(a, new int[64], 64, new());
        Assert.Equal(expected, a);
    }

    [Fact]
    public void RotateMergePathSortsCorrectlyWithTinyScratchRandomD20()
    {
        var a = DataGen.Ints(Distribution.RandomD20, 5000, 43);
        var expected = a.OrderBy(x => x).ToArray();
        QuadsortImpl.QuadsortWithScratch<int, ComparableCmp<int>>(a, new int[64], 64, new());
        Assert.Equal(expected, a);
    }
}
