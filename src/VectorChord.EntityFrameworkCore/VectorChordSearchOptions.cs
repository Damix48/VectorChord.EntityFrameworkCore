namespace VectorChord.EntityFrameworkCore;

public sealed class VectorChordRqSearchOptions
{
    public int[]? Probes { get; set; }
    public double? Epsilon { get; set; }
    public bool? Prefilter { get; set; }
    public int? MaxScanTuples { get; set; }
    public int? MaxSimRefine { get; set; }
    public int? MaxSimThreshold { get; set; }
}

public sealed class VectorChordGraphSearchOptions
{
    public int? EfSearch { get; set; }
    public int? BeamSearch { get; set; }
    public int? MaxScanTuples { get; set; }
}
