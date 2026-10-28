using System.Text;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Update;
using Npgsql.EntityFrameworkCore.PostgreSQL.Update.Internal;

namespace VectorChord.EntityFrameworkCore.Internal;

internal sealed class VectorChordUpdateSqlGenerator(UpdateSqlGeneratorDependencies dependencies)
    : NpgsqlUpdateSqlGenerator(dependencies)
{
    protected override void AppendValues(StringBuilder builder, string name, string? schema,
        IReadOnlyList<IColumnModification> operations)
    {
        if (!operations.Any(o => o.TypeMapping is QuantizedVectorTypeMapping))
        {
            base.AppendValues(builder, name, schema, operations);
            return;
        }

        builder.Append('(');
        for (var i = 0; i < operations.Count; i++)
        {
            if (i > 0) builder.Append(", ");
            var operation = operations[i];
            if (!operation.IsWrite)
                builder.Append("DEFAULT");
            else
                AppendValue(builder, operation, name, schema);
        }
        builder.Append(')');
    }

    protected override void AppendUpdateColumnValue(ISqlGenerationHelper helper, IColumnModification modification,
        StringBuilder builder, string name, string? schema)
    {
        if (modification.TypeMapping is QuantizedVectorTypeMapping)
            AppendValue(builder, modification, name, schema);
        else
            base.AppendUpdateColumnValue(helper, modification, builder, name, schema);
    }

    private void AppendValue(StringBuilder builder, IColumnModification modification, string name, string? schema)
    {
        if (modification.TypeMapping is QuantizedVectorTypeMapping mapping && modification.UseCurrentValueParameter)
        {
            VectorSql.AppendConversion(s => builder.Append(s),
                () => SqlGenerationHelper.GenerateParameterNamePlaceholder(builder, modification.ParameterName),
                mapping.QuantizeFunction, mapping.IsArray);
        }
        else if (modification.UseCurrentValueParameter)
            SqlGenerationHelper.GenerateParameterNamePlaceholder(builder, modification.ParameterName);
        else
            AppendSqlLiteral(builder, modification, name, schema);
    }

    protected override void AppendReturningClause(StringBuilder builder, IReadOnlyList<IColumnModification> operations,
        string? additionalValues = null)
    {
        if (!operations.Any(o => o.TypeMapping is QuantizedVectorTypeMapping))
        {
            base.AppendReturningClause(builder, operations, additionalValues);
            return;
        }

        builder.AppendLine().Append("RETURNING ");
        for (var i = 0; i < operations.Count; i++)
        {
            if (i > 0) builder.Append(", ");
            var operation = operations[i];
            var column = SqlGenerationHelper.DelimitIdentifier(operation.ColumnName);
            builder.Append(operation.TypeMapping is QuantizedVectorTypeMapping mapping
                ? VectorSql.Convert(column, mapping.DequantizeFunction, mapping.IsArray)
                : column);
        }
        if (additionalValues is not null)
            builder.Append(", ").Append(additionalValues);
    }
}

internal sealed class VectorChordModificationCommandFactory : IModificationCommandFactory
{
    public IModificationCommand CreateModificationCommand(in ModificationCommandParameters parameters)
        => new VectorChordModificationCommand(parameters);

    public INonTrackedModificationCommand CreateNonTrackedModificationCommand(in NonTrackedModificationCommandParameters parameters)
        => new NpgsqlModificationCommand(parameters);

    private sealed class VectorChordModificationCommand(in ModificationCommandParameters parameters)
        : NpgsqlModificationCommand(parameters)
    {
        public override IReadOnlyList<IColumnModification> ColumnModifications
        {
            get
            {
                var columns = base.ColumnModifications;
                // Read back the actual, lossy stored value in the same INSERT/UPDATE command.
                foreach (var column in columns)
                    if (column.IsWrite && column.TypeMapping is QuantizedVectorTypeMapping)
                        column.IsRead = true;
                return columns;
            }
        }
    }
}
