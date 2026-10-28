using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pgvector;
using VectorChord.EntityFrameworkCore.Internal;

namespace VectorChord.EntityFrameworkCore;

public static class VectorChordIndexBuilderExtensions
{
    internal const string MetricAnnotation = "VectorChord:DistanceMetric";

    public static IndexBuilder HasVectorChordRq(this IndexBuilder builder, DistanceMetric metric,
        Action<VectorChordRqIndexOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var options = new VectorChordRqIndexOptions();
        configure?.Invoke(options);
        VectorOptionValidation.Range(options.BuildThreads, 1, 255, nameof(options.BuildThreads));
        VectorOptionValidation.Probes(options.Probes);
        VectorOptionValidation.Range(options.Epsilon, 0, 4, nameof(options.Epsilon));
        VectorOptionValidation.Range(options.MaxSimRefine, 0, int.MaxValue, nameof(options.MaxSimRefine));
        VectorOptionValidation.Range(options.MaxSimThreshold, 0, int.MaxValue, nameof(options.MaxSimThreshold));
        if (options.Lists is { } lists)
        {
            if (lists.Length > 8 || lists.Any(v => v < 1 || v > 1 << 24) || !lists.SequenceEqual(lists.Order()))
                throw new ArgumentException("Lists must contain at most eight ascending values in [1, 16777216].");
            if (options.Probes is { } probes && probes.Length != lists.Length)
                throw new ArgumentException("Probes must have the same number of levels as Lists.");
        }

        Configure(builder, "vchordrq", metric);
        var toml = new List<string>();
        if (options.ResidualQuantization is { } residual)
            toml.Add("residual_quantization = " + (residual ? "true" : "false"));
        if (options.Lists is not null)
            toml.Add("build.internal.lists = [" + string.Join(", ", options.Lists) + "]");
        if (options.BuildThreads is { } threads)
            toml.Add("build.internal.build_threads = " + threads.ToString(CultureInfo.InvariantCulture));
        if (options.SphericalCentroids is { } spherical)
            toml.Add("build.internal.spherical_centroids = " + (spherical ? "true" : "false"));
        if (toml.Count > 0)
            builder.HasStorageParameter("options", string.Join("\n", toml));
        if (options.Probes is not null)
            builder.HasStorageParameter("probes", VectorOptionValidation.FormatProbes(options.Probes));
        if (options.Epsilon is { } epsilon)
            builder.HasStorageParameter("epsilon", epsilon.ToString("R", CultureInfo.InvariantCulture));
        if (options.MaxSimRefine is { } refine)
            builder.HasStorageParameter("maxsim_refine", refine);
        if (options.MaxSimThreshold is { } threshold)
            builder.HasStorageParameter("maxsim_threshold", threshold);
        return builder;
    }

    public static IndexBuilder HasVectorChordGraph(this IndexBuilder builder, DistanceMetric metric,
        Action<VectorChordGraphIndexOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (metric == DistanceMetric.MaxSim)
            throw new ArgumentException("MaxSim indexes are supported only by vchordrq.", nameof(metric));
        var options = new VectorChordGraphIndexOptions();
        configure?.Invoke(options);
        VectorOptionValidation.Range(options.Bits, 1, 2, nameof(options.Bits));
        VectorOptionValidation.Range(options.M, 1, 512, nameof(options.M));
        VectorOptionValidation.Range(options.EfConstruction, 1, 65535, nameof(options.EfConstruction));
        VectorOptionValidation.Range(options.EfSearch, 1, 65535, nameof(options.EfSearch));
        Configure(builder, "vchordg", metric);
        var toml = new List<string>();
        if (options.Bits is { } bits) toml.Add("bits = " + bits.ToString(CultureInfo.InvariantCulture));
        if (options.M is { } m) toml.Add("m = " + m.ToString(CultureInfo.InvariantCulture));
        if (options.EfConstruction is { } ef) toml.Add("ef_construction = " + ef.ToString(CultureInfo.InvariantCulture));
        if (toml.Count > 0) builder.HasStorageParameter("options", string.Join("\n", toml));
        if (options.EfSearch is { } search) builder.HasStorageParameter("ef_search", search);
        return builder;
    }

    public static PropertyBuilder HasVectorDimensions(this PropertyBuilder builder, int dimensions)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentOutOfRangeException.ThrowIfLessThan(dimensions, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(dimensions, 65535);
        var clrType = builder.Metadata.ClrType;
        var element = clrType.IsArray ? clrType.GetElementType() : clrType;
        if (element != typeof(Vector) && element != typeof(HalfVector))
            throw new ArgumentException("Vector dimensions require a Vector or HalfVector property.", nameof(builder));
        var configured = builder.Metadata.GetColumnType();
        var storeType = QuantizedVectorTypeMapping.IsQuantized(configured)
            ? new QuantizedVectorTypeMapping(configured!, clrType).QuantizedType
            : element == typeof(HalfVector) ? "halfvec" : "vector";
        return builder.HasColumnType(storeType + "(" + dimensions.ToString(CultureInfo.InvariantCulture) + ")" +
            (clrType.IsArray ? "[]" : ""));
    }

    private static void Configure(IndexBuilder builder, string method, DistanceMetric metric)
    {
        var suffix = metric switch
        {
            DistanceMetric.L2 => "l2",
            DistanceMetric.Cosine => "cosine",
            DistanceMetric.NegativeInnerProduct => "ip",
            DistanceMetric.MaxSim => "maxsim",
            _ => throw new ArgumentOutOfRangeException(nameof(metric))
        };
        builder.HasMethod(method);
        builder.HasAnnotation(MetricAnnotation, suffix);
    }
}

internal static class VectorOptionValidation
{
    public static void Range(int? value, int min, int max, string name)
    {
        if (value is { } v && (v < min || v > max))
            throw new ArgumentOutOfRangeException(name, $"Expected a value in [{min}, {max}].");
    }

    public static void Range(double? value, double min, double max, string name)
    {
        if (value is { } v && (!double.IsFinite(v) || v < min || v > max))
            throw new ArgumentOutOfRangeException(name, $"Expected a finite value in [{min}, {max}].");
    }

    public static void Probes(int[]? values)
    {
        if (values is not null && (values.Length > 8 || values.Any(v => v < 0)))
            throw new ArgumentException("Probes must contain at most eight non-negative integers.");
    }

    public static string FormatProbes(int[] values)
        => values.Length == 0 ? " " : string.Join(",", values.Select(v => v.ToString(CultureInfo.InvariantCulture)));
}
