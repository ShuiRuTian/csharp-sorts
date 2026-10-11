// The graft variants: the BCL introsort skeleton (the single-array
// GenericArraySortHelper<T> path — median-of-3 + Hoare, depth-limited introsort with
// heapsort fallback, ≤16 insertion sort, NaN pre-pass) with selected ipnsort parts
// plugged in. The variant is selected by an ops policy (a stateless struct, the
// port's IIsLess/ComparableCmp pattern), so every variant is one instantiation of
// one class and the policy folds/inlines per instantiation:
//
//   GraftArmsOps<T>       — BCL structure, comparisons via the Unsafe.As arms. The
//                           faithful port keeps the runtime's (T)(object) cast form;
//                           both emit identical full-opt codegen for value types
//                           (see src/Sorts/Comparers.cs).
//   GraftPartitionOps<T>  — + ipnsort's branchless Lomuto cyclic partition for the
//                           scalar arms (capability-gated), BCL Hoare kept for every
//                           other T.
//   GraftSmallSortOps<T>  — + ipnsort's small-sort (network/general/fallback
//                           dispatch, thresholds 32/32/16) replacing the ≤16
//                           insertion sort.
//   GraftAllOps<T>        — both.
//
// Design note (disasm-verified): for the shared-generic instantiation (__Canon,
// i.e. all reference types) the JIT does NOT inline comparison calls through a
// comparer struct passed as a type parameter — not even with instance members and
// [MethodImpl(AggressiveInlining)]: the port's own ComparableCmp<__Canon>.IsLess is
// compiled out-of-line, which is part of its reference-type deficit. Only arms that
// live on the SAME generic type as the caller inline (the BCL / faithful-port
// shape), so the BCL-structure paths call this class's own LessThan/GreaterThan
// below. The ops structs keep their arms for the ipnsort-internal paths (OpsCmp):
// those run on scalar instantiations (inline fine) or are the small-sort path,
// which for reference types pays the out-of-line comparer (a known limitation).
//
// Null handling: ipnsort's small-sort has no structural null guards, so the
// SmallSort/All ops add the null-first contract to their fall-through (the
// typeof(T).IsValueType gate folds it away for value types). Arms/Partition keep the
// plain BCL fall-through — the BCL structure guards every comparison call site, so
// no null checks are needed there.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Sorts.Experiments.Bcl;

/// <summary>Policy for one graft variant — instance members on a stateless struct
/// (the port's IIsLess pattern), so the JIT inlines and folds per instantiation.</summary>
public interface IGraftOps<T> where T : IComparable<T>
{
    bool LessThan(in T left, in T right);
    bool GreaterThan(in T left, in T right);

    /// <summary>Use ipnsort's branchless Lomuto partition where the comparison
    /// provably folds (the scalar arm set — see ArmCapability&lt;T&gt;).</summary>
    bool UseIpnPartition { get; }

    /// <summary>Use ipnsort's small-sort instead of the BCL's ≤16 insertion sort.</summary>
    bool UseIpnSmallSort { get; }
}

/// <summary>Exactly the arm set (all ≤ 8 bytes): the types whose comparison lowers
/// to a single native compare, the premise of the branchless Lomuto partition.
/// Evaluated once per partition, so its typeof chain is fine out of line.</summary>
internal static class ArmCapability<T> where T : IComparable<T>
{
    public static bool PreferLomuto =>
        typeof(T) == typeof(byte) ||
        typeof(T) == typeof(sbyte) ||
        typeof(T) == typeof(ushort) ||
        typeof(T) == typeof(short) ||
        typeof(T) == typeof(uint) ||
        typeof(T) == typeof(int) ||
        typeof(T) == typeof(ulong) ||
        typeof(T) == typeof(long) ||
        typeof(T) == typeof(nuint) ||
        typeof(T) == typeof(nint) ||
        typeof(T) == typeof(float) ||
        typeof(T) == typeof(double) ||
        typeof(T) == typeof(Half);
}

