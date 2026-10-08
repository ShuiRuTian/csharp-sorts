using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

/// <summary>Floating-point NaN semantics of the default comparable entry. The NaN
/// pre-pass (FloatPrepass.MoveNansToFront) parks every NaN at the front — matching
/// Array.Sort — which is also the precondition that makes ComparableCmp's raw
/// comparisons sound. Covers double, float and Half, plus the all-NaN, determinism
/// and Array.Sort-parity edge cases.</summary>
public class NaNPrepassTests
{
    [Fact]
    public void Double_NaNsSortedToFront() => AssertNaNContract(
        new[] { 3.5, double.NaN, -1.0, 0.0, double.NaN, 42.0, -0.0 },
        double.IsNaN,
        CanonicalDouble);

    [Fact]
    public void Float_NaNsSortedToFront() => AssertNaNContract(
        new[] { 3.5f, float.NaN, -1.0f, 0.0f, float.NaN, 42.0f, -0.0f },
        float.IsNaN,
        CanonicalSingle);

    [Fact]
    public void Half_NaNsSortedToFront() => AssertNaNContract(
        new[] { (Half)3.5, Half.NaN, (Half)(-1.0), (Half)0.0, Half.NaN, (Half)42.0 },
        Half.IsNaN,
        CanonicalHalf);

    [Fact]
    public void AllNaN_IsUnchanged()
    {
        var a = new[] { double.NaN, double.NaN, double.NaN };
        Sorts.Ipnsort.Sort(a);
        Assert.All(a, x => Assert.True(double.IsNaN(x)));
    }

    [Fact]
    public void Double_WithNaNs_IsDeterministic()
    {
        var a = BuildDouble();
        var b = (double[])a.Clone();
        Sorts.Ipnsort.Sort(a);
        Sorts.Ipnsort.Sort(b);
        Assert.Equal(b, a);
    }

    [Fact]
    public void Half_WithNaNs_IsDeterministic()
    {
        var a = BuildHalf();
        var b = (Half[])a.Clone();
        Sorts.Ipnsort.Sort(a);
        Sorts.Ipnsort.Sort(b);
        Assert.Equal(b, a);
    }

    [Fact]
    public void Double_MatchesArraySort_OnNaNData()
    {
        var a = BuildDouble();
        var b = (double[])a.Clone();
        Sorts.Ipnsort.Sort(a);
        Array.Sort(b);
        // Both unstable: compare under a canonical total order (NaN payloads by bits).
        Array.Sort(a, Comparer<double>.Create(CanonicalDouble));
        Array.Sort(b, Comparer<double>.Create(CanonicalDouble));
        Assert.Equal(b, a);
    }

    [Fact]
    public void Float_MatchesArraySort_OnNaNData()
    {
        var a = BuildSingle();
        var b = (float[])a.Clone();
        Sorts.Ipnsort.Sort(a);
        Array.Sort(b);
        Array.Sort(a, Comparer<float>.Create(CanonicalSingle));
        Array.Sort(b, Comparer<float>.Create(CanonicalSingle));
        Assert.Equal(b, a);
    }

    private static double[] BuildDouble()
    {
        var r = new Random(20261009);
        var a = new double[1000];
        for (int i = 0; i < a.Length; i++)
            a[i] = r.Next(5) == 0 ? double.NaN : r.NextDouble() * 200 - 100;
        return a;
    }

    private static float[] BuildSingle()
    {
        var r = new Random(20261009);
        var a = new float[1000];
        for (int i = 0; i < a.Length; i++)
            a[i] = r.Next(5) == 0 ? float.NaN : (float)(r.NextDouble() * 200 - 100);
        return a;
    }

    private static Half[] BuildHalf()
    {
        var r = new Random(20261009);
        var a = new Half[200];
        for (int i = 0; i < a.Length; i++)
            a[i] = r.Next(5) == 0 ? Half.NaN : (Half)(r.NextDouble() * 200 - 100);
        return a;
    }

    private static void AssertNaNContract<T>(T[] input, Func<T, bool> isNaN, Comparison<T> canonical)
        where T : IComparable<T>
    {
        var expected = (T[])input.Clone();
        Sorts.Ipnsort.Sort(input);

        int nanCount = expected.Count(isNaN);

        // 1) All NaNs at the front, none after.
        for (int i = 0; i < nanCount; i++)
            Assert.True(isNaN(input[i]), $"expected NaN at {i}");
        for (int i = nanCount; i < input.Length; i++)
            Assert.False(isNaN(input[i]), $"unexpected NaN at {i}");

        // 2) The NaN-free remainder is non-decreasing under CompareTo.
        for (int i = nanCount + 1; i < input.Length; i++)
            Assert.True(input[i].CompareTo(input[i - 1]) >= 0, $"descending pair at {i}");

        // 3) Multiset preserved (canonical total order, NaN payloads by bits).
        Array.Sort(expected, Comparer<T>.Create(canonical));
        Array.Sort(input, Comparer<T>.Create(canonical));
        Assert.Equal(expected, input);
    }

    private static int CanonicalDouble(double a, double b)
    {
        int c = a.CompareTo(b);
        return c != 0 ? c : BitConverter.DoubleToInt64Bits(a).CompareTo(BitConverter.DoubleToInt64Bits(b));
    }

    private static int CanonicalSingle(float a, float b)
    {
        int c = a.CompareTo(b);
        return c != 0 ? c : BitConverter.SingleToInt32Bits(a).CompareTo(BitConverter.SingleToInt32Bits(b));
    }

    private static int CanonicalHalf(Half a, Half b)
    {
        int c = a.CompareTo(b);
        return c != 0 ? c : BitConverter.HalfToInt16Bits(a).CompareTo(BitConverter.HalfToInt16Bits(b));
    }
}
