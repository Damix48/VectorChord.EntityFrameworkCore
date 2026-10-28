using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pgvector;
using VectorChord.EntityFrameworkCore;
using Xunit;

namespace VectorChord.Tests;

public sealed class ValidationTests
{
    [Fact]
    public void Unique_and_multicolumn_indexes_are_rejected()
    {
        AssertInvalid(e => e.HasIndex(x => x.Embedding).IsUnique().HasVectorChordRq(DistanceMetric.L2));
        AssertInvalid(e => e.HasIndex(x => new { x.Id, x.Embedding }).HasVectorChordRq(DistanceMetric.L2));
    }

    [Fact]
    public void Array_metric_and_residual_quantization_are_validated()
    {
        AssertInvalid(e => e.HasIndex(x => x.Tokens).HasVectorChordRq(DistanceMetric.Cosine));
        AssertInvalid(e => e.HasIndex(x => x.Embedding).HasVectorChordRq(DistanceMetric.MaxSim));
        AssertInvalid(e =>
        {
            e.Property(x => x.Embedding).HasColumnType("rabitq8(3)");
            e.HasIndex(x => x.Embedding).HasVectorChordRq(DistanceMetric.Cosine, o => o.ResidualQuantization = true);
        });
    }

    [Fact]
    public void Lossy_keys_concurrency_tokens_and_invalid_dimensions_are_rejected()
    {
        AssertInvalid(e => e.Property(x => x.Embedding).HasColumnType("rabitq8(3)").IsConcurrencyToken());
        AssertInvalid(e => e.Property(x => x.Embedding).HasColumnType("rabitq8(0)"));
        AssertInvalid(e => e.Property(x => x.Embedding).HasColumnType("rabitq4(3)").ValueGeneratedNever());
    }

    [Fact]
    public void Index_options_are_invariant_and_probe_defaults_are_separate_from_TOML()
    {
        using var db = new ValidationDb(e =>
        {
            e.Property(x => x.Embedding).HasColumnType("rabitq8(3)");
            e.HasIndex(x => x.Embedding).HasVectorChordRq(DistanceMetric.Cosine, o =>
            {
                o.Lists = [10, 100];
                o.BuildThreads = 2;
                o.Probes = [1, 10];
                o.Epsilon = 1.25;
            });
        });
        var index = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(Shape))!.GetIndexes().Single();
        Assert.Equal("build.internal.lists = [10, 100]\nbuild.internal.build_threads = 2",
            index["Npgsql:StorageParameter:options"]);
        Assert.Equal("1,10", index["Npgsql:StorageParameter:probes"]);
        Assert.Equal("1.25", index["Npgsql:StorageParameter:epsilon"]);
        Assert.Equal(["rabitq8_cosine_ops"], index.GetOperators());
    }

    private static void AssertInvalid(Action<EntityTypeBuilder<Shape>> configure)
    {
        using var db = new ValidationDb(configure);
        Assert.Throws<InvalidOperationException>(() => db.Model);
    }

    public sealed class Shape
    {
        public int Id { get; set; }
        public Vector? Embedding { get; set; }
        public Vector[]? Tokens { get; set; }
    }

    private sealed class ValidationDb(Action<EntityTypeBuilder<Shape>> configure) : DbContext
    {
        public object CacheKey => configure;
        protected override void OnConfiguring(DbContextOptionsBuilder options)
            => options.UseNpgsql("Host=localhost;Database=unused", o => o.UseVectorChord())
                .ReplaceService<IModelCacheKeyFactory, CacheKeys>();

        protected override void OnModelCreating(ModelBuilder model)
        {
            var entity = model.Entity<Shape>();
            entity.Property(x => x.Embedding).HasColumnType("vector(3)");
            entity.Property(x => x.Tokens).HasColumnType("vector(3)[]");
            configure(entity);
        }
    }

    private sealed class CacheKeys : IModelCacheKeyFactory
    {
        public object Create(DbContext context, bool designTime)
            => (((ValidationDb)context).CacheKey, designTime);
    }
}