/// <summary>BCL structure + Unsafe.As comparison arms only (plain fall-through: the
/// BCL structure guards every call site, so no null checks are needed).</summary>
public readonly struct GraftArmsOps<T> : IGraftOps<T> where T : IComparable<T>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool LessThan(in T left, in T right)
    {
        if (typeof(T) == typeof(byte)) return Unsafe.As<T, byte>(ref Unsafe.AsRef(in left)) < Unsafe.As<T, byte>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(sbyte)) return Unsafe.As<T, sbyte>(ref Unsafe.AsRef(in left)) < Unsafe.As<T, sbyte>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(ushort)) return Unsafe.As<T, ushort>(ref Unsafe.AsRef(in left)) < Unsafe.As<T, ushort>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(short)) return Unsafe.As<T, short>(ref Unsafe.AsRef(in left)) < Unsafe.As<T, short>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(uint)) return Unsafe.As<T, uint>(ref Unsafe.AsRef(in left)) < Unsafe.As<T, uint>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(int)) return Unsafe.As<T, int>(ref Unsafe.AsRef(in left)) < Unsafe.As<T, int>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(ulong)) return Unsafe.As<T, ulong>(ref Unsafe.AsRef(in left)) < Unsafe.As<T, ulong>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(long)) return Unsafe.As<T, long>(ref Unsafe.AsRef(in left)) < Unsafe.As<T, long>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(nuint)) return Unsafe.As<T, nuint>(ref Unsafe.AsRef(in left)) < Unsafe.As<T, nuint>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(nint)) return Unsafe.As<T, nint>(ref Unsafe.AsRef(in left)) < Unsafe.As<T, nint>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(float)) return Unsafe.As<T, float>(ref Unsafe.AsRef(in left)) < Unsafe.As<T, float>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(double)) return Unsafe.As<T, double>(ref Unsafe.AsRef(in left)) < Unsafe.As<T, double>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(Half)) return Unsafe.As<T, Half>(ref Unsafe.AsRef(in left)) < Unsafe.As<T, Half>(ref Unsafe.AsRef(in right));
        return left.CompareTo(right) < 0 ? true : false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool GreaterThan(in T left, in T right)
    {
        if (typeof(T) == typeof(byte)) return Unsafe.As<T, byte>(ref Unsafe.AsRef(in left)) > Unsafe.As<T, byte>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(sbyte)) return Unsafe.As<T, sbyte>(ref Unsafe.AsRef(in left)) > Unsafe.As<T, sbyte>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(ushort)) return Unsafe.As<T, ushort>(ref Unsafe.AsRef(in left)) > Unsafe.As<T, ushort>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(short)) return Unsafe.As<T, short>(ref Unsafe.AsRef(in left)) > Unsafe.As<T, short>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(uint)) return Unsafe.As<T, uint>(ref Unsafe.AsRef(in left)) > Unsafe.As<T, uint>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(int)) return Unsafe.As<T, int>(ref Unsafe.AsRef(in left)) > Unsafe.As<T, int>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(ulong)) return Unsafe.As<T, ulong>(ref Unsafe.AsRef(in left)) > Unsafe.As<T, ulong>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(long)) return Unsafe.As<T, long>(ref Unsafe.AsRef(in left)) > Unsafe.As<T, long>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(nuint)) return Unsafe.As<T, nuint>(ref Unsafe.AsRef(in left)) > Unsafe.As<T, nuint>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(nint)) return Unsafe.As<T, nint>(ref Unsafe.AsRef(in left)) > Unsafe.As<T, nint>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(float)) return Unsafe.As<T, float>(ref Unsafe.AsRef(in left)) > Unsafe.As<T, float>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(double)) return Unsafe.As<T, double>(ref Unsafe.AsRef(in left)) > Unsafe.As<T, double>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(Half)) return Unsafe.As<T, Half>(ref Unsafe.AsRef(in left)) > Unsafe.As<T, Half>(ref Unsafe.AsRef(in right));
        return left.CompareTo(right) > 0 ? true : false;
    }

    public bool UseIpnPartition => false;
    public bool UseIpnSmallSort => false;
}

