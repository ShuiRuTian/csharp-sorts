using System;
using System.Collections.Generic;
using System.Text;
using BenchmarkDotNet.Attributes;

namespace Sorts.Benchmarks;

/// <summary>Faithful port of dotnet/performance's array-sort case
/// (src/benchmarks/micro/libraries/System.Collections/Sort.cs):
///   Size   = Utils.DefaultCollectionSize = 512
///   data   = ValuesGenerator.ArrayOfUniqueValues&lt;T&gt;(Size), seed 12345
///   types  = int, string, IntStruct, IntClass, BigStruct
/// Each nested class runs the BCL `Array.Sort&lt;T&gt;(T[], 0, Size)` (baseline) against
/// `Sorts.Ipnsort.Sort&lt;T&gt;(T[])` on the identical array. See DataTypes.cs upstream for
/// the element types and ValuesGenerator.cs for the generator (both reproduced below).</summary>
public static class PerfValues
{
    private const int Seed = 12345; // same seed as dotnet/performance ValuesGenerator

    public static T[] ArrayOfUniqueValues<T>(int count)
    {
        var result = new T[count];
        var random = new Random(Seed);
        var unique = new HashSet<T>();
        while (unique.Count != count)
            unique.Add(GenerateValue<T>(random));
        unique.CopyTo(result);
        return result;
    }

    private static T GenerateValue<T>(Random random)
    {
        if (typeof(T) == typeof(int))
            return (T)(object)random.Next();
        if (typeof(T) == typeof(string))
            return (T)(object)GenerateRandomString(random, 1, 50);
        if (typeof(T).GetConstructor(new[] { typeof(int) }) is { } ctor)
            return (T)ctor.Invoke(new object[] { random.Next() });
        throw new NotSupportedException(typeof(T).Name);
    }

    private static string GenerateRandomString(Random random, int minLength, int maxLength)
    {
        int length = random.Next(minLength, maxLength);
        var sb = new StringBuilder(length);
        for (int i = 0; i < length; i++)
        {
            int sel = random.Next(0, 3);
            if (sel == 0) sb.Append((char)random.Next('a', 'z'));
            else if (sel == 1) sb.Append((char)random.Next('A', 'Z'));
            else sb.Append((char)random.Next('0', '9'));
        }
        return sb.ToString();
    }
}

public readonly struct IntStruct : IComparable<IntStruct>
{
    private readonly int _value;
    public IntStruct(int value) => _value = value;
    public int CompareTo(IntStruct other) => _value.CompareTo(other._value);
}

public class IntClass : IComparable<IntClass>
{
    private readonly int _value;
    public IntClass(int value) => _value = value;
    public int CompareTo(IntClass? other) => _value.CompareTo(other!._value);
}

public readonly struct BigStruct : IComparable<BigStruct>
{
    private readonly long _long;
    private readonly int _int0;
    private readonly int _int1;
    private readonly short _short0;
    private readonly short _short1;
    private readonly short _short2;
    private readonly short _short3;
    private readonly double _double;

    public BigStruct(int value)
    {
        _long = value;
        _int0 = value;
        _int1 = value;
        _short0 = (short)value;
        _short1 = (short)value;
        _short2 = (short)value;
        _short3 = (short)value;
        _double = value;
    }

    public int CompareTo(BigStruct other) => _int1.CompareTo(other._int1);
}

/// <summary>dotnet/performance Sort&lt;T&gt; case matrix, BCL vs Ipnsort.</summary>
public class DotnetPerfSortBench
{
    public const int Size = 512; // Utils.DefaultCollectionSize (the official case's size)

    // Size sweep: our small-array series (10..400) plus the official 512 point.
    // [Params] per nested class so BDN emits one case per (type, size); the data
    // generator (ArrayOfUniqueValues, seed 12345) and the two methods are unchanged.

    [Config(typeof(BenchConfig))]
    public class Int : SortMatrixBase<int>
    {
        [Params(10, 20, 30, 50, 80, 100, 200, 400, 512)]
        public int N { get; set; }
        [GlobalSetup] public void Setup() => Init(PerfValues.ArrayOfUniqueValues<int>(N));
    }

    [Config(typeof(BenchConfig))]
    public class String : SortMatrixBase<string>
    {
        [Params(10, 20, 30, 50, 80, 100, 200, 400, 512)]
        public int N { get; set; }
        [GlobalSetup] public void Setup() => Init(PerfValues.ArrayOfUniqueValues<string>(N));
    }

    [Config(typeof(BenchConfig))]
    public class IntStructBench : SortMatrixBase<IntStruct>
    {
        [Params(10, 20, 30, 50, 80, 100, 200, 400, 512)]
        public int N { get; set; }
        [GlobalSetup] public void Setup() => Init(PerfValues.ArrayOfUniqueValues<IntStruct>(N));
    }

    [Config(typeof(BenchConfig))]
    public class IntClassBench : SortMatrixBase<IntClass>
    {
        [Params(10, 20, 30, 50, 80, 100, 200, 400, 512)]
        public int N { get; set; }
        [GlobalSetup] public void Setup() => Init(PerfValues.ArrayOfUniqueValues<IntClass>(N));
    }

    [Config(typeof(BenchConfig))]
    public class BigStructBench : SortMatrixBase<BigStruct>
    {
        [Params(10, 20, 30, 50, 80, 100, 200, 400, 512)]
        public int N { get; set; }
        [GlobalSetup] public void Setup() => Init(PerfValues.ArrayOfUniqueValues<BigStruct>(N));
    }
}

/// <summary>dotnet/performance's Array_ComparerStruct case (a struct IComparer&lt;int&gt;):
/// the BCL takes it through the IComparer&lt;T&gt; interface (boxing the struct), while
/// Ipnsort's Sort&lt;T,TC&gt; kernel monomorphizes it (JIT value-type specialization).</summary>
public class StructComparerBench
{
    private int[] _template = null!;
    private int[] _work = null!;

    public readonly struct Cmp : IComparer<int>
    {
        public int Compare(int x, int y) => x.CompareTo(y);
    }

    [GlobalSetup]
    public void Setup()
    {
        _template = PerfValues.ArrayOfUniqueValues<int>(DotnetPerfSortBench.Size);
        _work = new int[_template.Length];
    }

    [Benchmark(Baseline = true)]
    public void ArraySort_ComparerStruct()
    {
        _template.AsSpan().CopyTo(_work);
        Array.Sort(_work, 0, _work.Length, new Cmp());
    }

    [Benchmark]
    public void Ipnsort_ComparerStruct()
    {
        _template.AsSpan().CopyTo(_work);
        Sorts.Ipnsort.Sort<int, Cmp>(_work.AsSpan(), default);
    }
}
