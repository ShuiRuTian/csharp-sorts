using System;
using System.Collections.Generic;

namespace Sorts;

/// <summary>Kernel comparison contract: true iff x &lt; y. Struct implementations get
/// JIT value-type specialization (devirtualized + inlined) — the C# equivalent of
/// Rust monomorphization / C cmp macros.</summary>
internal interface IIsLess<T>
{
    bool IsLess(in T x, in T y);
}

internal readonly struct ComparableCmp<T> : IIsLess<T> where T : IComparable<T>
{
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public bool IsLess(in T x, in T y) => x.CompareTo(y) < 0;
}

internal readonly struct InterfaceCmp<T> : IIsLess<T>
{
    private readonly IComparer<T> _cmp;
    public InterfaceCmp(IComparer<T>? cmp) => _cmp = cmp ?? Comparer<T>.Default;
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public bool IsLess(in T x, in T y) => _cmp.Compare(x, y) < 0;
}

internal readonly struct ComparisonCmp<T> : IIsLess<T>
{
    private readonly Comparison<T> _cmp;
    public ComparisonCmp(Comparison<T> cmp) => _cmp = cmp;
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public bool IsLess(in T x, in T y) => _cmp(x, y) < 0;
}

/// <summary>Adapts a struct IComparer&lt;T&gt; to the IIsLess kernel contract — the
/// JIT-specialized public entry shared by all three algorithms' Sort&lt;T,TC&gt; overloads.</summary>
internal readonly struct ComparerAdapter<T, TC> : IIsLess<T> where TC : struct, IComparer<T>
{
    private readonly TC _c;
    public ComparerAdapter(TC c) => _c = c;

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public bool IsLess(in T x, in T y) => _c.Compare(x, y) < 0;
}
