using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.Internal;
using Npgsql.EntityFrameworkCore.PostgreSQL.Query.Internal;

namespace VectorChord.EntityFrameworkCore.Internal;

internal sealed class VectorChordQuerySqlGeneratorFactory(
    QuerySqlGeneratorDependencies dependencies,
    IRelationalTypeMappingSource mappings,
    INpgsqlSingletonOptions options) : IQuerySqlGeneratorFactory
{
    public QuerySqlGenerator Create()
        => new VectorChordQuerySqlGenerator(dependencies, mappings, options.ReverseNullOrderingEnabled, options.PostgresVersion);
}

internal sealed class VectorChordQuerySqlGenerator(
    QuerySqlGeneratorDependencies dependencies,
    IRelationalTypeMappingSource mappings,
    bool reverseNullOrdering,
    Version postgresVersion)
    : NpgsqlQuerySqlGenerator(dependencies, mappings, reverseNullOrdering, postgresVersion)
{
    protected override Expression VisitSqlFunction(SqlFunctionExpression expression)
    {
        if (!expression.Name.StartsWith(VectorSql.ArrayFunctionPrefix, StringComparison.Ordinal))
            return base.VisitSqlFunction(expression);

        VectorSql.AppendConversion(s => Sql.Append(s), () => Visit(expression.Arguments![0]),
            expression.Name[VectorSql.ArrayFunctionPrefix.Length..], array: true);
        return expression;
    }
}
