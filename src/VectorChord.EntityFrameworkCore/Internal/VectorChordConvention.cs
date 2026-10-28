using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;
using Pgvector;

namespace VectorChord.EntityFrameworkCore.Internal;

internal sealed class VectorChordConventionPlugin : IConventionSetPlugin
{
    public ConventionSet ModifyConventions(ConventionSet conventions)
    {
        conventions.ModelFinalizingConventions.Add(new VectorChordConvention());
        return conventions;
    }
}

internal sealed class VectorChordConvention : IModelFinalizingConvention
{
    public void ProcessModelFinalizing(IConventionModelBuilder modelBuilder,
        IConventionContext<IConventionModelBuilder> context)
    {
        foreach (var entity in modelBuilder.Metadata.GetEntityTypes())
        {
            foreach (var property in entity.GetDeclaredProperties())
            {
                var storeType = property.GetColumnType();
                if (!QuantizedVectorTypeMapping.TryParseStoreType(storeType, out _, out var dimensions))
                    continue;

                if (dimensions is < 1 or > 65535)
                    throw new InvalidOperationException($"Invalid RaBitQ dimensions for {entity.DisplayName()}.{property.Name}.");

                if (property.IsKey() || property.IsConcurrencyToken)
                    throw new InvalidOperationException("Lossy RaBitQ properties cannot be keys or concurrency tokens.");
                if (property.GetComputedColumnSql() is not null || property.GetDefaultValueSql() is not null)
                    throw new InvalidOperationException("RaBitQ storage conversion cannot be combined with computed columns or SQL defaults.");

                if (property.Builder.ValueGenerated(ValueGenerated.OnAddOrUpdate) is null)
                    throw new InvalidOperationException("RaBitQ storage requires server-value readback; do not override ValueGenerated.");
                property.SetBeforeSaveBehavior(PropertySaveBehavior.Save);
                property.SetAfterSaveBehavior(PropertySaveBehavior.Save);
                if (property.GetBeforeSaveBehavior() != PropertySaveBehavior.Save
                    || property.GetAfterSaveBehavior() != PropertySaveBehavior.Save)
                    throw new InvalidOperationException("RaBitQ properties must allow writes before and after saving.");
            }

            foreach (var index in entity.GetDeclaredIndexes())
            {
                var method = index.GetMethod();
                if (method is not ("vchordrq" or "vchordg"))
                    continue;
                if (index.IsUnique || index.Properties.Count != 1)
                    throw new InvalidOperationException("VectorChord indexes must be non-unique, single-column indexes.");

                var property = index.Properties[0];
                if (QuantizedVectorTypeMapping.IsQuantized(property.GetColumnType())
                    && index.GetAnnotations().Any(a => a.Name == "Npgsql:StorageParameter:options"
                        && a.Value is string text && text.Contains("residual_quantization = true", StringComparison.Ordinal)))
                    throw new InvalidOperationException("Residual quantization is not supported for RaBitQ column storage.");
                var type = property.ClrType;
                var element = type.IsArray ? type.GetElementType()! : type;
                if (element != typeof(Vector) && element != typeof(HalfVector))
                    throw new InvalidOperationException("VectorChord indexes require Vector or HalfVector properties.");

                var metric = index.FindAnnotation(VectorChordIndexBuilderExtensions.MetricAnnotation)?.Value as string;
                if (metric is null)
                    continue;

                if (type.IsArray != (metric == "maxsim") || method == "vchordg" && metric == "maxsim")
                    throw new InvalidOperationException("Vector arrays require an RQ MaxSim index; scalar vectors require a scalar distance metric.");

                var storage = QuantizedVectorTypeMapping.TryParseStoreType(property.GetColumnType(), out var quantizedType, out _)
                    ? quantizedType!
                    : element == typeof(HalfVector) ? "halfvec" : "vector";
                index.Builder.HasAnnotation("Npgsql:IndexOperators", new[] { storage + "_" + metric + "_ops" });
                ((IMutableIndex)index).RemoveAnnotation(VectorChordIndexBuilderExtensions.MetricAnnotation);
            }
        }
    }
}
