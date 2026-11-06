using System.Globalization;

namespace VectorChord.EntityFrameworkCore.Internal;

/// <summary>
/// Reads and writes the subset of the <c>options</c> TOML that the model builder emits.
/// Unknown lines are preserved verbatim so a rebuild keeps options this library does not model.
/// </summary>
internal static class VectorChordRqOptionsToml
{
    private const string Residual = "residual_quantization";
    private const string Lists = "build.internal.lists";
    private const string Threads = "build.internal.build_threads";
    private const string Spherical = "build.internal.spherical_centroids";

    public static VectorChordRqIndexOptions Parse(string? toml)
    {
        var options = new VectorChordRqIndexOptions();
        foreach (var line in Lines(toml))
        {
            var key = KeyOf(line);
            var value = ValueOf(line);
            try
            {
                switch (key)
                {
                    case Residual: options.ResidualQuantization = bool.Parse(value); break;
                    case Spherical: options.SphericalCentroids = bool.Parse(value); break;
                    case Threads: options.BuildThreads = int.Parse(value, CultureInfo.InvariantCulture); break;
                    case Lists: options.Lists = ParseLists(value); break;
                }
            }
            catch (Exception exception) when (exception is FormatException or OverflowException)
            {
                throw new InvalidOperationException($"Invalid VectorChord index option: {line}.", exception);
            }
        }
        return options;
    }

    public static string Merge(string? existing, VectorChordRqIndexOptions options)
    {
        var overrides = new List<(string Key, string Line)>();
        if (options.ResidualQuantization is { } residual)
            overrides.Add((Residual, Residual + " = " + (residual ? "true" : "false")));
        if (options.Lists is { } lists)
            overrides.Add((Lists, Lists + " = [" + string.Join(", ", lists) + "]"));
        if (options.BuildThreads is { } threads)
            overrides.Add((Threads, Threads + " = " + threads.ToString(CultureInfo.InvariantCulture)));
        if (options.SphericalCentroids is { } spherical)
            overrides.Add((Spherical, Spherical + " = " + (spherical ? "true" : "false")));

        var lines = new List<string>();
        foreach (var line in Lines(existing))
        {
            var key = KeyOf(line);
            var index = overrides.FindIndex(o => o.Key == key);
            if (index < 0)
            {
                lines.Add(line);
                continue;
            }
            lines.Add(overrides[index].Line);
            overrides.RemoveAt(index);
        }
        lines.AddRange(overrides.Select(o => o.Line));
        return string.Join("\n", lines);
    }

    // shortcut: only dotted keys are merged; section-style TOML ([build.internal]) is kept verbatim and
    // would duplicate a key. Upgrade if callers hand-write section-style options.
    private static IEnumerable<string> Lines(string? toml)
        => (toml ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.StartsWith('#') && !line.StartsWith('['));

    private static string? KeyOf(string line)
    {
        var separator = line.IndexOf('=');
        return separator < 0 ? null : line[..separator].Trim();
    }

    private static string ValueOf(string line)
    {
        var separator = line.IndexOf('=');
        return separator < 0 ? "" : line[(separator + 1)..].Trim();
    }

    private static int[] ParseLists(string value)
    {
        var inner = value.Trim();
        if (inner.StartsWith('[')) inner = inner[1..];
        if (inner.EndsWith(']')) inner = inner[..^1];
        return inner.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(v => int.Parse(v, CultureInfo.InvariantCulture))
            .ToArray();
    }
}
