// Ported from dotnet/runtime (release/11.0), MIT License (.NET Foundation):
// src/libraries/System.Private.CoreLib/src/System/Collections/Generic/ArraySortHelper.cs
//
// Single-array path of Array.Sort<T>(T[]): ArraySortHelper<T> (the IComparer<T> /
// Comparison<T> entries) and GenericArraySortHelper<T> (the IComparable<T> default
// path, with the NaN pre-pass and the JIT-specialized primitive arms), plus
// SortUtils.MoveNansToFront. The key/value-pair and TComparer variants, and the
// BinarySearch paths, are omitted (out of scope).
//
// Standalone adaptations: Array.IntrosortSizeThreshold -> IntrosortConfig (same
// value, 16); ThrowHelper/ExceptionResource -> standard exceptions.
//
// Purpose: reference implementation for experiments that graft ipnsort techniques
// onto the current C# sort.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

#pragma warning disable CA1822

namespace Sorts.Experiments.Bcl;

/// <summary>Array.IntrosortSizeThreshold (System.Array): partitions at or below this
/// size are finished with insertion sort.</summary>
internal static class IntrosortConfig
{
    internal const int IntrosortSizeThreshold = 16;
}

/// <summary>Array.Sort&lt;T&gt;(T[], IComparer&lt;T&gt;) / (T[], Comparison&lt;T&gt;) path
/// (verbatim).</summary>
public sealed partial class ArraySortHelper<T>
{
    public void Sort(Span<T> keys, IComparer<T>? comparer)
    {
        // Add a try block here to detect IComparers (or their
        // underlying IComparables, etc) that are bogus.
        try
        {
            comparer ??= Comparer<T>.Default;
            IntrospectiveSort(keys, comparer.Compare);
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

    public static void Sort(Span<T> keys, Comparison<T> comparer)
    {
        Debug.Assert(comparer != null, "Check the arguments in the caller!");

        // Add a try block here to detect bogus comparisons
        try
        {
            IntrospectiveSort(keys, comparer);
        }
        catch (IndexOutOfRangeException)
        {
            throw new ArgumentException(
                "Comparison<T> method returns inconsistent results. For example, it might not return 0 when comparing an item with itself.",
                nameof(comparer));
        }
        catch (Exception e)
        {
            throw new InvalidOperationException("Failed to compare two elements in the array.", e);
        }
    }

    private static void SwapIfGreater(Span<T> keys, Comparison<T> comparer, int i, int j)
    {
        Debug.Assert(i != j);

        if (comparer(keys[i], keys[j]) > 0)
        {
            T key = keys[i];
            keys[i] = keys[j];
            keys[j] = key;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Swap(Span<T> a, int i, int j)
    {
        Debug.Assert(i != j);

        T t = a[i];
        a[i] = a[j];
        a[j] = t;
    }

    public static void IntrospectiveSort(Span<T> keys, Comparison<T> comparer)
    {
        Debug.Assert(comparer != null);

        if (keys.Length > 1)
        {
            IntroSort(keys, 2 * (BitOperations.Log2((uint)keys.Length) + 1), comparer);
        }
    }

    // IntroSort is recursive; block it from being inlined into itself as
    // this is currenly not profitable.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void IntroSort(Span<T> keys, int depthLimit, Comparison<T> comparer)
    {
        Debug.Assert(!keys.IsEmpty);
        Debug.Assert(depthLimit >= 0);
        Debug.Assert(comparer != null);

        int partitionSize = keys.Length;
        while (partitionSize > 1)
        {
            if (partitionSize <= IntrosortConfig.IntrosortSizeThreshold)
            {
                if (partitionSize == 2)
                {
                    SwapIfGreater(keys, comparer, 0, 1);
                    return;
                }

                if (partitionSize == 3)
                {
                    SwapIfGreater(keys, comparer, 0, 1);
                    SwapIfGreater(keys, comparer, 0, 2);
                    SwapIfGreater(keys, comparer, 1, 2);
                    return;
                }

                InsertionSort(keys.Slice(0, partitionSize), comparer);
                return;
            }

            if (depthLimit == 0)
            {
                HeapSort(keys.Slice(0, partitionSize), comparer);
                return;
            }
            depthLimit--;

            int p = PickPivotAndPartition(keys.Slice(0, partitionSize), comparer);

            // Note we've already partitioned around the pivot and do not have to move the pivot again.
            IntroSort(keys[(p + 1)..partitionSize], depthLimit, comparer);
            partitionSize = p;
        }
    }

    private static int PickPivotAndPartition(Span<T> keys, Comparison<T> comparer)
    {
        Debug.Assert(keys.Length >= IntrosortConfig.IntrosortSizeThreshold);
        Debug.Assert(comparer != null);

        int hi = keys.Length - 1;

        // Compute median-of-three.  But also partition them, since we've done the comparison.
        int middle = hi >> 1;

        // Sort lo, mid and hi appropriately, then pick mid as the pivot.
        SwapIfGreater(keys, comparer, 0, middle);  // swap the low with the mid point
        SwapIfGreater(keys, comparer, 0, hi);   // swap the low with the high
        SwapIfGreater(keys, comparer, middle, hi); // swap the middle with the high

        T pivot = keys[middle];
        Swap(keys, middle, hi - 1);
        int left = 0, right = hi - 1;  // We already partitioned lo and hi and put the pivot in hi - 1.  And we pre-increment & decrement below.

        while (left < right)
        {
            while (comparer(keys[++left], pivot) < 0) ;
            while (comparer(pivot, keys[--right]) < 0) ;

            if (left >= right)
                break;

            Swap(keys, left, right);
        }

        // Put pivot in the right location.
        if (left != hi - 1)
        {
            Swap(keys, left, hi - 1);
        }
        return left;
    }

    private static void HeapSort(Span<T> keys, Comparison<T> comparer)
    {
        Debug.Assert(comparer != null);
        Debug.Assert(!keys.IsEmpty);

        int n = keys.Length;
        for (int i = n >> 1; i >= 1; i--)
        {
            DownHeap(keys, i, n, comparer);
        }

        for (int i = n; i > 1; i--)
        {
            Swap(keys, 0, i - 1);
            DownHeap(keys, 1, i - 1, comparer);
        }
    }

    private static void DownHeap(Span<T> keys, int i, int n, Comparison<T> comparer)
    {
        Debug.Assert(comparer != null);

        T d = keys[i - 1];
        while (i <= n >> 1)
        {
            int child = 2 * i;
            if (child < n && comparer(keys[child - 1], keys[child]) < 0)
            {
                child++;
            }

            if (!(comparer(d, keys[child - 1]) < 0))
                break;

            keys[i - 1] = keys[child - 1];
            i = child;
        }

        keys[i - 1] = d;
    }

    private static void InsertionSort(Span<T> keys, Comparison<T> comparer)
    {
        for (int i = 0; i < keys.Length - 1; i++)
        {
            T t = keys[i + 1];

            int j = i;
            while (j >= 0 && comparer(t, keys[j]) < 0)
            {
                keys[j + 1] = keys[j];
                j--;
            }

            keys[j + 1] = t;
        }
    }
}

/// <summary>Array.Sort&lt;T&gt;(T[]) default path for T : IComparable&lt;T&gt; (verbatim):
/// NaN pre-pass + null-aware introsort + the JIT-specialized primitive arms.</summary>
public sealed partial class GenericArraySortHelper<T>
    where T : IComparable<T>
{
    public void Sort(Span<T> keys, IComparer<T>? comparer)
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

                    IntroSort(keys, 2 * (BitOperations.Log2((uint)keys.Length) + 1));
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

    /// <summary>Swaps the values in the two references if the first is greater than the second.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SwapIfGreater(ref T i, ref T j)
    {
        if (i != null && GreaterThan(ref i, ref j))
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

    // IntroSort is recursive; block it from being inlined into itself as
    // this is currenly not profitable.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void IntroSort(Span<T> keys, int depthLimit)
    {
        Debug.Assert(!keys.IsEmpty);
        Debug.Assert(depthLimit >= 0);

        int partitionSize = keys.Length;
        while (partitionSize > 1)
        {
            if (partitionSize <= IntrosortConfig.IntrosortSizeThreshold)
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

            int p = PickPivotAndPartition(keys.Slice(0, partitionSize));

            // Note we've already partitioned around the pivot and do not have to move the pivot again.
            IntroSort(keys[(p + 1)..partitionSize], depthLimit);
            partitionSize = p;
        }
    }

    // sizeof(T) for an unconstrained T is intentional here: it is the runtime size of
    // an element (pointer size for reference types), used to convert a byte offset into
    // an element index. CS8500 is suppressed for this method only — upstream suppresses
    // it at the CoreLib build level instead.
#pragma warning disable CS8500
    private static unsafe int PickPivotAndPartition(Span<T> keys)
    {
        Debug.Assert(keys.Length >= IntrosortConfig.IntrosortSizeThreshold);

        // Use median-of-three to select a pivot. Grab a reference to the 0th, Length-1th, and Length/2th elements, and sort them.
        ref T zeroRef = ref MemoryMarshal.GetReference(keys);
        ref T lastRef = ref Unsafe.Add(ref zeroRef, keys.Length - 1);
        ref T middleRef = ref Unsafe.Add(ref zeroRef, (keys.Length - 1) >> 1);
        SwapIfGreater(ref zeroRef, ref middleRef);
        SwapIfGreater(ref zeroRef, ref lastRef);
        SwapIfGreater(ref middleRef, ref lastRef);

        // Select the middle value as the pivot, and move it to be just before the last element.
        ref T nextToLastRef = ref Unsafe.Add(ref zeroRef, keys.Length - 2);
        T pivot = middleRef;
        Swap(ref middleRef, ref nextToLastRef);

        // Walk the left and right pointers, swapping elements as necessary, until they cross.
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
                while (Unsafe.IsAddressLessThan(ref leftRef, ref nextToLastRef) && GreaterThan(ref pivot, ref leftRef = ref Unsafe.Add(ref leftRef, 1))) ;
                while (Unsafe.IsAddressGreaterThan(ref rightRef, ref zeroRef) && LessThan(ref pivot, ref rightRef = ref Unsafe.Add(ref rightRef, -1))) ;
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
            if (child < n && (keys[child - 1] == null || LessThan(ref keys[child - 1], ref keys[child])))
            {
                child++;
            }

            if (keys[child - 1] == null || !LessThan(ref d, ref keys[child - 1]))
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
            while (j >= 0 && (t == null || LessThan(ref t, ref Unsafe.Add(ref MemoryMarshal.GetReference(keys), j))))
            {
                Unsafe.Add(ref MemoryMarshal.GetReference(keys), j + 1) = Unsafe.Add(ref MemoryMarshal.GetReference(keys), j);
                j--;
            }

            Unsafe.Add(ref MemoryMarshal.GetReference(keys), j + 1) = t!;
        }
    }

    // - These methods exist for use in sorting, where the additional operations present in
    //   the CompareTo methods that would otherwise be used on these primitives add non-trivial overhead,
    //   in particular for floating point where the CompareTo methods need to factor in NaNs.
    // - The floating-point comparisons here assume no NaNs, which is valid only because the sorting routines
    //   themselves special-case NaN with a pre-pass that ensures none are present in the values being sorted
    //   by moving them all to the front first and then sorting the rest.
    // - These are duplicated here rather than being on a helper type due to current limitations around generic inlining.

    [MethodImpl(MethodImplOptions.AggressiveInlining)] // compiles to a single comparison or method call
    private static bool LessThan(ref T left, ref T right)
    {
        if (typeof(T) == typeof(byte)) return (byte)(object)left < (byte)(object)right;
        if (typeof(T) == typeof(sbyte)) return (sbyte)(object)left < (sbyte)(object)right;
        if (typeof(T) == typeof(ushort)) return (ushort)(object)left < (ushort)(object)right;
        if (typeof(T) == typeof(short)) return (short)(object)left < (short)(object)right;
        if (typeof(T) == typeof(uint)) return (uint)(object)left < (uint)(object)right;
        if (typeof(T) == typeof(int)) return (int)(object)left < (int)(object)right;
        if (typeof(T) == typeof(ulong)) return (ulong)(object)left < (ulong)(object)right;
        if (typeof(T) == typeof(long)) return (long)(object)left < (long)(object)right;
        if (typeof(T) == typeof(nuint)) return (nuint)(object)left < (nuint)(object)right;
        if (typeof(T) == typeof(nint)) return (nint)(object)left < (nint)(object)right;
        if (typeof(T) == typeof(float)) return (float)(object)left < (float)(object)right;
        if (typeof(T) == typeof(double)) return (double)(object)left < (double)(object)right;
        if (typeof(T) == typeof(Half)) return (Half)(object)left < (Half)(object)right;
        return left.CompareTo(right) < 0 ? true : false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)] // compiles to a single comparison or method call
    private static bool GreaterThan(ref T left, ref T right)
    {
        if (typeof(T) == typeof(byte)) return (byte)(object)left > (byte)(object)right;
        if (typeof(T) == typeof(sbyte)) return (sbyte)(object)left > (sbyte)(object)right;
        if (typeof(T) == typeof(ushort)) return (ushort)(object)left > (ushort)(object)right;
        if (typeof(T) == typeof(short)) return (short)(object)left > (short)(object)right;
        if (typeof(T) == typeof(uint)) return (uint)(object)left > (uint)(object)right;
        if (typeof(T) == typeof(int)) return (int)(object)left > (int)(object)right;
        if (typeof(T) == typeof(ulong)) return (ulong)(object)left > (ulong)(object)right;
        if (typeof(T) == typeof(long)) return (long)(object)left > (long)(object)right;
        if (typeof(T) == typeof(nuint)) return (nuint)(object)left > (nuint)(object)right;
        if (typeof(T) == typeof(nint)) return (nint)(object)left > (nint)(object)right;
        if (typeof(T) == typeof(float)) return (float)(object)left > (float)(object)right;
        if (typeof(T) == typeof(double)) return (double)(object)left > (double)(object)right;
        if (typeof(T) == typeof(Half)) return (Half)(object)left > (Half)(object)right;
        return left.CompareTo(right) > 0 ? true : false;
    }
}

/// <summary>Helper methods for use in array/span sorting routines (verbatim; the
/// key/value-pair overloads of MoveNansToFront are out of scope).</summary>
public static class SortUtils
{
    public static int MoveNansToFront<TKey, TValue>(Span<TKey> keys, Span<TValue> values) where TKey : notnull
    {
        Debug.Assert(typeof(TKey) == typeof(double) || typeof(TKey) == typeof(float));

        int left = 0;

        for (int i = 0; i < keys.Length; i++)
        {
            if ((typeof(TKey) == typeof(double) && double.IsNaN((double)(object)keys[i])) ||
                (typeof(TKey) == typeof(float) && float.IsNaN((float)(object)keys[i])) ||
                (typeof(TKey) == typeof(Half) && Half.IsNaN((Half)(object)keys[i])))
            {
                TKey temp = keys[left];
                keys[left] = keys[i];
                keys[i] = temp;

                if ((uint)i < (uint)values.Length) // check to see if we have values
                {
                    TValue tempValue = values[left];
                    values[left] = values[i];
                    values[i] = tempValue;
                }

                left++;
            }
        }

        return left;
    }
}
