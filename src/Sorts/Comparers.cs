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

/// <summary>Adapts T's own CompareTo to the kernel contract. For the CTS integer
/// primitives the JIT-time typeof specialization below lowers IsLess to a single
/// native compare (cmp + cset) — the direct C# analog of rustc monomorphizing
/// `|a, b| *a &lt; *b` for i32. Without it, int.CompareTo's -1/0/1 body inlines as
/// two data-dependent branches (cmp+bge, cmp+bgt) plus a materializing cset per
/// comparison — 3x the instructions and mispredicting on random data in the
/// branchless partition hot loops (JitDisasm-verified). RyuJIT folds every
/// typeof(T) == typeof(X) test at compilation time, so non-matching arms cost
/// nothing at runtime. The primitive arms use the runtime's own `(T)(object)`
/// cast idiom (ArraySortHelper's LessThan/GreaterThan): RyuJIT folds the typeof
/// tests and elides the box/unbox entirely — verified zero allocations and a
/// single native compare per arm.
/// Floating point IS specialized below, sound only because
/// the default comparable entry runs FloatPrepass.MoveNansToFront first: with every
/// NaN parked at the front the remaining slice is NaN-free and the raw &gt; operator
/// agrees with CompareTo exactly (including -0.0 == +0.0). Any other caller of this
/// comparer on a float/double/Half span MUST run the same pre-pass; previously
/// floating point was left on CompareTo precisely because of that obligation.
/// Enums fall through to CompareTo (T is the enum type, not its underlying
/// primitive).
///
/// Why not rely on the BCL's own paths? int.CompareTo inlines through ALL of
/// them — direct call, generic constrained call, Comparer&lt;T&gt;.Default.Compare
/// (the whole property chain folds away) and even an IComparer&lt;int&gt; static
/// field (devirtualized via known concrete type). What happens next is
/// POLARITY-dependent (JitDisasm-verified on .NET 10 ARM64): RyuJIT recognizes
/// the pattern `CompareTo(...) &lt; 0` and folds it into a single native
/// cmp + branch — ideal for BRANCH-based consumers, which is exactly what
/// Array.Sort's introsort is (verified: its IntroSort / PickPivotAndPartition /
/// InsertionSort all compile to one compare per comparison; it pays no tax).
/// But that same fold materializes the bool via a sign-bit shift, severing the
/// cset → csel chain branchless code needs, while the `&gt; 0` polarity used
/// here keeps cset alive but leaves CompareTo's -1/0/1 body un-folded — two
/// extra data-dependent branches per comparison. The typeof specialization
/// escapes the dilemma entirely: the raw `&gt;` operator is single-instruction
/// under every consumer. There is no JIT intrinsification of CompareTo itself
/// (unlike EqualityComparer&lt;T&gt;.Default, which IS intrinsified). The sorts
/// here outperform Array.Sort on random data through their branchless
/// partition design, not through comparison quality.</summary>
internal readonly struct ComparableCmp<T> : IIsLess<T> where T : IComparable<T>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsLess(in T x, in T y)
    {
        if (typeof(T) == typeof(int)) return (int)(object)y > (int)(object)x; // x < y ⟺ y > x — see interface remarks
        if (typeof(T) == typeof(long)) return (long)(object)y > (long)(object)x;
        if (typeof(T) == typeof(uint)) return (uint)(object)y > (uint)(object)x;
        if (typeof(T) == typeof(ulong)) return (ulong)(object)y > (ulong)(object)x;
        if (typeof(T) == typeof(short)) return (short)(object)y > (short)(object)x;
        if (typeof(T) == typeof(ushort)) return (ushort)(object)y > (ushort)(object)x;
        if (typeof(T) == typeof(byte)) return (byte)(object)y > (byte)(object)x;
        if (typeof(T) == typeof(sbyte)) return (sbyte)(object)y > (sbyte)(object)x;
        if (typeof(T) == typeof(char)) return (char)(object)y > (char)(object)x;
        if (typeof(T) == typeof(double)) return (double)(object)y > (double)(object)x; // NaN-free slice only — see FloatPrepass
        if (typeof(T) == typeof(float)) return (float)(object)y > (float)(object)x; // NaN-free slice only — see FloatPrepass
        if (typeof(T) == typeof(Half)) return (Half)(object)y > (Half)(object)x; // NaN-free slice only — see FloatPrepass
        return y.CompareTo(x) > 0; // x < y ⟺ y > x — see interface remarks
    }
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

/// <summary>The inverted comparer — the C# analog of upstream's monomorphized closure
/// `|a, b| !is_less(b, a)` (ipnsort quicksort.rs:45, driftsort quicksort.rs:69,
/// glidesort's cmp_from_closure). Wrapping the struct comparer re-monomorphizes the
/// entire call chain with TC = InvertedCmp&lt;T, TC&gt;, so the negation and argument
/// swap fold into the comparison at JIT time — zero per-element cost, exactly like
/// the Rust closure. The naive alternative — threading a runtime `invert: bool`
/// into the partition loops — costs a test + branch per element and duplicates the
/// loop body (JitDisasm-verified on ARM64); upstream avoids both by construction.</summary>
internal readonly struct InvertedCmp<T, TC> : IIsLess<T> where TC : struct, IIsLess<T>
{
    private readonly TC _cmp;
    public InvertedCmp(TC cmp) => _cmp = cmp;

    /// <summary>IsLess(x, y) = !inner.IsLess(y, x): !(y &lt; x) ⟺ x &gt;= y. Note the
    /// argument swap AND the negation together — a plain negation without the swap
    /// would yield !x &lt; y = x &gt;= y with the operands in the wrong slots.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsLess(in T x, in T y) => !_cmp.IsLess(in y, in x);
}
