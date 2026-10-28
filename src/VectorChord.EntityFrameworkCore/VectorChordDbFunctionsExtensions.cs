using Microsoft.EntityFrameworkCore;
using Pgvector;

namespace VectorChord.EntityFrameworkCore;

public static class VectorChordDbFunctionsExtensions
{
    public static bool WithinL2Distance(this Vector? vector, Vector? center, float threshold)
        => throw ServerOnly();

    public static bool WithinL2Distance(this DbFunctions _, Vector? vector, Vector? center, float threshold)
        => throw ServerOnly();

    public static bool WithinCosineDistance(this Vector? vector, Vector? center, float threshold)
        => throw ServerOnly();

    public static bool WithinCosineDistance(this DbFunctions _, Vector? vector, Vector? center, float threshold)
        => throw ServerOnly();

    public static bool WithinNegativeInnerProduct(this Vector? vector, Vector? center, float threshold)
        => throw ServerOnly();

    public static bool WithinNegativeInnerProduct(this DbFunctions _, Vector? vector, Vector? center, float threshold)
        => throw ServerOnly();

    public static float MaxSimDistance(this DbFunctions _, Vector[] document, Vector[] query)
        => throw ServerOnly();

    public static bool WithinL2Distance(this HalfVector? vector, HalfVector? center, float threshold)
        => throw ServerOnly();

    public static bool WithinL2Distance(this DbFunctions _, HalfVector? vector, HalfVector? center, float threshold)
        => throw ServerOnly();

    public static bool WithinCosineDistance(this HalfVector? vector, HalfVector? center, float threshold)
        => throw ServerOnly();

    public static bool WithinCosineDistance(this DbFunctions _, HalfVector? vector, HalfVector? center, float threshold)
        => throw ServerOnly();

    public static bool WithinNegativeInnerProduct(this HalfVector? vector, HalfVector? center, float threshold)
        => throw ServerOnly();

    public static bool WithinNegativeInnerProduct(this DbFunctions _, HalfVector? vector, HalfVector? center, float threshold)
        => throw ServerOnly();

    public static float MaxSimDistance(this DbFunctions _, HalfVector[] document, HalfVector[] query)
        => throw ServerOnly();

    private static InvalidOperationException ServerOnly()
        => new("VectorChord functions can only be used in EF Core LINQ queries.");
}
