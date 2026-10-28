using System.Data.Common;
using System.Globalization;
using System.Linq.Expressions;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Pgvector;

namespace VectorChord.EntityFrameworkCore.Internal;

internal sealed partial class QuantizedVectorTypeMapping : RelationalTypeMapping
{
    public string QuantizedType { get; }
    public bool IsArray { get; }
    public Type ElementType { get; }
    public string DenseType { get; }
    public string QuantizeFunction { get; }
    public string DequantizeFunction { get; }
    private string ParameterDataTypeName { get; }
    private string QuotedDenseType { get; }
    private string DenseArrayCast { get; }

    public QuantizedVectorTypeMapping(string storeType, Type clrType)
        : this(new RelationalTypeMappingParameters(
            new CoreTypeMappingParameters(clrType, comparer: CreateComparer(clrType)), storeType))
    {
    }

    private QuantizedVectorTypeMapping(RelationalTypeMappingParameters parameters) : base(parameters)
    {
        QuantizedType = TryParseStoreType(parameters.StoreType, out var quantizedType, out _)
            ? quantizedType!
            : throw new ArgumentException("Expected rabitq4 or rabitq8, optionally with dimensions and [].", nameof(parameters));
        IsArray = ClrType.IsArray;
        ElementType = IsArray ? ClrType.GetElementType()! : ClrType;
        DenseType = ElementType == typeof(HalfVector) ? "halfvec" : "vector";
        QuantizeFunction = "quantize_to_" + QuantizedType;
        DequantizeFunction = "dequantize_to_" + DenseType;
        ParameterDataTypeName = DenseType + (IsArray ? "[]" : "");
        QuotedDenseType = "'::" + DenseType;
        DenseArrayCast = "::" + ParameterDataTypeName;
    }

    public static bool IsQuantized(string? storeType) => TryParseStoreType(storeType, out _, out _);

    public static int? GetDimensions(string storeType)
        => TryParseStoreType(storeType, out _, out var dimensions) ? dimensions : null;

    public static bool TryParseStoreType(string? storeType, out string? quantizedType, out int? dimensions)
    {
        var match = storeType is null ? Match.Empty : StoreTypePattern().Match(storeType);
        quantizedType = match.Success ? match.Groups[1].Value.ToLowerInvariant() : null;
        dimensions = match.Groups[2].Success
            ? int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture)
            : null;
        return match.Success;
    }

    protected override RelationalTypeMapping Clone(RelationalTypeMappingParameters parameters)
        => new QuantizedVectorTypeMapping(parameters);

    protected override void ConfigureParameter(DbParameter parameter)
        => ((NpgsqlParameter)parameter).DataTypeName = ParameterDataTypeName;

    protected override string GenerateNonNullSqlLiteral(object value)
    {
        var builder = new StringBuilder();
        if (IsArray)
        {
            builder.Append("ARRAY[");
            var index = 0;
            foreach (var element in (Array)value)
            {
                if (index++ > 0) builder.Append(", ");
                AppendLiteral(builder, element);
            }
            builder.Append(']').Append(DenseArrayCast);
        }
        else
            AppendLiteral(builder, value);
        return VectorSql.Convert(builder.ToString(), QuantizeFunction, IsArray);
    }

    private void AppendLiteral(StringBuilder builder, object? element)
    {
        if (element is null)
        {
            builder.Append("NULL");
            return;
        }
        builder.Append('\'').Append(element.ToString()!.Replace("'", "''")).Append(QuotedDenseType);
    }

    public override Expression GenerateCodeLiteral(object value)
    {
        Expression Element(object element)
            => Expression.New(ElementType.GetConstructor([typeof(string)])!, Expression.Constant(element.ToString()));
        return IsArray
            ? Expression.NewArrayInit(ElementType, ((Array)value).Cast<object?>()
                .Select(v => v is null ? Expression.Constant(null, ElementType) : Element(v)))
            : Element(value);
    }

    private static ValueComparer CreateComparer(Type type)
        => type == typeof(Vector)
            ? new ValueComparer<Vector>((a, b) => a == b, v => v.GetHashCode(), v => new Vector(v.ToArray()))
            : type == typeof(HalfVector)
                ? new ValueComparer<HalfVector>((a, b) => a == b, v => v.GetHashCode(), v => new HalfVector(v.ToArray()))
                : type == typeof(Vector[])
                    ? new ValueComparer<Vector[]>(
                        (a, b) => a == b || a != null && b != null && a.SequenceEqual(b),
                        v => ArrayHash(v),
                        v => v.Select(x => x == null ? null! : new Vector(x.ToArray())).ToArray())
                    : new ValueComparer<HalfVector[]>(
                        (a, b) => a == b || a != null && b != null && a.SequenceEqual(b),
                        v => ArrayHash(v),
                        v => v.Select(x => x == null ? null! : new HalfVector(x.ToArray())).ToArray());

    private static int ArrayHash<T>(T[] values)
    {
        var hash = new HashCode();
        foreach (var value in values)
            hash.Add(value);
        return hash.ToHashCode();
    }

    [GeneratedRegex(@"^(rabitq[48])\s*(?:\(\s*(\d+)\s*\))?\s*(\[\])?$", RegexOptions.IgnoreCase)]
    private static partial Regex StoreTypePattern();
}

internal sealed class QuantizedVectorTypeMappingPlugin : IRelationalTypeMappingSourcePlugin
{
    public RelationalTypeMapping? FindMapping(in RelationalTypeMappingInfo mappingInfo)
    {
        var storeType = mappingInfo.StoreTypeName;
        if (!QuantizedVectorTypeMapping.IsQuantized(storeType))
            return null;

        var array = storeType!.EndsWith("[]", StringComparison.Ordinal);
        var type = mappingInfo.ClrType ?? (array ? typeof(Vector[]) : typeof(Vector));
        var element = array ? type.GetElementType() : type;
        if (type.IsArray != array || element != typeof(Vector) && element != typeof(HalfVector))
            throw new InvalidOperationException("RaBitQ storage requires Vector, HalfVector, Vector[], or HalfVector[].");

        return new QuantizedVectorTypeMapping(storeType, type);
    }
}
