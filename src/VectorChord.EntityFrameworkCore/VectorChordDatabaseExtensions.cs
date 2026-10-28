using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace VectorChord.EntityFrameworkCore;

public static class VectorChordDatabaseExtensions
{
    public static Task SetVectorChordRqSearchOptionsAsync(this DatabaseFacade database,
        VectorChordRqSearchOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        VectorOptionValidation.Probes(options.Probes);
        VectorOptionValidation.Range(options.Epsilon, 0, 4, nameof(options.Epsilon));
        VectorOptionValidation.Range(options.MaxScanTuples, -1, int.MaxValue, nameof(options.MaxScanTuples));
        VectorOptionValidation.Range(options.MaxSimRefine, 0, int.MaxValue, nameof(options.MaxSimRefine));
        VectorOptionValidation.Range(options.MaxSimThreshold, 0, int.MaxValue, nameof(options.MaxSimThreshold));
        var values = new Dictionary<string, string>();
        if (options.Probes is not null) values.Add("probes", VectorOptionValidation.FormatProbes(options.Probes));
        Add(values, "epsilon", options.Epsilon);
        Add(values, "max_scan_tuples", options.MaxScanTuples);
        Add(values, "maxsim_refine", options.MaxSimRefine);
        Add(values, "maxsim_threshold", options.MaxSimThreshold);
        if (options.Prefilter is { } prefilter) values.Add("prefilter", prefilter ? "on" : "off");
        return SetOptions(database, "vchordrq", values, cancellationToken);
    }

    public static Task SetVectorChordGraphSearchOptionsAsync(this DatabaseFacade database,
        VectorChordGraphSearchOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        VectorOptionValidation.Range(options.EfSearch, 1, 65535, nameof(options.EfSearch));
        VectorOptionValidation.Range(options.BeamSearch, 1, 65535, nameof(options.BeamSearch));
        VectorOptionValidation.Range(options.MaxScanTuples, -1, int.MaxValue, nameof(options.MaxScanTuples));
        var values = new Dictionary<string, string>();
        Add(values, "ef_search", options.EfSearch);
        Add(values, "beam_search", options.BeamSearch);
        Add(values, "max_scan_tuples", options.MaxScanTuples);
        return SetOptions(database, "vchordg", values, cancellationToken);
    }

    private static void Add(Dictionary<string, string> values, string name, IFormattable? value)
    {
        if (value is not null)
            values.Add(name, value.ToString(null, CultureInfo.InvariantCulture));
    }

    private static async Task SetOptions(DatabaseFacade database, string prefix,
        Dictionary<string, string> values, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        if (database.CurrentTransaction is null)
            throw new InvalidOperationException("VectorChord search overrides require an active EF transaction and last until it ends.");
        if (values.Count == 0)
            return;
        var parameters = new List<object>();
        var calls = new List<string>();
        foreach (var (name, value) in values)
        {
            var offset = parameters.Count;
            parameters.Add(prefix + "." + name);
            parameters.Add(value);
            calls.Add("set_config({" + offset + "}, {" + (offset + 1) + "}, true)");
        }
        var statement = FormattableStringFactory.Create("SELECT " + string.Join(", ", calls), parameters.ToArray());
        await database.ExecuteSqlInterpolatedAsync(statement, cancellationToken).ConfigureAwait(false);
    }
}
