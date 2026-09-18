using System;
using System.Collections.Generic;
using Sorts;
using Xunit;

public class PowersortTests
{
    [Fact]
    public void ScaleFactorIsCeilOfTwoPow62OverN()
    {
        // ceil(2^62 / n): exact for powers of two, +1 rounding otherwise.
        Assert.Equal(1UL << 62, Powersort.MergeTreeScaleFactor(1));
        Assert.Equal(1UL << 32, Powersort.MergeTreeScaleFactor(1 << 30));
        // n = 100: ceil(4611686018427387904 / 100) = 46116860184273880.
        Assert.Equal(46116860184273880UL, Powersort.MergeTreeScaleFactor(100));
        // n = 1000: ceil(2^62 / 1000) = 4611686018427388.
        Assert.Equal(4611686018427388UL, Powersort.MergeTreeScaleFactor(1000));
    }

    [Fact]
    public void PowersortDepthMatchesRust()
    {
        // n = 100, runs (0,40), (40,40), (40,60); scale f = ceil(2^62/100)
        //   = 46116860184273880 = 0x0A3D70A3D70A3D8.
        // Node between runs 1|2: left=0, mid=40, right=80.
        //   x = (0+40)*f = 0x19999999999999C0, y = (40+80)*f = 0x4CCCCCCCCCCCCD40.
        //   x ^ y has MSB at bit 62 -> CLZ = 1.
        // Node between runs 2|3: left=40, mid=80, right=100.
        //   x = (40+80)*f = 0x4CCCCCCCCCCCCD40, y = (80+100)*f = 0x7333333333333360.
        //   MSB difference at bit 61 -> CLZ = 2.
        ulong f = Powersort.MergeTreeScaleFactor(100);
        Assert.Equal(46116860184273880UL, f);
        Assert.Equal((byte)1, Powersort.MergeTreeDepth(0, 40, 80, f));
        Assert.Equal((byte)2, Powersort.MergeTreeDepth(40, 80, 100, f));
    }

    [Fact]
    public void StackInvariantHoldsOnRandomPartition()
    {
        // Random partition of [0,1000) into 20 runs. The powersort comment block
        // proves adjacent nodes sharing a run have distinct depths; the merge-stack
        // discipline (pop while top >= d, then push) then keeps the stack's desired
        // depths strictly ascending from bottom to top — the invariant that bounds
        // the stack at 64 entries. (Raw depths along i are not monotonic; the
        // strict ascent is a property of the stack, checked here at every step.)
        var rng = new Random(1234);
        var cuts = new HashSet<int>();
        while (cuts.Count < 19) cuts.Add(rng.Next(1, 1000));
        var b = new List<int> { 0 };
        b.AddRange(cuts);
        b.Sort();
        b.Add(1000);

        ulong f = Powersort.MergeTreeScaleFactor(1000);
        var stack = new List<byte>();
        for (int i = 0; i + 2 < b.Count; i++)
        {
            byte d = Powersort.MergeTreeDepth(b[i], b[i + 1], b[i + 2], f);
            if (i > 0)
            {
                byte prev = Powersort.MergeTreeDepth(b[i - 1], b[i], b[i + 1], f);
                Assert.NotEqual(prev, d); // CLZ(x^y) != CLZ(y^z), the proven property.
            }
            while (stack.Count > 0 && stack[^1] >= d) stack.RemoveAt(stack.Count - 1);
            stack.Add(d);
            for (int j = 1; j < stack.Count; j++)
                Assert.True(stack[j - 1] < stack[j], $"stack not ascending at run {i}");
            Assert.True(stack.Count <= 64);
        }
        Assert.Equal(20, b.Count - 1);
    }
}