/// <summary>+ ipnsort branchless Lomuto partition for the scalar arms.</summary>
public readonly struct GraftPartitionOps<T> : IGraftOps<T> where T : IComparable<T>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool LessThan(in T left, in T right)
    {
        if (typeof(T) == typeof(byte)) return Unsafe.As<T, byte>(ref Unsafe.AsRef(in left)) < Unsafe.As<T, byte>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(sbyte)) return Unsafe.As<T, sbyte>(ref Unsafe.AsRef(in left)) < Unsafe.As<T, sbyte>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(ushort)) return Unsafe.As<T, ushort>(ref Unsafe.AsRef(in left)) < Unsafe.As<T, ushort>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(short)) return Unsafe.As<T, short>(ref Unsafe.AsRef(in left)) < Unsafe.As<T, short>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(uint)) return Unsafe.As<T, uint>(ref Unsafe.AsRef(in left)) < Unsafe.As<T, uint>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(int)) return Unsafe.As<T, int>(ref Unsafe.AsRef(in left)) < Unsafe.As<T, int>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(ulong)) return Unsafe.As<T, ulong>(ref Unsafe.AsRef(in left)) < Unsafe.As<T, ulong>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(long)) return Unsafe.As<T, long>(ref Unsafe.AsRef(in left)) < Unsafe.As<T, long>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(nuint)) return Unsafe.As<T, nuint>(ref Unsafe.AsRef(in left)) < Unsafe.As<T, nuint>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(nint)) return Unsafe.As<T, nint>(ref Unsafe.AsRef(in left)) < Unsafe.As<T, nint>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(float)) return Unsafe.As<T, float>(ref Unsafe.AsRef(in left)) < Unsafe.As<T, float>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(double)) return Unsafe.As<T, double>(ref Unsafe.AsRef(in left)) < Unsafe.As<T, double>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(Half)) return Unsafe.As<T, Half>(ref Unsafe.AsRef(in left)) < Unsafe.As<T, Half>(ref Unsafe.AsRef(in right));
        return left.CompareTo(right) < 0 ? true : false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool GreaterThan(in T left, in T right)
    {
        if (typeof(T) == typeof(byte)) return Unsafe.As<T, byte>(ref Unsafe.AsRef(in left)) > Unsafe.As<T, byte>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(sbyte)) return Unsafe.As<T, sbyte>(ref Unsafe.AsRef(in left)) > Unsafe.As<T, sbyte>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(ushort)) return Unsafe.As<T, ushort>(ref Unsafe.AsRef(in left)) > Unsafe.As<T, ushort>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(short)) return Unsafe.As<T, short>(ref Unsafe.AsRef(in left)) > Unsafe.As<T, short>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(uint)) return Unsafe.As<T, uint>(ref Unsafe.AsRef(in left)) > Unsafe.As<T, uint>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(int)) return Unsafe.As<T, int>(ref Unsafe.AsRef(in left)) > Unsafe.As<T, int>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(ulong)) return Unsafe.As<T, ulong>(ref Unsafe.AsRef(in left)) > Unsafe.As<T, ulong>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(long)) return Unsafe.As<T, long>(ref Unsafe.AsRef(in left)) > Unsafe.As<T, long>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(nuint)) return Unsafe.As<T, nuint>(ref Unsafe.AsRef(in left)) > Unsafe.As<T, nuint>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(nint)) return Unsafe.As<T, nint>(ref Unsafe.AsRef(in left)) > Unsafe.As<T, nint>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(float)) return Unsafe.As<T, float>(ref Unsafe.AsRef(in left)) > Unsafe.As<T, float>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(double)) return Unsafe.As<T, double>(ref Unsafe.AsRef(in left)) > Unsafe.As<T, double>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(Half)) return Unsafe.As<T, Half>(ref Unsafe.AsRef(in left)) > Unsafe.As<T, Half>(ref Unsafe.AsRef(in right));
        return left.CompareTo(right) > 0 ? true : false;
    }

    public bool UseIpnPartition => ArmCapability<T>.PreferLomuto;
    public bool UseIpnSmallSort => false;
}

