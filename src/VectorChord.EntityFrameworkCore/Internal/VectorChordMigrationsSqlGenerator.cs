using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.Internal;
using Npgsql.EntityFrameworkCore.PostgreSQL.Migrations;

namespace VectorChord.EntityFrameworkCore.Internal;

internal sealed class VectorChordMigrationsSqlGenerator(
    MigrationsSqlGeneratorDependencies dependencies,
    INpgsqlSingletonOptions options) : NpgsqlMigrationsSqlGenerator(dependencies, options)
{
    protected override void Generate(AlterDatabaseOperation operation, IModel? model,
        MigrationCommandListBuilder builder)
    {
        var extensions = operation.GetPostgresExtensions().ToArray();
        // Npgsql orders extension names alphabetically: vchord would otherwise precede its dependency.
        if (extensions.Any(e => e.Name == "vchord") && extensions.FirstOrDefault(e => e.Name == "vector") is { } vector)
            GenerateCreateExtension(vector, model, builder);
        base.Generate(operation, model, builder);
    }

    protected override void Generate(AlterColumnOperation operation, IModel? model,
        MigrationCommandListBuilder builder)
    {
        var from = operation.OldColumn.ColumnType;
        var to = operation.ColumnType;
        var fromQuantized = QuantizedVectorTypeMapping.IsQuantized(from);
        var toQuantized = QuantizedVectorTypeMapping.IsQuantized(to);
        if (from == to || !fromQuantized && !toQuantized)
        {
            base.Generate(operation, model, builder);
            return;
        }

        var array = operation.ClrType.IsArray;
        var element = array ? operation.ClrType.GetElementType()! : operation.ClrType;
        if (from is null || to is null
            || from.EndsWith("[]", StringComparison.Ordinal) != array
            || to.EndsWith("[]", StringComparison.Ordinal) != array
            || element != typeof(Pgvector.Vector) && element != typeof(Pgvector.HalfVector))
            throw new NotSupportedException("RaBitQ storage changes require matching scalar/array Vector or HalfVector columns.");
        if (operation.ComputedColumnSql != operation.OldColumn.ComputedColumnSql)
            throw new NotSupportedException("Change computed column configuration separately from RaBitQ storage.");

        var column = Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name);
        var value = column;
        if (fromQuantized)
            value = VectorSql.Convert(value,
                element == typeof(Pgvector.HalfVector) ? "dequantize_to_halfvec" : "dequantize_to_vector", array);
        if (toQuantized)
        {
            var mapping = new QuantizedVectorTypeMapping(to, operation.ClrType);
            value = VectorSql.Convert(value, mapping.QuantizeFunction, array);
        }
        builder.Append("-- RaBitQ storage conversion is lossy; dequantization cannot restore the original vectors.").AppendLine();
        if (array)
        {
            // PostgreSQL disallows subqueries directly in ALTER TYPE USING.
            builder.Append("CREATE FUNCTION pg_temp.__vectorchord_convert_array(").Append(from)
                .Append(") RETURNS ").Append(to)
                .Append(" LANGUAGE sql IMMUTABLE STRICT AS $$ SELECT ")
                .Append(value.Replace(column, "$1", StringComparison.Ordinal)).AppendLine(" $$;");
            value = "pg_temp.__vectorchord_convert_array(" + column + ")";
        }
        builder.Append("ALTER TABLE ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Table, operation.Schema))
            .Append(" ALTER COLUMN ").Append(column)
            .Append(" TYPE ").Append(to).Append(" USING ").Append(value).AppendLine(";");
        if (array)
            builder.Append("DROP FUNCTION pg_temp.__vectorchord_convert_array(").Append(from).AppendLine(");");

        // Let Npgsql handle all other column changes without emitting a second TYPE alteration.
        operation.OldColumn.ColumnType = to;
        try
        {
            base.Generate(operation, model, builder);
        }
        finally
        {
            operation.OldColumn.ColumnType = from;
        }
    }
}
