// Ported from https://github.com/orlp/glidesort, MIT OR Apache-2.0, by Orson Peters & Lukas Bergdoll.
// C# port 2026 — small_sort.rs: the SMALL_SORT (48)-element small-sort network.
// Rust's MutSlice typestate only proves read-before-write discipline; the ported
// code maintains that discipline by construction on plain Span<T>.
using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Sorts;

/// <summary>GlideSort small-sort kernel (upstream small_sort.rs): sorts spans of up to
/// SmallSort elements in place, using only its own stack scratch.</summary>
internal static class GlideSmallSort
{
    /// <summary>Upstream SMALL_SORT (lib.rs:35) — largest input the small-sort network handles.</summary>
    internal const int SmallSort = 48;

    /// <summary>small_sort (small_sort.rs:7-9): sorts el (len &lt;= SmallSort) in place via
    /// block_insertion_sort over pow2 sorting networks.</summary>
    internal static void Sort<T, TC>(Span<T> el, TC cmp) where TC : struct, IIsLess<T>
        => BlockInsertionSort(el, cmp);

    /// <summary>sort4_raw (small_sort.rs:11-50): optimal 5-comparison stable network sorting
    /// four elements from src into dst. src and dst must not overlap.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Sort4Raw<T, TC>(ref T srcp, ref T dstp, TC cmp) where TC : struct, IIsLess<T>
    {
        // Stably create two pairs a <= b and c <= d.
        int c1 = cmp.IsLess(in Unsafe.Add(ref srcp, 1), in srcp) ? 1 : 0;
        int c2 = cmp.IsLess(in Unsafe.Add(ref srcp, 3), in Unsafe.Add(ref srcp, 2)) ? 1 : 0;
        ref T a = ref Unsafe.Add(ref srcp, c1);
        ref T b = ref Unsafe.Add(ref srcp, c1 ^ 1);
        ref T c = ref Unsafe.Add(ref srcp, 2 + c2);
        ref T d = ref Unsafe.Add(ref srcp, 2 + (c2 ^ 1));

        // Compare (a, c) and (b, d) to identify max/min. We're left with two
        // unknown elements, but because we are a stable sort we must know which
        // one is leftmost and which one is rightmost.
        // c3, c4 | min max unk_left unk_right
        //  0,  0 |  a   d    b         c
        //  0,  1 |  a   b    c         d
        //  1,  0 |  c   d    a         b
        //  1,  1 |  c   b    a         d
        bool c3 = cmp.IsLess(in c, in a);
        bool c4 = cmp.IsLess(in d, in b);
        ref T min = ref (c3 ? ref c : ref a);
        ref T max = ref (c4 ? ref b : ref d);
        ref T unkLeft = ref (c3 ? ref a : ref (c4 ? ref c : ref b));
        ref T unkRight = ref (c4 ? ref d : ref (c3 ? ref b : ref c));

        // Sort the last two unknown elements.
        bool c5 = cmp.IsLess(in unkRight, in unkLeft);
        ref T lo = ref (c5 ? ref unkRight : ref unkLeft);
        ref T hi = ref (c5 ? ref unkLeft : ref unkRight);

        dstp = min;
        Unsafe.Add(ref dstp, 1) = lo;
        Unsafe.Add(ref dstp, 2) = hi;
        Unsafe.Add(ref dstp, 3) = max;
    }

    /// <summary>sort4_into (small_sort.rs:245-259): sorts src[0..4] into dst[0..4] via scratch.
    /// Contract (upstream typestate/assert): src and dst must be exactly 4 elements; scratch at least 4.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Sort4Into<T, TC>(Span<T> src, Span<T> dst, Span<T> scratch, TC cmp) where TC : struct, IIsLess<T>
    {
        if (src.Length != 4 || dst.Length != 4)
            ThrowLengthContract(nameof(src), src.Length, nameof(dst), dst.Length, 4);
        Sort4Raw(ref MemoryMarshal.GetReference(src), ref MemoryMarshal.GetReference(scratch), cmp);
        scratch.Slice(0, 4).CopyTo(dst);
    }

    /// <summary>sort8_into (small_sort.rs:261-271): sorts src into dst via a 4-group network plus
    /// final merge. Contract (upstream assert at small_sort.rs:122, dst.len() == N): src and dst must
    /// be exactly 8 elements; scratch at least 8.</summary>
    internal static void Sort8Into<T, TC>(Span<T> src, Span<T> dst, Span<T> scratch, TC cmp) where TC : struct, IIsLess<T>
    {
        if (src.Length != 8 || dst.Length != 8)
            ThrowLengthContract(nameof(src), src.Length, nameof(dst), dst.Length, 8);
        var sort = new Pow2SmallSort<T>(src, scratch.Slice(0, 8));
        sort.SortGroupsOfFourFromSrcToDst(8, cmp);
        sort.FinalMergeFromDstInto(8, dst, cmp);
    }

