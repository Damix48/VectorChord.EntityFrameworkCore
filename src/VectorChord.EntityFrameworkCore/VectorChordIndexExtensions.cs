using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using VectorChord.EntityFrameworkCore.Internal;

namespace VectorChord.EntityFrameworkCore;

/// <summary>
/// Read accessors for the index configuration the model builder produced.
/// </summary>
public static class VectorChordIndexExtensions
{
    /// <summary>
    /// The RQ options <c>HasVectorChordRq</c> configured, or <see langword="null"/> for other indexes.
    /// Unset options come back as <see langword="null"/>, exactly as they were configured.
    /// </summary>
    /// <remarks>
    /// Npgsql strips index storage annotations from the runtime model, so this must be called on the
    /// design-time model: <c>db.GetService&lt;IDesignTimeModel&gt;().Model</c>.
    /// </remarks>
    public static VectorChordRqIndexOptions? GetVectorChordRqOptions(this IReadOnlyIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        if (index.GetMethod() != "vchordrq")
            return null;
        if (index.GetOperators() is null)
            throw new InvalidOperationException(
                $"Index options for {index.Name ?? "this index"} are not available on the runtime model; " +
                "read them from db.GetService<IDesignTimeModel>().Model instead.");

        var options = VectorChordRqOptionsToml.Parse(StorageParameter(index, "options"));
        if (StorageParameter(index, "probes") is { } probes)
            options.Probes = probes.Trim().Length == 0
                ? []
                : probes.Split(',', StringSplitOptions.TrimEntries)
                    .Select(p => int.Parse(p, CultureInfo.InvariantCulture)).ToArray();
        if (StorageParameter(index, "epsilon") is { } epsilon)
            options.Epsilon = double.Parse(epsilon, CultureInfo.InvariantCulture);
        if (StorageParameter(index, "maxsim_refine") is { } refine)
            options.MaxSimRefine = int.Parse(refine, CultureInfo.InvariantCulture);
        if (StorageParameter(index, "maxsim_threshold") is { } threshold)
            options.MaxSimThreshold = int.Parse(threshold, CultureInfo.InvariantCulture);
        return options;
    }

    /// <summary>
    /// The distance metric of a VectorChord index, read from its operator class, or
    /// <see langword="null"/> when the index has none.
    /// </summary>
    public static DistanceMetric? GetVectorChordDistanceMetric(this IReadOnlyIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        if (index.GetOperators() is not [var opclass])
            return null;
        var name = opclass.EndsWith("_ops", StringComparison.Ordinal) ? opclass[..^4] : opclass;
        return name.EndsWith("_cosine", StringComparison.Ordinal) ? DistanceMetric.Cosine
            : name.EndsWith("_l2", StringComparison.Ordinal) ? DistanceMetric.L2
            : name.EndsWith("_ip", StringComparison.Ordinal) ? DistanceMetric.NegativeInnerProduct
            : name.EndsWith("_maxsim", StringComparison.Ordinal) ? DistanceMetric.MaxSim
            : null;
    }

    internal static string? StorageParameter(IReadOnlyIndex index, string name)
        => index.FindAnnotation("Npgsql:StorageParameter:" + name)?.Value is { } value
            ? Convert.ToString(value, CultureInfo.InvariantCulture)
            : null;
}