/// <summary>+ ipnsort small-sort replacing the ≤16 insertion sort. Null-aware
/// fall-through (ipnsort's small-sort has no structural null guards).</summary>
public readonly struct GraftSmallSortOps<T> : IGraftOps<T> where T : IComparable<T>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool LessThan(in T left, in T right)
    {
        if (typeof(T) == typeof(byte)) return Unsafe.As<T, byte>(ref Unsafe.AsRef(in left)) < Unsafe.As<T, byte>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(sbyte)) return Unsafe.As<T, sbyte>(ref Unsafe.AsRef(in left)) < Unsafe.As<T, sbyte>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(ushort)) return Unsafe.As<T, ushort>(ref Unsafe.AsRef(in left)) < Unsafe.As<T, ushort>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(short)) return Unsafe.As<T, short>(ref Unsafe.AsRef(in left)) < Unsafe.As<T, short>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(uint)) return Unsafe.As<T, uint>(ref Unsafe.AsRef(in left)) < Unsafe.As<T, uint>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(int)) return Unsafe.As<T, int>(ref Unsafe.AsRef(in left)) < Unsafe.As<T, int>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(ulong)) return Unsafe.As<T, ulong>(ref Unsafe.AsRef(in left)) < Unsafe.As<T, ulong>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(long)) return Unsafe.As<T, long>(ref Unsafe.AsRef(in left)) < Unsafe.As<T, long>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(nuint)) return Unsafe.As<T, nuint>(ref Unsafe.AsRef(in left)) < Unsafe.As<T, nuint>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(nint)) return Unsafe.As<T, nint>(ref Unsafe.AsRef(in left)) < Unsafe.As<T, nint>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(float)) return Unsafe.As<T, float>(ref Unsafe.AsRef(in left)) < Unsafe.As<T, float>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(double)) return Unsafe.As<T, double>(ref Unsafe.AsRef(in left)) < Unsafe.As<T, double>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(Half)) return Unsafe.As<T, Half>(ref Unsafe.AsRef(in left)) < Unsafe.As<T, Half>(ref Unsafe.AsRef(in right));
        if (!typeof(T).IsValueType)
        {
            if (left is null) return right is not null;
            if (right is null) return false;
        }
        return left.CompareTo(right) < 0 ? true : false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool GreaterThan(in T left, in T right)
    {
        if (typeof(T) == typeof(byte)) return Unsafe.As<T, byte>(ref Unsafe.AsRef(in left)) > Unsafe.As<T, byte>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(sbyte)) return Unsafe.As<T, sbyte>(ref Unsafe.AsRef(in left)) > Unsafe.As<T, sbyte>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(ushort)) return Unsafe.As<T, ushort>(ref Unsafe.AsRef(in left)) > Unsafe.As<T, ushort>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(short)) return Unsafe.As<T, short>(ref Unsafe.AsRef(in left)) > Unsafe.As<T, short>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(uint)) return Unsafe.As<T, uint>(ref Unsafe.AsRef(in left)) > Unsafe.As<T, uint>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(int)) return Unsafe.As<T, int>(ref Unsafe.AsRef(in left)) > Unsafe.As<T, int>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(ulong)) return Unsafe.As<T, ulong>(ref Unsafe.AsRef(in left)) > Unsafe.As<T, ulong>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(long)) return Unsafe.As<T, long>(ref Unsafe.AsRef(in left)) > Unsafe.As<T, long>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(nuint)) return Unsafe.As<T, nuint>(ref Unsafe.AsRef(in left)) > Unsafe.As<T, nuint>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(nint)) return Unsafe.As<T, nint>(ref Unsafe.AsRef(in left)) > Unsafe.As<T, nint>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(float)) return Unsafe.As<T, float>(ref Unsafe.AsRef(in left)) > Unsafe.As<T, float>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(double)) return Unsafe.As<T, double>(ref Unsafe.AsRef(in left)) > Unsafe.As<T, double>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(Half)) return Unsafe.As<T, Half>(ref Unsafe.AsRef(in left)) > Unsafe.As<T, Half>(ref Unsafe.AsRef(in right));
        if (!typeof(T).IsValueType)
        {
            if (left is null) return false;
            if (right is null) return true;
        }
        return left.CompareTo(right) > 0 ? true : false;
    }

    public bool UseIpnPartition => false;
    public bool UseIpnSmallSort => true;
}

