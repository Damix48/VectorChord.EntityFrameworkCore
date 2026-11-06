using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using VectorChord.EntityFrameworkCore.Internal;

namespace VectorChord.EntityFrameworkCore;

/// <summary>
/// DDL for rebuilding a <c>vchordrq</c> index with different build options.
/// </summary>
public static class VectorChordRqIndexSql
{
    private const int MaxIdentifierLength = 63;
    private const string ScratchSuffix = "__rebuild";

    /// <summary>
    /// Statements that build a scratch copy of <paramref name="index"/> with
    /// <paramref name="options"/>, drop the original, and rename the copy into place,
    /// once per table the index is mapped to (several for TPC).
    /// Options this library does not model are carried over unchanged.
    /// </summary>
    /// <remarks>
    /// <c>CONCURRENTLY</c> cannot run inside a transaction, so the caller must not have one open.
    /// </remarks>
    public static string[] BuildRebuild(IReadOnlyIndex index, VectorChordRqIndexOptions options,
        bool concurrently = true)
    {
        ArgumentNullException.ThrowIfNull(index);
        return Tables(index).SelectMany(table => BuildRebuild(index, table, options, concurrently)).ToArray();
    }

    /// <summary>
    /// Same as <see cref="BuildRebuild(IReadOnlyIndex, VectorChordRqIndexOptions, bool)"/> for one
    /// table of the index, which TPC hierarchies need.
    /// </summary>
    public static string[] BuildRebuild(IReadOnlyIndex index, StoreObjectIdentifier table,
        VectorChordRqIndexOptions options, bool concurrently = true)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(options);
        VectorOptionValidation.Index(options);

        if (index.GetMethod() != "vchordrq")
            throw new ArgumentException("Only vchordrq indexes can be rebuilt.", nameof(index));
        if (index.GetOperators() is not [var opclass])
            throw new ArgumentException("The index has no single operator class; configure it with HasVectorChordRq.", nameof(index));
        if (index.Properties.Count != 1)
            throw new ArgumentException("VectorChord indexes are single-column.", nameof(index));
        if (!Tables(index).Contains(table))
            throw new ArgumentException("The index is not mapped to the given table.", nameof(table));

        var property = (IReadOnlyProperty)index.Properties[0];
        var column = property.GetColumnName(table) ?? property.Name;
        var indexName = index.GetDatabaseName(table) ?? index.Name
            ?? throw new ArgumentException("The index has no name.", nameof(index));
        var scratch = ScratchName(indexName);
        var concurrentlySql = concurrently ? "CONCURRENTLY " : "";

        // A failed CREATE INDEX CONCURRENTLY leaves an invalid index behind; clearing it first keeps retries working.
        return
        [
            $"DROP INDEX {concurrentlySql}IF EXISTS {Quote(table.Schema, scratch)}",
            $"CREATE INDEX {concurrentlySql}{Quote(table.Schema, scratch)} ON {Quote(table.Schema, table.Name)} USING vchordrq ({Quote(column)} {opclass}){Storage(index, options)}",
            $"DROP INDEX {concurrentlySql}{Quote(table.Schema, indexName)}",
            $"ALTER INDEX {Quote(table.Schema, scratch)} RENAME TO {Quote(indexName)}",
        ];
    }

    /// <summary>
    /// The tables the index is physically created on. A TPC hierarchy maps one model index to one
    /// table per concrete type, so the entity type itself has no table name.
    /// </summary>
    private static IReadOnlyList<StoreObjectIdentifier> Tables(IReadOnlyIndex index)
    {
        var tables = ((IModel)index.DeclaringEntityType.Model).GetRelationalModel().Tables
            .Where(table => table.Indexes.Any(mapped => mapped.MappedIndexes.Any(i => ReferenceEquals(i, index))))
            .Select(table => StoreObjectIdentifier.Table(table.Name, table.Schema))
            .ToList();
        if (tables.Count == 0)
            throw new ArgumentException("The index is not mapped to any table.", nameof(index));
        return tables;
    }

    private static string Storage(IReadOnlyIndex index, VectorChordRqIndexOptions options)
    {
        var parameters = new List<string>();
        var toml = VectorChordRqOptionsToml.Merge(VectorChordIndexExtensions.StorageParameter(index, "options"), options);
        if (toml.Length > 0)
            parameters.Add("options = $vectorchord$" + toml + "$vectorchord$");
        Add(parameters, index, "probes", options.Probes is { } probes ? VectorOptionValidation.FormatProbes(probes) : null);
        Add(parameters, index, "epsilon", options.Epsilon?.ToString("R", CultureInfo.InvariantCulture));
        Add(parameters, index, "maxsim_refine", options.MaxSimRefine?.ToString(CultureInfo.InvariantCulture));
        Add(parameters, index, "maxsim_threshold", options.MaxSimThreshold?.ToString(CultureInfo.InvariantCulture));
        return parameters.Count == 0 ? "" : " WITH (" + string.Join(", ", parameters) + ")";
    }

    private static void Add(List<string> parameters, IReadOnlyIndex index, string name, string? value)
    {
        value ??= VectorChordIndexExtensions.StorageParameter(index, name);
        if (value is not null)
            parameters.Add(name + " = '" + value.Replace("'", "''", StringComparison.Ordinal) + "'");
    }

    private static string ScratchName(string name)
        => (name.Length + ScratchSuffix.Length <= MaxIdentifierLength
            ? name
            : name[..(MaxIdentifierLength - ScratchSuffix.Length)]) + ScratchSuffix;

    private static string Quote(string? schema, string name)
        => schema is null ? Quote(name) : Quote(schema) + "." + Quote(name);

    private static string Quote(string name) => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
