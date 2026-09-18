// Ported from https://github.com/orlp/driftsort, MIT OR Apache-2.0, by Orson Peters & Lukas Bergdoll.
// C# port 2026 — merge.rs: the stable merge of two sorted runs with an n/2 scratch buffer.
//
// MergeState's Drop impl (merge.rs:124-136) doubles as panic recovery and as the
// completion of the merge — whatever remains of the shorter run lands in the hole in v.
// The port runs that copy unconditionally after the merge loops (Drain), which is the
// same success-path behavior; an exception from the comparator leaves the input in an
// unspecified state, the established policy for these ports.
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Sorts;

/// <summary>DriftSort merge kernel (upstream merge.rs): merges the non-decreasing runs
/// v[..leftLen] and v[leftLen..] in place, storing the result in v. scratch is temporary
/// storage: the upstream contract supplies at least v.Length - v.Length / 2 elements,
/// which always fits the shorter run the algorithm copies out. An empty-sided request
/// (leftLen &lt;= 0 or leftLen &gt;= v.Length) is a no-op, as upstream returns early
/// (merge.rs:15-17); a scratch violation throws at this seam where upstream silently
/// returns (merge.rs:15) — reachable never by a conforming caller, and silently leaving
/// the runs unmerged would corrupt the sort.</summary>
internal static class DriftMerge
{
    /// <summary>merge (merge.rs:7-65): copies the shorter run into scratch (buf), then
    /// traces it against the longer run forwards (merge_up, when the left run is shorter)
    /// or backwards (merge_down), copying the lesser/greater element into v. Whichever
    /// run the trace exhausts first ends the loop; the remaining shorter-run elements are
    /// copied into the gap in v by Drain.</summary>
    internal static void Merge<T, TC>(Span<T> v, Span<T> scratch, int leftLen, TC cmp) where TC : struct, IIsLess<T>
    {
        int len = v.Length;
        if (leftLen <= 0 || leftLen >= len)
            return; // One side empty: nothing to merge (merge.rs:15-17).
        int rightLen = len - leftLen;
        if (scratch.Length < Math.Min(leftLen, rightLen))
            ThrowScratchTooSmall(scratch.Length, Math.Min(leftLen, rightLen));

        ref T vBase = ref MemoryMarshal.GetReference(v);
        ref T buf = ref MemoryMarshal.GetReference(scratch);

        // The merge process first copies the shorter run into buf. Then it traces the
        // newly copied run and the longer run forwards (or backwards), comparing their
        // next unconsumed elements and copying the lesser (or greater) one into v.
        //
        // As soon as the shorter run is fully consumed, the process is done. If the
        // longer run gets consumed first, then we must copy whatever is left of the
        // shorter run into the remaining gap in v.
        //
        // Intermediate state of the process is always tracked by the MergeState offsets,
        // which serve two purposes:
        //  1. Describe the remaining shorter-run range [start, end) in buf.
        //  2. Fill the remaining gap in v at dst if the longer run gets consumed first.
        bool leftIsShorter = leftLen <= rightLen;
        int saveOff = leftIsShorter ? 0 : leftLen;
        int saveLen = leftIsShorter ? leftLen : rightLen;
        for (int i = 0; i < saveLen; i++)
            Unsafe.Add(ref buf, i) = Unsafe.Add(ref vBase, saveOff + i);

        var state = new MergeState<T>(ref buf, ref vBase, saveLen, saveOff);
        if (leftIsShorter)
        {
            // The saved left run merges with the right run v[leftLen..len], forwards.
            state.MergeUp(leftLen, len, cmp);
        }
        else
        {
            // The saved right run merges with the left run v[0..leftLen], backwards from
            // v's end; both stop boundaries are offset 0 (v_base and buf, merge.rs:61).
            state.MergeDown(leftEnd: 0, rightEnd: 0, outEnd: len, cmp);
        }
        // Finally the state drains: any unconsumed shorter-run elements land in the hole.
        state.Drain();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowScratchTooSmall(int scratchLen, int needed) =>
        throw new ArgumentException(
            $"scratch.Length ({scratchLen}) violates the merge contract: at least {needed} elements (the shorter run; v.Length - v.Length / 2 always suffices) are required.");

    /// <summary>MergeState (merge.rs:68-136): the intermediate merge state — the remaining
    /// shorter-run range [start, end) in buf and its destination offset dst in v. Indices
    /// are plain ints relative to each buffer's base, mirroring upstream's raw pointers
    /// (they stay within their buffers for any comparator outcome, as the branchless
    /// steps advance exactly one cursor).</summary>
    private ref struct MergeState<T>
    {
        private readonly ref T _buf; // scratch base — the copied shorter run
        private readonly ref T _v;   // v base
        private int _start;          // first unconsumed saved element (buf offset)
        private int _end;            // one-past last unconsumed saved element (buf offset)
        private int _dst;            // next write position in v

        /// <summary>Construction at merge.rs:52-56: start = buf, end = buf + saveLen,
        /// dst = saveBase.</summary>
        internal MergeState(ref T buf, ref T v, int saveLen, int dstOff)
        {
            _buf = ref buf;
            _v = ref v;
            _start = 0;
            _end = saveLen;
            _dst = dstOff;
        }

        /// <summary>merge_up (merge.rs:75-95): forward branchless merge of the saved run
        /// against v[right..rightEnd] — the lesser of the two fronts (ties left) goes to
        /// v[dst], advancing exactly one cursor.</summary>
        internal void MergeUp<TC>(int right, int rightEnd, TC cmp) where TC : struct, IIsLess<T>
        {
            while (_start != _end && right != rightEnd)
            {
                bool consumeLeft = !cmp.IsLess(in Unsafe.Add(ref _v, right), in Unsafe.Add(ref _buf, _start));
                ref T pick = ref (consumeLeft
                    ? ref Unsafe.Add(ref _buf, _start)
                    : ref Unsafe.Add(ref _v, right));
                Unsafe.Add(ref _v, _dst) = pick;
                _start += consumeLeft ? 1 : 0;
                right += consumeLeft ? 0 : 1;
                _dst++;
            }
        }

        /// <summary>merge_down (merge.rs:97-121): backward branchless merge from v's end —
        /// the greater of the two backs (ties to the back, keeping stability) goes to
        /// v[outIdx], retreating exactly one cursor. leftEnd and rightEnd are the stop
        /// boundaries (v_base and buf respectively, both offset 0).</summary>
        internal void MergeDown<TC>(int leftEnd, int rightEnd, int outEnd, TC cmp) where TC : struct, IIsLess<T>
        {
            int outIdx = outEnd;
            while (true)
            {
                int left = _dst - 1;  // last unconsumed left element (v offset)
                int right = _end - 1; // last unconsumed saved element (buf offset)
                outIdx--;
                bool consumeLeft = cmp.IsLess(in Unsafe.Add(ref _buf, right), in Unsafe.Add(ref _v, left));
                ref T pick = ref (consumeLeft
                    ? ref Unsafe.Add(ref _v, left)
                    : ref Unsafe.Add(ref _buf, right));
                Unsafe.Add(ref _v, outIdx) = pick;
                _dst = left + (consumeLeft ? 0 : 1);
                _end = right + (consumeLeft ? 1 : 0);
                if (_dst == leftEnd || _end == rightEnd)
                    break;
            }
        }

        /// <summary>Drop (merge.rs:124-136): copies the remaining saved elements
        /// buf[start..end] into v[dst..], filling the gap the merge left behind.</summary>
        internal void Drain()
        {
            int remaining = _end - _start;
            for (int i = 0; i < remaining; i++)
                Unsafe.Add(ref _v, _dst + i) = Unsafe.Add(ref _buf, _start + i);
        }
    }
}