    /// <summary>sort16_into (small_sort.rs:273-286): sorts src into dst via 4-group network, double
    /// merge, final merge. Contract: src and dst must be exactly 16 elements; scratch at least 32
    /// (upstream splits scratch at 16 — the second half is the ping-pong buffer).</summary>
    internal static void Sort16Into<T, TC>(Span<T> src, Span<T> dst, Span<T> scratch, TC cmp) where TC : struct, IIsLess<T>
    {
        if (src.Length != 16 || dst.Length != 16)
            ThrowLengthContract(nameof(src), src.Length, nameof(dst), dst.Length, 16);
        var sort = new Pow2SmallSort<T>(src, scratch.Slice(0, 16));
        sort.SortGroupsOfFourFromSrcToDst(16, cmp);
        sort.SetNewDst(scratch.Slice(16, 16));
        sort.DoubleMergeFromSrcToDst(16, cmp);
        sort.FinalMergeFromDstInto(16, dst, cmp);
    }

    /// <summary>sort32_into (small_sort.rs:288-304): sorts src into dst via 4-group network, two
    /// double merges, swap, final double merge, final merge. Contract: src and dst must be exactly
    /// 32 elements; scratch at least 64.</summary>
    internal static void Sort32Into<T, TC>(Span<T> src, Span<T> dst, Span<T> scratch, TC cmp) where TC : struct, IIsLess<T>
    {
        if (src.Length != 32 || dst.Length != 32)
            ThrowLengthContract(nameof(src), src.Length, nameof(dst), dst.Length, 32);
        var sort = new Pow2SmallSort<T>(src, scratch.Slice(0, 32));
        sort.SortGroupsOfFourFromSrcToDst(32, cmp);
        sort.SetNewDst(scratch.Slice(32, 32));
        sort.DoubleMergeFromSrcToDst(16, cmp);
        sort.DoubleMergeFromSrcToDst(16, cmp);
        sort.SwapSrcDst();
        sort.DoubleMergeFromSrcToDst(32, cmp);
        sort.FinalMergeFromDstInto(32, dst, cmp);
    }

    /// <summary>assert_abort (upstream util.rs): argument-contract failure aborts. Mirrors the
    /// upstream asserts that MutSlice typestate usually makes unrepresentable (dst.len() == N,
    /// src.len() == N — small_sort.rs:122, 62-70).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowLengthContract(string srcName, int srcLen, string dstName, int dstLen, int n) =>
        throw new ArgumentException(
            $"{srcName}.Length ({srcLen}) or {dstName}.Length ({dstLen}) violates the length contract: both must be exactly {n}.");

