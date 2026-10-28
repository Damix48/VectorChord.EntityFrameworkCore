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
