using System.Collections.Frozen;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql.EntityFrameworkCore.PostgreSQL.Query.Expressions.Internal;
using Pgvector.EntityFrameworkCore;

namespace VectorChord.EntityFrameworkCore.Internal;

internal sealed class VectorChordTranslatorPlugin(
    ISqlExpressionFactory sql,
    IRelationalTypeMappingSource mappings) : IMethodCallTranslatorPlugin
{
    public IEnumerable<IMethodCallTranslator> Translators { get; } = [new VectorChordTranslator(sql, mappings)];
}

internal sealed class VectorChordTranslator(
    ISqlExpressionFactory sql,
    IRelationalTypeMappingSource mappings) : IMethodCallTranslator
{
    public SqlExpression? Translate(
        SqlExpression? instance, MethodInfo method, IReadOnlyList<SqlExpression> arguments,
        IDiagnosticsLogger<DbLoggerCategory.Query> logger)
    {
        var ours = method.DeclaringType == typeof(VectorChordDbFunctionsExtensions);
        var pgvector = method.DeclaringType == typeof(VectorDbFunctionsExtensions);
        if (!ours && !pgvector)
            return null;

        var offset = ours && IsDbFunctionsOverload(method) ? 1 : 0;
        var left = sql.ApplyDefaultTypeMapping(arguments[offset]);
        var right = sql.ApplyDefaultTypeMapping(arguments[offset + 1]);
        var quantized = left.TypeMapping as QuantizedVectorTypeMapping
            ?? right.TypeMapping as QuantizedVectorTypeMapping;
        if (pgvector && quantized is null)
            return null;

        if (quantized is not null)
        {
            left = QuantizeOperand(left, quantized);
            right = QuantizeOperand(right, quantized);
        }

        if (method.Name.StartsWith("Within", StringComparison.Ordinal))
        {
            var op = method.Name switch
            {
                "WithinL2Distance" => "<<->>",
                "WithinCosineDistance" => "<<=>>",
                "WithinNegativeInnerProduct" => "<<#>>",
                _ => throw new NotSupportedException()
            };
            var storage = quantized?.QuantizedType ?? left.TypeMapping!.StoreTypeNameBase;
            var threshold = sql.ApplyDefaultTypeMapping(arguments[offset + 2]);
            // VectorChord 1.1.1's RaBitQ cosine operator returns -cosine, not 1-cosine.
            if (quantized is not null && method.Name == "WithinCosineDistance")
                threshold = sql.Subtract(threshold, sql.Constant(1f));
            var sphere = sql.Function("sphere",
                [right, threshold],
                nullable: false, argumentsPropagateNullability: [false, false],
                typeof(object), new SphereTypeMapping("sphere_" + storage));
            var predicate = new PgUnknownBinaryExpression(left, sphere, op, typeof(bool), mappings.FindMapping(typeof(bool)));
            // Preserve the index condition, while making null vectors a false result even in projections/negations.
            return sql.AndAlso(sql.IsNotNull(left), sql.AndAlso(sql.IsNotNull(right), predicate));
        }

        var distanceOperator = method.Name switch
        {
            "L2Distance" => "<->",
            "CosineDistance" => "<=>",
            "MaxInnerProduct" => "<#>",
            "MaxSimDistance" => "@#",
            _ => throw new NotSupportedException($"The {method.Name} operator is not supported for RaBitQ storage.")
        };
        var distance = new PgUnknownBinaryExpression(left, right, distanceOperator, method.ReturnType,
            mappings.FindMapping(method.ReturnType));
        return quantized is not null && method.Name == "CosineDistance"
            ? sql.Add(sql.Constant(1d), distance)
            : distance;
    }

    private static readonly FrozenSet<MethodInfo> DbFunctionsOverloads = typeof(VectorChordDbFunctionsExtensions)
        .GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Where(m => m.GetParameters()[0].ParameterType == typeof(DbFunctions))
        .ToFrozenSet();

    private static bool IsDbFunctionsOverload(MethodInfo method) => DbFunctionsOverloads.Contains(method);

    private SqlExpression QuantizeOperand(SqlExpression expression, QuantizedVectorTypeMapping target)
    {
        if (expression.TypeMapping is QuantizedVectorTypeMapping existing
            && expression is not SqlParameterExpression and not SqlConstantExpression)
        {
            if (existing.QuantizedType != target.QuantizedType || existing.IsArray != target.IsArray)
                throw new NotSupportedException("Distances between different RaBitQ storage formats require an explicit conversion.");
            return expression;
        }

        return VectorConversions.Quantize(expression, target, sql, mappings);
    }

    private sealed class SphereTypeMapping(string storeType)
        : RelationalTypeMapping(new RelationalTypeMappingParameters(new CoreTypeMappingParameters(typeof(object)), storeType))
    {
        protected override RelationalTypeMapping Clone(RelationalTypeMappingParameters parameters)
            => new SphereTypeMapping(parameters.StoreType);
    }
}

internal static class VectorConversions
{
    public static SqlExpression Quantize(
        SqlExpression value, QuantizedVectorTypeMapping target,
        ISqlExpressionFactory sql, IRelationalTypeMappingSource mappings)
    {
        if (value.TypeMapping is QuantizedVectorTypeMapping && value is not SqlParameterExpression and not SqlConstantExpression)
            return value;

        var dense = mappings.FindMapping(value.Type)!;
        value = value switch
        {
            SqlParameterExpression parameter => parameter.ApplyTypeMapping(dense),
            SqlConstantExpression constant => constant.ApplyTypeMapping(dense),
            _ => sql.ApplyTypeMapping(value, dense)
        };
        return Function(value, target.QuantizeFunction, target.IsArray, target);
    }

    public static SqlExpression Dequantize(SqlExpression value, QuantizedVectorTypeMapping mapping,
        IRelationalTypeMappingSource mappings)
        => Function(value, mapping.DequantizeFunction, mapping.IsArray, mappings.FindMapping(mapping.ClrType)!);

    private static SqlExpression Function(SqlExpression value, string name, bool array, RelationalTypeMapping mapping)
        => new SqlFunctionExpression(array ? VectorSql.ArrayFunctionPrefix + name : name,
            [value], nullable: true, argumentsPropagateNullability: [true], value.Type, mapping);
}
