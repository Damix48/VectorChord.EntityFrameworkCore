using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;
using VectorChord.EntityFrameworkCore.Internal;

namespace VectorChord.EntityFrameworkCore;

public static class VectorChordOptionsBuilderExtensions
{
    public static NpgsqlDbContextOptionsBuilder UseVectorChord(this NpgsqlDbContextOptionsBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseVector();
        var options = ((IRelationalDbContextOptionsBuilderInfrastructure)builder).OptionsBuilder;
        ((IDbContextOptionsBuilderInfrastructure)options).AddOrUpdateExtension(
            options.Options.FindExtension<VectorChordOptionsExtension>() ?? new VectorChordOptionsExtension());
        return builder;
    }

    public static ModelBuilder HasVectorChordExtension(this ModelBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.HasPostgresExtension("vector");
        builder.HasPostgresExtension("vchord");
        return builder;
    }
}
