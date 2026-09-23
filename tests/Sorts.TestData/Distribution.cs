namespace Sorts.TestData;

public enum Distribution
{
    Random, Ascending, Descending, Sawtooth, OrganPipe, RandomD20,
    RandomP5, RandomS95, Zipfian, AllEqual, FewUnique, RandomMerge,
    // Near-sorted family (the most common real small-array shape: sorted data
    // with a few displaced/new elements). Fixed-k so they stay meaningful at
    // small N (percentage-based perturbations collapse to "fully sorted").
    SortedSwap1, SortedSwap3, RandomSnl,
    // Structural + duplicates / adversarial probes.
    Plateau, Stagger, Median3Killer
}
