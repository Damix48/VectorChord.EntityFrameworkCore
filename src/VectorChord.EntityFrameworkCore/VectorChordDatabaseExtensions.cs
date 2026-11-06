using System.Globalization;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace VectorChord.EntityFrameworkCore;

public static class VectorChordDatabaseExtensions
{
    /// <summary>
    /// Rebuilds the <c>vchordrq</c> index on <paramref name="property"/> with changed build options,
    /// using <c>CREATE INDEX CONCURRENTLY</c> / <c>DROP INDEX CONCURRENTLY</c> / <c>ALTER INDEX ... RENAME</c>.
    /// Options left unset keep their current values.
    /// </summary>
    public static async Task RebuildVectorChordRqIndexAsync<TEntity>(this DatabaseFacade database,
        Expression<Func<TEntity, object?>> property, Action<VectorChordRqIndexOptions> configure,
        CancellationToken cancellationToken = default) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(property);
        ArgumentNullException.ThrowIfNull(configure);
        if (database.CurrentTransaction is not null)
            throw new InvalidOperationException(
                "VectorChord index rebuilds use CONCURRENTLY and cannot run inside a transaction.");

        var member = (property.Body as UnaryExpression)?.Operand as MemberExpression ?? property.Body as MemberExpression;
        var name = member?.Member.Name
            ?? throw new ArgumentException("Expected a property accessor.", nameof(property));
        // Index annotations do not survive into the runtime model.
        var entity = database.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(TEntity))
            ?? throw new InvalidOperationException($"{typeof(TEntity).Name} is not part of the model.");
        var index = entity.GetIndexes().FirstOrDefault(i => i.GetMethod() == "vchordrq"
                && i.Properties.Count == 1 && i.Properties[0].Name == name)
            ?? throw new InvalidOperationException($"No vchordrq index is configured on {typeof(TEntity).Name}.{name}.");

        var options = index.GetVectorChordRqOptions() ?? new VectorChordRqIndexOptions();
        configure(options);
        foreach (var statement in VectorChordRqIndexSql.BuildRebuild(index, options))
            await database.ExecuteSqlRawAsync(statement, cancellationToken).ConfigureAwait(false);
    }

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
