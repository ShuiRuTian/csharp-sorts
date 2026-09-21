using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Sorts;

/// <summary>Kernel comparison contract: true iff x &lt; y. Struct implementations get
/// JIT value-type specialization (devirtualized + inlined) — the C# equivalent of
/// Rust monomorphization / C cmp macros.
///
/// All int-returning implementations express the predicate as `y.CompareTo(x) &gt; 0`
/// (args swapped, greater-than) rather than `x.CompareTo(y) &lt; 0`. The two are
/// mathematically identical for any antisymmetric comparer (the Ord contract
/// upstream Rust assumes too), but NOT to RyuJIT: `&lt; 0` gets folded into a
/// sign-bit trick (`(uint)result &gt;&gt; 31`, lsr) which severs the link between the
/// comparison and the select, so every downstream value ternary degrades to a
/// data-dependent branch. `&gt; 0` cannot be bit-tricked and keeps the cset/cmp
/// alive, letting value ternaries fold to csel/cmov — JitDisasm-verified on both
/// ARM64 and x64.</summary>
internal interface IIsLess<T>
{
    bool IsLess(in T x, in T y);
}

internal readonly struct ComparableCmp<T> : IIsLess<T> where T : IComparable<T>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsLess(in T x, in T y) => y.CompareTo(x) > 0; // x < y ⟺ y > x — see interface remarks
}

internal readonly struct InterfaceCmp<T> : IIsLess<T>
{
    private readonly IComparer<T> _cmp;
    public InterfaceCmp(IComparer<T>? cmp) => _cmp = cmp ?? Comparer<T>.Default;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsLess(in T x, in T y) => _cmp.Compare(y, x) > 0; // x < y ⟺ Compare(y,x) > 0 — see interface remarks
}

internal readonly struct ComparisonCmp<T> : IIsLess<T>
{
    private readonly Comparison<T> _cmp;
    public ComparisonCmp(Comparison<T> cmp) => _cmp = cmp;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsLess(in T x, in T y) => _cmp(y, x) > 0; // x < y ⟺ cmp(y,x) > 0 — see interface remarks
}

/// <summary>Adapts a struct IComparer&lt;T&gt; to the IIsLess kernel contract — the
/// JIT-specialized public entry shared by all three algorithms' Sort&lt;T,TC&gt; overloads.</summary>
internal readonly struct ComparerAdapter<T, TC> : IIsLess<T> where TC : struct, IComparer<T>
{
    private readonly TC _c;
    public ComparerAdapter(TC c) => _c = c;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsLess(in T x, in T y) => _c.Compare(y, x) > 0; // x < y ⟺ Compare(y,x) > 0 — see interface remarks
}