/// <summary>+ both grafted parts. Null-aware fall-through (see GraftSmallSortOps).</summary>
public readonly struct GraftAllOps<T> : IGraftOps<T> where T : IComparable<T>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool LessThan(in T left, in T right)
    {
        if (typeof(T) == typeof(byte)) return Unsafe.As<T, byte>(ref Unsafe.AsRef(in left)) < Unsafe.As<T, byte>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(sbyte)) return Unsafe.As<T, sbyte>(ref Unsafe.AsRef(in left)) < Unsafe.As<T, sbyte>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(ushort)) return Unsafe.As<T, ushort>(ref Unsafe.AsRef(in left)) < Unsafe.As<T, ushort>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(short)) return Unsafe.As<T, short>(ref Unsafe.AsRef(in left)) < Unsafe.As<T, short>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(uint)) return Unsafe.As<T, uint>(ref Unsafe.AsRef(in left)) < Unsafe.As<T, uint>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(int)) return Unsafe.As<T, int>(ref Unsafe.AsRef(in left)) < Unsafe.As<T, int>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(ulong)) return Unsafe.As<T, ulong>(ref Unsafe.AsRef(in left)) < Unsafe.As<T, ulong>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(long)) return Unsafe.As<T, long>(ref Unsafe.AsRef(in left)) < Unsafe.As<T, long>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(nuint)) return Unsafe.As<T, nuint>(ref Unsafe.AsRef(in left)) < Unsafe.As<T, nuint>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(nint)) return Unsafe.As<T, nint>(ref Unsafe.AsRef(in left)) < Unsafe.As<T, nint>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(float)) return Unsafe.As<T, float>(ref Unsafe.AsRef(in left)) < Unsafe.As<T, float>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(double)) return Unsafe.As<T, double>(ref Unsafe.AsRef(in left)) < Unsafe.As<T, double>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(Half)) return Unsafe.As<T, Half>(ref Unsafe.AsRef(in left)) < Unsafe.As<T, Half>(ref Unsafe.AsRef(in right));
        if (!typeof(T).IsValueType)
        {
            if (left is null) return right is not null;
            if (right is null) return false;
        }
        return left.CompareTo(right) < 0 ? true : false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool GreaterThan(in T left, in T right)
    {
        if (typeof(T) == typeof(byte)) return Unsafe.As<T, byte>(ref Unsafe.AsRef(in left)) > Unsafe.As<T, byte>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(sbyte)) return Unsafe.As<T, sbyte>(ref Unsafe.AsRef(in left)) > Unsafe.As<T, sbyte>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(ushort)) return Unsafe.As<T, ushort>(ref Unsafe.AsRef(in left)) > Unsafe.As<T, ushort>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(short)) return Unsafe.As<T, short>(ref Unsafe.AsRef(in left)) > Unsafe.As<T, short>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(uint)) return Unsafe.As<T, uint>(ref Unsafe.AsRef(in left)) > Unsafe.As<T, uint>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(int)) return Unsafe.As<T, int>(ref Unsafe.AsRef(in left)) > Unsafe.As<T, int>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(ulong)) return Unsafe.As<T, ulong>(ref Unsafe.AsRef(in left)) > Unsafe.As<T, ulong>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(long)) return Unsafe.As<T, long>(ref Unsafe.AsRef(in left)) > Unsafe.As<T, long>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(nuint)) return Unsafe.As<T, nuint>(ref Unsafe.AsRef(in left)) > Unsafe.As<T, nuint>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(nint)) return Unsafe.As<T, nint>(ref Unsafe.AsRef(in left)) > Unsafe.As<T, nint>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(float)) return Unsafe.As<T, float>(ref Unsafe.AsRef(in left)) > Unsafe.As<T, float>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(double)) return Unsafe.As<T, double>(ref Unsafe.AsRef(in left)) > Unsafe.As<T, double>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(Half)) return Unsafe.As<T, Half>(ref Unsafe.AsRef(in left)) > Unsafe.As<T, Half>(ref Unsafe.AsRef(in right));
        if (!typeof(T).IsValueType)
        {
            if (left is null) return false;
            if (right is null) return true;
        }
        return left.CompareTo(right) > 0 ? true : false;
    }

    public bool UseIpnPartition => ArmCapability<T>.PreferLomuto;
    public bool UseIpnSmallSort => true;
}

/// <summary>IIsLess adapter over the ops policy — lets the ported ipnsort internals
/// (IpnPartition / IpnSmallSort) run on the graft's comparison arms.</summary>
internal readonly struct OpsCmp<T, TOps> : IIsLess<T>
    where T : IComparable<T>
    where TOps : struct, IGraftOps<T>
{
    private readonly TOps _ops;
    public OpsCmp(TOps ops) => _ops = ops;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsLess(in T x, in T y) => _ops.LessThan(in x, in y);

    /// <summary>The partition gate is the policy's — same set the arms fold for.</summary>
    public static bool PreferLomuto(int elementSize) => default(TOps).UseIpnPartition;
}

