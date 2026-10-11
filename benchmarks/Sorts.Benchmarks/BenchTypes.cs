using System;

namespace Sorts.Benchmarks;

/// <summary>24-byte unmanaged key+payload record (sequential layout: 4B Key + 4B
/// padding + 8B A + 4B B + 4B tail padding). Comparison is by Key only; the long/int
/// payload makes every copy and every swap more expensive than a bare int.</summary>
public readonly record struct Struct16(int Key, long A, int B) : IComparable<Struct16>
{
    public int CompareTo(Struct16 other) => Key.CompareTo(other.Key);
}

/// <summary>128-byte unmanaged record: int Key + 15 junk longs (4 + 120 bytes, padded
/// to 128 by the 8-byte alignment). CompareTo is by Key only — the payload exists
/// purely to make element copies expensive. Being unmanaged (no reference fields),
/// this type exercises the sorts' memmove-heavy/unaligned-copy paths, much like the
/// freeze-style networking of large elements.</summary>
public readonly record struct Struct128(
    int Key,
    long P0, long P1, long P2, long P3, long P4, long P5, long P6, long P7,
    long P8, long P9, long P10, long P11, long P12, long P13, long P14)
    : IComparable<Struct128>
{
    public int CompareTo(Struct128 other) => Key.CompareTo(other.Key);
}

