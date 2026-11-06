namespace VectorChord.EntityFrameworkCore;

public enum DistanceMetric
{
    L2,
    Cosine,
    NegativeInnerProduct,
    MaxSim
}

public sealed class VectorChordRqIndexOptions
{
    public int[]? Lists { get; set; }
    public int? BuildThreads { get; set; }
    public bool? ResidualQuantization { get; set; }
    public bool? SphericalCentroids { get; set; }
    public int[]? Probes { get; set; }
    public double? Epsilon { get; set; }
    public int? MaxSimRefine { get; set; }
    public int? MaxSimThreshold { get; set; }
}

public sealed class VectorChordGraphIndexOptions
{
    public int? Bits { get; set; }
    public int? M { get; set; }
    public int? EfConstruction { get; set; }
    public int? EfSearch { get; set; }
}

/// <summary>
/// Sizing guidance from the VectorChord docs (indexing.html#tuning and partitioning-tuning.html).
/// </summary>
public static class VectorChordRqTuning
{
    /// <summary>
    /// Recommended <c>build.internal.lists</c> for a table with <paramref name="rows"/> vectors.
    /// Returns <c>0</c> below 100k rows, meaning "do not partition" (<c>lists = []</c>).
    /// </summary>
    /// <remarks>
    /// The docs' table gives a single-level list count. For a hierarchical index, split it across
    /// levels so the product of the levels is this value, e.g. <c>[400, 160000]</c> for 100M rows.
    /// </remarks>
    public static int RecommendedLists(long rows)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rows);
        return rows switch
        {
            < 100_000 => 0,
            < 2_000_000 => (int)(rows / 500),
            < 50_000_000 => (int)Math.Ceiling(4 * Math.Sqrt(rows)),
            _ => (int)Math.Ceiling(8 * Math.Sqrt(rows)),
        };
    }
}