/// <summary>The graft: BCL GenericArraySortHelper&lt;T&gt; structure with the
/// policy-selected ipnsort parts. The delegate entry (non-default comparer) is the
/// faithful BCL port's ArraySortHelper&lt;T&gt; — unchanged by the graft.</summary>
public static class GraftSort<T, TOps>
    where T : IComparable<T>
    where TOps : struct, IGraftOps<T>
{
    public static void Sort(Span<T> keys, IComparer<T>? comparer)
    {
        try
        {
            if (comparer == null || comparer == Comparer<T>.Default)
            {
                if (keys.Length > 1)
                {
                    // For floating-point, do a pre-pass to move all NaNs to the beginning
                    // so that we can do an optimized comparison as part of the actual sort
                    // on the remainder of the values.
                    if (typeof(T) == typeof(double) ||
                        typeof(T) == typeof(float) ||
                        typeof(T) == typeof(Half))
                    {
                        int nanLeft = SortUtils.MoveNansToFront(keys, default(Span<byte>));
                        if (nanLeft == keys.Length)
                        {
                            return;
                        }
                        keys = keys.Slice(nanLeft);
                    }

                    IntroSort(keys, 2 * (BitOperations.Log2((uint)keys.Length) + 1), default(TOps));
                }
            }
            else
            {
                ArraySortHelper<T>.IntrospectiveSort(keys, comparer.Compare);
            }
        }
        catch (IndexOutOfRangeException)
        {
            throw new ArgumentException(
                "IComparer.Compare() method returns inconsistent results. For example, it might not return 0 when comparing an item with itself.",
                nameof(comparer));
        }
        catch (Exception e)
        {
            throw new InvalidOperationException("Failed to compare two elements in the array.", e);
        }
    }

    // Same-class arms. Shared-generic (reference-type / __Canon) inlining requires the
    // comparison to live on the SAME generic type as the caller: a comparer struct
    // passed as a type parameter (the port's IIsLess pattern) is NOT inlined for
    // __Canon — disasm-verified: ComparableCmp<__Canon>.IsLess is compiled
    // out-of-line, and the port pays that on its reference-type path — while these
    // same-class arms inline exactly like the faithful port's own. The BCL structure
    // guards every call site, so these are plain (no null checks). The ops structs
    // keep their own arms for the ipnsort-internal paths (OpsCmp): those run on
    // scalar instantiations (inline fine) or are the null-aware small-sort path.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool LessThan(in T left, in T right)
    {
        if (typeof(T) == typeof(byte)) return Unsafe.As<T, byte>(ref Unsafe.AsRef(in left)) < Unsafe.As<T, byte>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(sbyte)) return Unsafe.As<T, sbyte>(ref Unsafe.AsRef(in left)) < Unsafe.As<T, sbyte>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(ushort)) return Unsafe.As<T, ushort>(ref Unsafe.AsRef(in left)) < Unsafe.As<T, ushort>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(short)) return Unsafe.As<T, short>(ref Unsafe.AsRef(in left)) < Unsafe.As<T, short>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(uint)) return Unsafe.As<T, uint>(ref Unsafe.AsRef(in left)) < Unsafe.As<T, uint>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(int)) return Unsafe.As<T, int>(ref Unsafe.AsRef(in left)) < Unsafe.As<T, int>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(ulong)) return Unsafe.As<T, ulong>(ref Unsafe.AsRef(in left)) < Unsafe.As<T, ulong>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(long)) return Unsafe.As<T, long>(ref Unsafe.AsRef(in left)) < Unsafe.As<T, long>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(nuint)) return Unsafe.As<T, nuint>(ref Unsafe.AsRef(in left)) < Unsafe.As<T, nuint>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(nint)) return Unsafe.As<T, nint>(ref Unsafe.AsRef(in left)) < Unsafe.As<T, nint>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(float)) return Unsafe.As<T, float>(ref Unsafe.AsRef(in left)) < Unsafe.As<T, float>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(double)) return Unsafe.As<T, double>(ref Unsafe.AsRef(in left)) < Unsafe.As<T, double>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(Half)) return Unsafe.As<T, Half>(ref Unsafe.AsRef(in left)) < Unsafe.As<T, Half>(ref Unsafe.AsRef(in right));
        return left.CompareTo(right) < 0 ? true : false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool GreaterThan(in T left, in T right)
    {
        if (typeof(T) == typeof(byte)) return Unsafe.As<T, byte>(ref Unsafe.AsRef(in left)) > Unsafe.As<T, byte>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(sbyte)) return Unsafe.As<T, sbyte>(ref Unsafe.AsRef(in left)) > Unsafe.As<T, sbyte>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(ushort)) return Unsafe.As<T, ushort>(ref Unsafe.AsRef(in left)) > Unsafe.As<T, ushort>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(short)) return Unsafe.As<T, short>(ref Unsafe.AsRef(in left)) > Unsafe.As<T, short>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(uint)) return Unsafe.As<T, uint>(ref Unsafe.AsRef(in left)) > Unsafe.As<T, uint>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(int)) return Unsafe.As<T, int>(ref Unsafe.AsRef(in left)) > Unsafe.As<T, int>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(ulong)) return Unsafe.As<T, ulong>(ref Unsafe.AsRef(in left)) > Unsafe.As<T, ulong>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(long)) return Unsafe.As<T, long>(ref Unsafe.AsRef(in left)) > Unsafe.As<T, long>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(nuint)) return Unsafe.As<T, nuint>(ref Unsafe.AsRef(in left)) > Unsafe.As<T, nuint>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(nint)) return Unsafe.As<T, nint>(ref Unsafe.AsRef(in left)) > Unsafe.As<T, nint>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(float)) return Unsafe.As<T, float>(ref Unsafe.AsRef(in left)) > Unsafe.As<T, float>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(double)) return Unsafe.As<T, double>(ref Unsafe.AsRef(in left)) > Unsafe.As<T, double>(ref Unsafe.AsRef(in right));
        if (typeof(T) == typeof(Half)) return Unsafe.As<T, Half>(ref Unsafe.AsRef(in left)) > Unsafe.As<T, Half>(ref Unsafe.AsRef(in right));
        return left.CompareTo(right) > 0 ? true : false;
    }

    // IntroSort is recursive; block it from being inlined into itself as
    // this is currenly not profitable.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void IntroSort(Span<T> keys, int depthLimit, TOps ops)
    {
        Debug.Assert(!keys.IsEmpty);
        Debug.Assert(depthLimit >= 0);

        int partitionSize = keys.Length;
        while (partitionSize > 1)
        {
            if (ops.UseIpnSmallSort)
            {
                if (partitionSize <= IpnSmallSort.SmallSortThreshold<T>())
                {
                    IpnSmallSort.SmallSort(keys.Slice(0, partitionSize), new OpsCmp<T, TOps>(ops));
                    return;
                }
            }
            else if (partitionSize <= IntrosortConfig.IntrosortSizeThreshold)
            {
                if (partitionSize == 2)
                {
                    SwapIfGreater(ref keys[0], ref keys[1]);
                    return;
                }

                if (partitionSize == 3)
                {
                    ref T hiRef = ref keys[2];
                    ref T him1Ref = ref keys[1];
                    ref T loRef = ref keys[0];

                    SwapIfGreater(ref loRef, ref him1Ref);
                    SwapIfGreater(ref loRef, ref hiRef);
                    SwapIfGreater(ref him1Ref, ref hiRef);
                    return;
                }

                InsertionSort(keys.Slice(0, partitionSize));
                return;
            }

            if (depthLimit == 0)
            {
                HeapSort(keys.Slice(0, partitionSize));
                return;
            }
            depthLimit--;

            int p = PickPivotAndPartition(keys.Slice(0, partitionSize), ops);

            // Note we've already partitioned around the pivot and do not have to move the pivot again.
            IntroSort(keys[(p + 1)..partitionSize], depthLimit, ops);
            partitionSize = p;
        }
    }

    // sizeof(T) for an unconstrained T is intentional here: it is the runtime size of
    // an element (pointer size for reference types), used to convert a byte offset into
    // an element index. CS8500 is suppressed for this method only — upstream suppresses
    // it at the CoreLib build level instead.
