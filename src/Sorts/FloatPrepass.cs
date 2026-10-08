using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Sorts;

/// <summary>Port of the runtime's <c>SortUtils.MoveNansToFront</c>
/// (dotnet/runtime, System.Private.CoreLib/.../ArraySortHelper.cs).
///
/// Partitions every NaN to the front of the span in place and returns their count. The
/// remaining <c>keys.Slice(count)</c> is NaN-free, which is exactly the precondition that
/// lets the hot comparison use the raw <c>&lt;</c>/<c>&gt;</c> operator: for two non-NaN
/// values the operator agrees with <c>CompareTo</c>, and all NaNs (which <c>CompareTo</c>
/// orders before negative infinity) stay in the leading partition. Covers double, float
/// and Half — the same three types the BCL call sites check for.
///
/// Only the three floating-point types do work; for any other element type RyuJIT
/// constant-folds the <c>typeof</c> chain to <c>false</c> and the loop degenerates to a
/// length scan, so the entry cost is zero for non-fp instantiations.</summary>
internal static class FloatPrepass
{
    internal static int MoveNansToFront<T>(Span<T> keys)
    {
        int left = 0;
        ref T baseRef = ref MemoryMarshal.GetReference(keys);
        for (int i = 0; i < keys.Length; i++)
        {
            ref T cur = ref Unsafe.Add(ref baseRef, i);
            bool isNan =
                typeof(T) == typeof(double) ? double.IsNaN(Unsafe.As<T, double>(ref cur)) :
                typeof(T) == typeof(float) ? float.IsNaN(Unsafe.As<T, float>(ref cur)) :
                typeof(T) == typeof(Half) ? Half.IsNaN(Unsafe.As<T, Half>(ref cur)) :
                false;
            if (isNan)
            {
                ref T dst = ref Unsafe.Add(ref baseRef, left);
                T tmp = dst;
                dst = cur;
                cur = tmp;
                left++;
            }
        }
        return left;
    }
}
