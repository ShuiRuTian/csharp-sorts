using System.Linq;
using Sorts.TestData;
using Xunit;

public class DataGenTests
{
    [Fact]
    public void DeterministicAcrossCalls()
    {
        Assert.Equal(DataGen.Ints(Distribution.Random, 1000, 7), DataGen.Ints(Distribution.Random, 1000, 7));
    }

    [Fact]
    public void AscendingIsAscending() =>
        Assert.Equal(Enumerable.Range(0, 500).ToArray(), DataGen.Ints(Distribution.Ascending, 500, 1));

    [Fact]
    public void FewUniqueHasFourDistinctValues() =>
        Assert.Equal(4, DataGen.Ints(Distribution.FewUnique, 10_000, 3).Distinct().Count());

    [Fact]
    public void PairPayloadsAreInputOrderStamps() =>
        Assert.Equal(Enumerable.Range(0, 300), DataGen.Pairs(Distribution.RandomD20, 300, 5).Select(p => p.Payload));

    [Theory]
    [InlineData(Distribution.Random)]
    [InlineData(Distribution.Zipfian)]
    public void NonEmptyForAllTypes(Distribution d)
    {
        Assert.NotEmpty(DataGen.Ints(d, 100, 9));
        Assert.NotEmpty(DataGen.Doubles(d, 100, 9));
        Assert.NotEmpty(DataGen.Strings(d, 100, 9));
        Assert.NotEmpty(DataGen.Pairs(d, 100, 9));
    }
}