#pragma warning disable CS8500
    private static unsafe int PickPivotAndPartition(Span<T> keys, TOps ops)
    {
        Debug.Assert(keys.Length >= IntrosortConfig.IntrosortSizeThreshold);

        // BCL median-of-3 selection (ref-based as upstream): orders lo/mid/hi, then
        // picks the middle element as the pivot.
        ref T zeroRef = ref MemoryMarshal.GetReference(keys);
        ref T lastRef = ref Unsafe.Add(ref zeroRef, keys.Length - 1);
        ref T middleRef = ref Unsafe.Add(ref zeroRef, (keys.Length - 1) >> 1);
        SwapIfGreater(ref zeroRef, ref middleRef);
        SwapIfGreater(ref zeroRef, ref lastRef);
        SwapIfGreater(ref middleRef, ref lastRef);

        if (ops.UseIpnPartition)
        {
            // ipnsort partition over the whole span: pivot to front, branchless
            // Lomuto cyclic scan (the gate folds — ops.UseIpnPartition is this
            // comparer's PreferLomuto), pivot placed at numLt. Contract:
            // [0..numLt) < pivot, v[numLt] = pivot, (numLt..] >= pivot. The
            // median-of-3 ordering above is what makes the middle element the
            // pivot value (kept from the BCL).
            return IpnPartition.Partition(keys, (keys.Length - 1) >> 1, new OpsCmp<T, TOps>(ops));
        }

        // BCL Hoare scan (verbatim structure; comparisons via the policy arms).
        ref T nextToLastRef = ref Unsafe.Add(ref zeroRef, keys.Length - 2);
        T pivot = middleRef;
        Swap(ref middleRef, ref nextToLastRef);

        ref T leftRef = ref zeroRef, rightRef = ref nextToLastRef;
        while (Unsafe.IsAddressLessThan(ref leftRef, ref rightRef))
        {
            if (pivot == null)
            {
                while (Unsafe.IsAddressLessThan(ref leftRef, ref nextToLastRef) && (leftRef = ref Unsafe.Add(ref leftRef, 1)) == null) ;
                while (Unsafe.IsAddressGreaterThan(ref rightRef, ref zeroRef) && (rightRef = ref Unsafe.Add(ref rightRef, -1)) != null) ;
            }
            else
            {
                while (Unsafe.IsAddressLessThan(ref leftRef, ref nextToLastRef) && GreaterThan(in pivot, in leftRef = ref Unsafe.Add(ref leftRef, 1))) ;
                while (Unsafe.IsAddressGreaterThan(ref rightRef, ref zeroRef) && LessThan(in pivot, in rightRef = ref Unsafe.Add(ref rightRef, -1))) ;
            }

            if (Unsafe.IsAddressGreaterThanOrEqualTo(ref leftRef, ref rightRef))
            {
                break;
            }

            Swap(ref leftRef, ref rightRef);
        }

        // Put the pivot in the correct location.
        if (!Unsafe.AreSame(ref leftRef, ref nextToLastRef))
        {
            Swap(ref leftRef, ref nextToLastRef);
        }

        return (int)((nint)Unsafe.ByteOffset(ref zeroRef, ref leftRef) / sizeof(T));
    }