    /// <summary>partial_sort_into (small_sort.rs:361-415): sorts the largest pow2 chunk of src
    /// (32/16/8/4/2/1 elements) into dst and returns the chunk length. dst len must be
    /// at least min(src.len, 32) — holds for both call shapes in block_insertion_sort.</summary>
    private static int PartialSortInto<T, TC>(Span<T> src, Span<T> dst, TC cmp) where TC : struct, IIsLess<T>
    {
        // with_stack_scratch::<64> (mut_slice.rs:398-409): true stack storage for value
        // types without GC references; types containing references cannot live in
        // untracked stack bytes (the GC would not update them on compaction), so they
        // rent from the shared pool instead.
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            T[] rented = ArrayPool<T>.Shared.Rent(64);
            try
            {
                return PartialSortIntoCore(src, dst, rented.AsSpan(0, 64), cmp);
            }
            finally
            {
                ArrayPool<T>.Shared.Return(rented, clearArray: true);
            }
        }
        Span<byte> raw = stackalloc byte[64 * Unsafe.SizeOf<T>()];
        return PartialSortIntoCore(src, dst,
            MemoryMarshal.CreateSpan(ref Unsafe.As<byte, T>(ref MemoryMarshal.GetReference(raw)), 64), cmp);
    }

    private static int PartialSortIntoCore<T, TC>(Span<T> src, Span<T> dst, Span<T> scratch, TC cmp) where TC : struct, IIsLess<T>
    {
        int n = src.Length;
        if (n >= 32)
        {
            Sort32Into(src.Slice(0, 32), dst.Slice(0, 32), scratch, cmp);
            return 32;
        }
        if (n >= 16)
        {
            Sort16Into(src.Slice(0, 16), dst.Slice(0, 16), scratch.Slice(0, 32), cmp); // Yes, 32.
            return 16;
        }
        if (n >= 8)
        {
            Sort8Into(src.Slice(0, 8), dst.Slice(0, 8), scratch.Slice(0, 8), cmp);
            return 8;
        }
        if (n >= 4)
        {
            Sort4Into(src.Slice(0, 4), dst.Slice(0, 4), scratch.Slice(0, 4), cmp);
            return 4;
        }
        if (n >= 2)
        {
            ref T first = ref MemoryMarshal.GetReference(src);
            ref T second = ref Unsafe.Add(ref first, 1);
            if (cmp.IsLess(in second, in first))
            {
                var tmp = first;
                first = second;
                second = tmp;
            }
            dst[0] = src[0];
            dst[1] = src[1];
            return 2;
        }
        dst[0] = src[0];
        return 1;
    }

    /// <summary>Pow2SmallSort (small_sort.rs:55-243): helper for sorting small 2^n sized
    /// arrays, ping-ponging between a source and a destination buffer. The Rust Drop impl
    /// is panic-recovery only (success paths forget self), so it has no C# port; an
    /// exception from cmp leaves the buffers in an unspecified state where elements may be
    /// duplicated or lost (copies out of one side are not undone). Buffers are tracked as
    /// (buffer, position) pairs — cur_src = _srcBuf[_srcPos..], cur_dst = _dstBuf[_dstPos..]
    /// — with both buffers exactly the full N elements, mirroring MutSlice begin/end.</summary>
    private ref struct Pow2SmallSort<T>
    {
        private readonly Span<T> _origSrc; // whole original source region, len N
        private Span<T> _srcBuf;
        private int _srcPos;
        private Span<T> _dstBuf;
        private int _dstPos;

        /// <summary>new (small_sort.rs:62-70): src.len must equal dst.len (both N).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal Pow2SmallSort(Span<T> src, Span<T> dst)
        {
            _origSrc = src;
            _srcBuf = src;
            _srcPos = 0;
            _dstBuf = dst;
            _dstPos = 0;
        }

        /// <summary>set_new_dst (small_sort.rs:74-84): the current source must be exhausted;
        /// the old destination rewound to its start becomes the new source.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void SetNewDst(Span<T> newDst)
        {
            _srcBuf = _dstBuf;
            _srcPos = _dstBuf.Length - _origSrc.Length;
            _dstBuf = newDst;
            _dstPos = 0;
        }

        /// <summary>swap_src_dst (small_sort.rs:86-97): the current source must be exhausted;
        /// exchange the roles of the two buffers.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void SwapSrcDst()
        {
            Span<T> newDst = _srcBuf.Slice(_srcPos - _origSrc.Length);
            SetNewDst(newDst);
        }

        /// <summary>sort_groups_of_four_from_src_to_dst (small_sort.rs:99-113): sort n/4
        /// four-element arrays from src into dst (n % 4 == 0, n == cur_src.len).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void SortGroupsOfFourFromSrcToDst<TC>(int n, TC cmp) where TC : struct, IIsLess<T>
        {
            ref T s = ref MemoryMarshal.GetReference(_srcBuf);
            ref T d = ref MemoryMarshal.GetReference(_dstBuf);
            int sp = _srcPos, dp = _dstPos;
            for (int i = 0; i < n >> 2; i++)
            {
                Sort4Raw(ref Unsafe.Add(ref s, sp), ref Unsafe.Add(ref d, dp), cmp);
                sp += 4;
                dp += 4;
            }
            _srcPos = sp;
            _dstPos = dp;
        }

        /// <summary>final_merge_from_dst_into (small_sort.rs:116-166): merge two n/2-element
        /// runs into dst (n % 4 == 0, dst.len == n). Upstream's non-Copy branch uses
        /// imbalance-guarded merge ops; every C# type is bitwise-copyable with no drop
        /// glue (may_call_ord_on_copy() == true), so only that branch is ported.</summary>
        internal void FinalMergeFromDstInto<TC>(int n, Span<T> dst, TC cmp) where TC : struct, IIsLess<T>
        {
            int k = n >> 1;
            SetNewDst(dst);

            Span<T> backupSrc = _srcBuf.Slice(_srcPos);
            Span<T> backupDst = _dstBuf.Slice(_dstPos);
            var merge = new BranchlessMergeState<T>(_srcBuf.Slice(_srcPos), _dstBuf.Slice(_dstPos));
            for (int i = 0; i < k; i++)
            {
                merge.MergeOneAtBegin(cmp);
                merge.MergeOneAtEnd(cmp);
            }
            if (!merge.SymmetricMergeSuccessful)
            {
                // Bad comparison operator, just copy over input.
                backupSrc.CopyTo(backupDst);
            }
            _srcPos += n;
            _dstPos += n;
        }

        /// <summary>double_merge_from_src_to_dst (small_sort.rs:168-225): merge four n/4-element
        /// runs into two n/2-element runs (n % 8 == 0, n &lt;= cur_src.len).</summary>
        [MethodImpl(MethodImplOptions.NoInlining)] // upstream #[inline(never)]
        internal void DoubleMergeFromSrcToDst<TC>(int n, TC cmp) where TC : struct, IIsLess<T>
        {
            int k = n >> 2;

            Span<T> backupSrc = _srcBuf.Slice(_srcPos, n);
            Span<T> backupDst = _dstBuf.Slice(_dstPos, n);
            var leftMerge = new BranchlessMergeState<T>(
                _srcBuf.Slice(_srcPos, 2 * k), _dstBuf.Slice(_dstPos, 2 * k));
            var rightMerge = new BranchlessMergeState<T>(
                _srcBuf.Slice(_srcPos + 2 * k, 2 * k), _dstBuf.Slice(_dstPos + 2 * k, 2 * k));
            for (int i = 0; i < k; i++)
            {
                leftMerge.MergeOneAtBegin(cmp);
                rightMerge.MergeOneAtBegin(cmp);
                leftMerge.MergeOneAtEnd(cmp);
                rightMerge.MergeOneAtEnd(cmp);
            }
            if (!leftMerge.SymmetricMergeSuccessful || !rightMerge.SymmetricMergeSuccessful)
            {
                // Bad comparison operator, just copy over input.
                backupSrc.CopyTo(backupDst);
            }
            _srcPos += n;
            _dstPos += n;
        }
    }

    /// <summary>Minimal port of BranchlessMergeState (branchless_merge.rs:96-303) — the
    /// disjoint symmetric-merge subset small_sort.rs uses (new_disjoint plus the four
    /// one-at-a-time ops; only the unguarded Copy-type variants apply in C#). left and
    /// right are adjacent halves of one buffer [left | right]; dst is disjoint from both.
    /// Indices are plain ints so they may cross for a bad comparison operator, exactly as
    /// upstream's raw pointers do; all reads/writes stay inside the two spans regardless.</summary>
    private ref struct BranchlessMergeState<T>
    {
        private readonly Span<T> _src;  // [left | right], len 2k
        private readonly Span<T> _dst;  // len 2k
        private int _leftBegin, _leftEnd;
        private int _rightBegin, _rightEnd;
        private int _dstBegin, _dstEnd;

        /// <summary>new_disjoint (branchless_merge.rs:132-140): left = src[0..k], right = src[k..2k].</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal BranchlessMergeState(Span<T> src, Span<T> dst)
        {
            int k = src.Length >> 1;
            _src = src;
            _dst = dst;
            _leftBegin = 0;
            _leftEnd = k;
            _rightBegin = k;
            _rightEnd = src.Length;
            _dstBegin = 0;
            _dstEnd = dst.Length;
        }

        /// <summary>branchless_merge_one_at_begin (branchless_merge.rs:176-196): merge the
        /// smaller of left/right's front element to dst's front (ties towards left).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void MergeOneAtBegin<TC>(TC cmp) where TC : struct, IIsLess<T>
        {
            // Upstream note: adding 1 and subtracting right_less gave significantly faster
            // codegen than adding !right_less. Kept as (1 - rightLess) increments.
            ref T s = ref MemoryMarshal.GetReference(_src);
            ref T d = ref MemoryMarshal.GetReference(_dst);
            int lb = _leftBegin, rb = _rightBegin;
            bool rightLess = cmp.IsLess(in Unsafe.Add(ref s, rb), in Unsafe.Add(ref s, lb));
            Unsafe.Add(ref d, _dstBegin) = rightLess ? Unsafe.Add(ref s, rb) : Unsafe.Add(ref s, lb);
            _dstBegin++;
            _leftBegin = lb + (rightLess ? 0 : 1);
            _rightBegin = rb + (rightLess ? 1 : 0);
        }

        /// <summary>branchless_merge_one_at_end (branchless_merge.rs:229-245): merge the
        /// larger of left/right's back element to dst's back (ties towards right).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void MergeOneAtEnd<TC>(TC cmp) where TC : struct, IIsLess<T>
        {
            ref T s = ref MemoryMarshal.GetReference(_src);
            ref T d = ref MemoryMarshal.GetReference(_dst);
            int le = _leftEnd, re = _rightEnd;
            bool rightLess = cmp.IsLess(in Unsafe.Add(ref s, re - 1), in Unsafe.Add(ref s, le - 1));
            _dstEnd--;
            Unsafe.Add(ref d, _dstEnd) = rightLess ? Unsafe.Add(ref s, le - 1) : Unsafe.Add(ref s, re - 1);
            _leftEnd = le - (rightLess ? 1 : 0);
            _rightEnd = re - (rightLess ? 0 : 1);
        }

        /// <summary>symmetric_merge_successful (branchless_merge.rs:280-284): left_begin ==
        /// left_end implies right is exhausted too; only an invalid comparison operator
        /// can violate this.</summary>
        internal bool SymmetricMergeSuccessful => _leftBegin == _leftEnd;
    }

    /// <summary>BlockInserter (small_sort.rs:308-349): inserts the sorted run src into el
    /// through a moving hole that starts directly after the sorted prefix (el[sortedLen]).
    /// The Rust Drop impl is panic-recovery only — insert forgets self on success — so it
    /// has no C# port (no finalizers, no Dispose).</summary>
    private ref struct BlockInserter<T>
    {
        private readonly Span<T> _src;   // sorted run, e.g. scratch[0..len]
        private readonly Span<T> _el;    // dst region lives inside el: [sorted prefix | hole]
        private readonly int _hole;      // hole_begin, index into el
        private int _dstEnd;             // dst end, index into el

        /// <summary>new (small_sort.rs:315-328): dst = el[0..sortedLen), dst_hole = el[sortedLen..sortedLen+src.len).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal BlockInserter(Span<T> src, Span<T> el, int sortedLen)
        {
            _src = src;
            _el = el;
            _hole = sortedLen;
            _dstEnd = sortedLen + src.Length;
        }

        /// <summary>insert (small_sort.rs:330-348): binary-free insertion from the largest
        /// src element down, shifting sorted elements right through the hole.</summary>
        internal void Insert<TC>(TC cmp) where TC : struct, IIsLess<T>
        {
            ref T e = ref MemoryMarshal.GetReference(_el);
            ref T s = ref MemoryMarshal.GetReference(_src);
            int srcEnd = _src.Length;
            int hole = _hole, dstEnd = _dstEnd;
            while (srcEnd > 0)
            {
                var p = Unsafe.Add(ref s, srcEnd - 1);
                while (hole != 0 && cmp.IsLess(in p, in Unsafe.Add(ref e, hole - 1)))
                {
                    hole--;
                    dstEnd--;
                    Unsafe.Add(ref e, dstEnd) = Unsafe.Add(ref e, hole);
                }
                srcEnd--;
                dstEnd--;
                Unsafe.Add(ref e, dstEnd) = p;
            }
        }
    }

    /// <summary>block_insertion_sort (small_sort.rs:417-443): sort el by sorting a pow2
    /// chunk at a time into scratch and inserting it into the sorted prefix through a hole.</summary>
    internal static void BlockInsertionSort<T, TC>(Span<T> el, TC cmp) where TC : struct, IIsLess<T>
    {
        int n = el.Length;
        if (n <= 1)
        {
            return;
        }

        // with_stack_scratch::<32> (mut_slice.rs:398-409) — see PartialSortInto.
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            T[] rented = ArrayPool<T>.Shared.Rent(32);
            try
            {
                BlockInsertionSortCore(el, cmp, rented.AsSpan(0, 32));
            }
            finally
            {
                ArrayPool<T>.Shared.Return(rented, clearArray: true);
            }
        }
        else
        {
            Span<byte> raw = stackalloc byte[32 * Unsafe.SizeOf<T>()];
            BlockInsertionSortCore(el, cmp,
                MemoryMarshal.CreateSpan(ref Unsafe.As<byte, T>(ref MemoryMarshal.GetReference(raw)), 32));
        }
    }

    private static void BlockInsertionSortCore<T, TC>(Span<T> el, TC cmp, Span<T> scratch) where TC : struct, IIsLess<T>
    {
        int n = el.Length;
        int numSorted = PartialSortInto(el, el, cmp);

        while (numSorted < n)
        {
            Span<T> unsorted = el.Slice(numSorted);
            int inLen = PartialSortInto(unsorted, scratch, cmp);
            numSorted += inLen;
            new BlockInserter<T>(scratch.Slice(0, inLen), el, numSorted - inLen).Insert(cmp);
        }
    }
}
