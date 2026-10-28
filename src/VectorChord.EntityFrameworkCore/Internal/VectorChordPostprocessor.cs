using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql.EntityFrameworkCore.PostgreSQL.Query.Internal;
using Npgsql.EntityFrameworkCore.PostgreSQL.Query.Expressions.Internal;

namespace VectorChord.EntityFrameworkCore.Internal;

internal sealed class VectorChordPostprocessorFactory(
    QueryTranslationPostprocessorDependencies dependencies,
    RelationalQueryTranslationPostprocessorDependencies relationalDependencies,
    ISqlExpressionFactory sql,
    IRelationalTypeMappingSource mappings) : IQueryTranslationPostprocessorFactory
{
    public QueryTranslationPostprocessor Create(QueryCompilationContext context)
        => new VectorChordPostprocessor(dependencies, relationalDependencies,
            (RelationalQueryCompilationContext)context, sql, mappings);
}

internal sealed class VectorChordPostprocessor(
    QueryTranslationPostprocessorDependencies dependencies,
    RelationalQueryTranslationPostprocessorDependencies relationalDependencies,
    RelationalQueryCompilationContext context,
    ISqlExpressionFactory sql,
    IRelationalTypeMappingSource mappings)
    : NpgsqlQueryTranslationPostprocessor(dependencies, relationalDependencies, context)
{
    public override Expression Process(Expression query)
        => new MaterializationVisitor(sql, mappings).Visit(base.Process(query));

    private sealed class MaterializationVisitor(ISqlExpressionFactory sql, IRelationalTypeMappingSource mappings) : ExpressionVisitor
    {
        protected override Expression VisitExtension(Expression node)
        {
            if (node is ShapedQueryExpression { QueryExpression: SelectExpression select } shaped)
                return shaped.Update(Materialize((SelectExpression)Visit(select)), Visit(shaped.ShaperExpression));

            if (node is RelationalSplitCollectionShaperExpression split)
                return split.Update(Visit(split.ParentIdentifier), Visit(split.ChildIdentifier),
                    Materialize((SelectExpression)Visit(split.SelectExpression)), Visit(split.InnerShaper));

            if (node is SqlParameterExpression { TypeMapping: QuantizedVectorTypeMapping parameterMapping } parameter)
                return VectorConversions.Quantize(parameter, parameterMapping, sql, mappings);
            if (node is SqlConstantExpression { TypeMapping: QuantizedVectorTypeMapping constantMapping } constant)
                return VectorConversions.Quantize(constant, constantMapping, sql, mappings);

            if (node is UpdateExpression update)
            {
                var setters = update.ColumnValueSetters.Select(setter =>
                {
                    var value = (SqlExpression)Visit(setter.Value);
                    return new ColumnValueSetter(setter.Column,
                        setter.Column.TypeMapping is QuantizedVectorTypeMapping mapping
                            ? VectorConversions.Quantize(value, mapping, sql, mappings)
                            : value);
                }).ToArray();
                return update.Update((SelectExpression)Visit(update.SelectExpression), setters);
            }

            if (node is SelectExpression selectExpression)
            {
                var visited = (SelectExpression)base.VisitExtension(selectExpression);
                var orderings = visited.Orderings.Select(ordering =>
                {
                    if (visited.IsDistinct || visited.GroupBy.Count != 0)
                        return ordering;
                    // Removing the constant preserves ordering and exposes the index operator.
                    return ordering.Expression is SqlBinaryExpression
                    {
                        OperatorType: ExpressionType.Add,
                        Left: SqlConstantExpression { Value: 1d },
                        Right: PgUnknownBinaryExpression { Operator: "<=>" } cosine
                    } && cosine.Left.TypeMapping is QuantizedVectorTypeMapping
                        ? ordering.Update(cosine)
                        : ordering;
                }).ToArray();
                return visited.Update(visited.Tables, visited.Predicate, visited.GroupBy, visited.Having,
                    visited.Projection, orderings, visited.Offset, visited.Limit);
            }

            return base.VisitExtension(node);
        }

        private SelectExpression Materialize(SelectExpression select)
        {
            var projections = select.Projection.Select(projection =>
                projection.Expression.TypeMapping is QuantizedVectorTypeMapping mapping
                    ? projection.Update(VectorConversions.Dequantize(projection.Expression, mapping, mappings))
                    : projection).ToArray();
            return select.Update(select.Tables, select.Predicate, select.GroupBy, select.Having,
                projections, select.Orderings, select.Offset, select.Limit);
        }
    }
}