#pragma warning restore CS8500

    /// <summary>Swaps the values in the two references if the first is greater than the second.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SwapIfGreater(ref T i, ref T j)
    {
        if (i != null && GreaterThan(in i, in j))
        {
            Swap(ref i, ref j);
        }
    }

    /// <summary>Swaps the values in the two references, regardless of whether the two references are the same.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Swap(ref T i, ref T j)
    {
        Debug.Assert(!Unsafe.AreSame(ref i, ref j));

        T t = i;
        i = j;
        j = t;
    }

    private static void HeapSort(Span<T> keys)
    {
        Debug.Assert(!keys.IsEmpty);

        int n = keys.Length;
        for (int i = n >> 1; i >= 1; i--)
        {
            DownHeap(keys, i, n);
        }

        for (int i = n; i > 1; i--)
        {
            Swap(ref keys[0], ref keys[i - 1]);
            DownHeap(keys, 1, i - 1);
        }
    }

    private static void DownHeap(Span<T> keys, int i, int n)
    {
        T d = keys[i - 1];
        while (i <= n >> 1)
        {
            int child = 2 * i;
            if (child < n && (keys[child - 1] == null || LessThan(in keys[child - 1], in keys[child])))
            {
                child++;
            }

            if (keys[child - 1] == null || !LessThan(in d, in keys[child - 1]))
                break;

            keys[i - 1] = keys[child - 1];
            i = child;
        }

        keys[i - 1] = d;
    }

    private static void InsertionSort(Span<T> keys)
    {
        for (int i = 0; i < keys.Length - 1; i++)
        {
            T t = Unsafe.Add(ref MemoryMarshal.GetReference(keys), i + 1);

            int j = i;
            while (j >= 0 && (t == null || LessThan(in t, in Unsafe.Add(ref MemoryMarshal.GetReference(keys), j))))
            {
                Unsafe.Add(ref MemoryMarshal.GetReference(keys), j + 1) = Unsafe.Add(ref MemoryMarshal.GetReference(keys), j);
                j--;
            }

            Unsafe.Add(ref MemoryMarshal.GetReference(keys), j + 1) = t!;
        }
    }
}
