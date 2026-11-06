using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Pgvector;
using VectorChord.EntityFrameworkCore;
using Xunit;

namespace VectorChord.Tests;

public sealed class RebuildTests
{
    [Fact]
    public void Options_and_metric_round_trip()
    {
        using var db = new RebuildDb();
        var index = Index(db, nameof(RebuildDb.Item.Embedding));

        var options = index.GetVectorChordRqOptions();
        Assert.NotNull(options);
        Assert.Equal([10, 100], options.Lists!);
        Assert.Equal(2, options.BuildThreads);
        Assert.True(options.ResidualQuantization);
        Assert.True(options.SphericalCentroids);
        Assert.Equal([1, 10], options.Probes!);
        Assert.Equal(1.25, options.Epsilon);
        Assert.Equal(3, options.MaxSimRefine);
        Assert.Equal(4, options.MaxSimThreshold);
        Assert.Equal(DistanceMetric.Cosine, index.GetVectorChordDistanceMetric());

        var plain = Index(db, nameof(RebuildDb.Item.Long));
        var empty = plain.GetVectorChordRqOptions();
        Assert.NotNull(empty);
        Assert.Null(empty.Lists);
        Assert.Null(empty.Probes);
        Assert.Equal(DistanceMetric.Cosine, plain.GetVectorChordDistanceMetric());
    }

    [Fact]
    public void Runtime_model_indexes_explain_where_the_options_live()
    {
        using var db = new RebuildDb();
        var index = db.Model.FindEntityType(typeof(RebuildDb.Item))!.GetIndexes()
            .Single(i => i.Properties[0].Name == nameof(RebuildDb.Item.Other));

        var exception = Assert.Throws<InvalidOperationException>(() => index.GetVectorChordRqOptions());
        Assert.Contains("IDesignTimeModel", exception.Message);
    }

    [Fact]
    public void Rebuild_swaps_the_index_and_preserves_unmodelled_options()
    {
        using var db = new RebuildDb();
        var index = Index(db, nameof(RebuildDb.Item.Other));
        var name = DatabaseName(index);
        var options = index.GetVectorChordRqOptions()!;
        options.Lists = [500, 5000];

        var sql = VectorChordRqIndexSql.BuildRebuild(index, options);

        Assert.Equal(4, sql.Length);
        Assert.Equal($"DROP INDEX CONCURRENTLY IF EXISTS \"{name}__rebuild\"", sql[0]);
        Assert.StartsWith($"CREATE INDEX CONCURRENTLY \"{name}__rebuild\"", sql[1]);
        Assert.Contains("ON \"Items\" USING vchordrq (\"Other\" vector_l2_ops)", sql[1]);
        Assert.Contains("build.internal.lists = [500, 5000]", sql[1]);
        Assert.Contains("build.internal.sampling_factor = 64", sql[1]);
        Assert.Contains("residual_quantization = true", sql[1]);
        Assert.Contains("probes = '1,10'", sql[1]);
        Assert.Contains("epsilon = '1.25'", sql[1]);
        Assert.Equal($"DROP INDEX CONCURRENTLY \"{name}\"", sql[2]);
        Assert.Equal($"ALTER INDEX \"{name}__rebuild\" RENAME TO \"{name}\"", sql[3]);

        var offline = VectorChordRqIndexSql.BuildRebuild(index, options, concurrently: false);
        Assert.DoesNotContain(offline, statement => statement.Contains("CONCURRENTLY", StringComparison.Ordinal));
    }

    [Fact]
    public void Tpc_indexes_are_rebuilt_on_every_concrete_table()
    {
        using var db = new TpcDb();
        var index = db.GetService<IDesignTimeModel>().Model
            .FindEntityType(typeof(TpcDb.Animal))!.GetIndexes().Single();

        var sql = VectorChordRqIndexSql.BuildRebuild(index, new() { Lists = [10] });

        Assert.Equal(8, sql.Length);
        Assert.Contains(sql, s => s.Contains(
            "CREATE INDEX CONCURRENTLY \"IX_Cat_Embedding__rebuild\" ON \"Cat\" USING vchordrq (\"Embedding\" vector_cosine_ops)"));
        Assert.Contains(sql, s => s.Contains(
            "CREATE INDEX CONCURRENTLY \"IX_Dog_Embedding__rebuild\" ON \"Dog\" USING vchordrq (\"Embedding\" vector_cosine_ops)"));
        Assert.Contains("DROP INDEX CONCURRENTLY \"IX_Cat_Embedding\"", sql);
        Assert.Contains("ALTER INDEX \"IX_Dog_Embedding__rebuild\" RENAME TO \"IX_Dog_Embedding\"", sql);

        var cat = VectorChordRqIndexSql.BuildRebuild(index, StoreObjectIdentifier.Table("Cat", null), new() { Lists = [10] });
        Assert.Equal(4, cat.Length);
        Assert.Throws<ArgumentException>(() =>
            VectorChordRqIndexSql.BuildRebuild(index, StoreObjectIdentifier.Table("Animal", null), new()));
    }

    [Fact]
    public void Invalid_options_are_rejected_before_any_sql_is_built()
    {
        using var db = new RebuildDb();
        var index = Index(db, nameof(RebuildDb.Item.Other));

        Assert.Throws<ArgumentException>(() =>
            VectorChordRqIndexSql.BuildRebuild(index, new() { Lists = [100, 10] }));
        Assert.Throws<ArgumentException>(() =>
            VectorChordRqIndexSql.BuildRebuild(index, new() { Lists = [100], Probes = [1, 2] }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            VectorChordRqIndexSql.BuildRebuild(index, new() { Epsilon = 9 }));
    }

    [Fact]
    public void Scratch_names_stay_within_the_identifier_limit()
    {
        using var db = new RebuildDb();
        var index = Index(db, nameof(RebuildDb.Item.Long));
        var name = DatabaseName(index);

        var create = VectorChordRqIndexSql.BuildRebuild(index, new() { Lists = [10] })[1];
        Assert.True(name.Length + "__rebuild".Length > 63);
        Assert.Contains($"\"{name[..(63 - "__rebuild".Length)]}__rebuild\"", create);
    }

    private static IReadOnlyIndex Index(DbContext db, string property)
        => db.GetService<IDesignTimeModel>().Model
            .FindEntityType(typeof(RebuildDb.Item))!.GetIndexes()
            .Single(i => i.Properties.Count == 1 && i.Properties[0].Name == property);

    private static string DatabaseName(IReadOnlyIndex index)
        => index.GetDatabaseName(StoreObjectIdentifier.Table("Items", null))!;

    private sealed class RebuildDb : DbContext
    {
        public DbSet<Item> Items => Set<Item>();

        public sealed class Item
        {
            public int Id { get; set; }
            public Vector? Embedding { get; set; }
            public Vector? Other { get; set; }
            public Vector? Long { get; set; }
        }

        protected override void OnConfiguring(DbContextOptionsBuilder options)
            => options.UseNpgsql("Host=localhost;Database=unused", o => o.UseVectorChord());

        protected override void OnModelCreating(ModelBuilder model)
        {
            var entity = model.Entity<Item>();
            foreach (var name in new[] { nameof(Item.Embedding), nameof(Item.Other), nameof(Item.Long) })
                entity.Property(name).HasColumnType("vector(3)");

            entity.HasIndex(x => x.Embedding).HasVectorChordRq(DistanceMetric.Cosine, rq =>
            {
                rq.Lists = [10, 100];
                rq.BuildThreads = 2;
                rq.ResidualQuantization = true;
                rq.SphericalCentroids = true;
                rq.Probes = [1, 10];
                rq.Epsilon = 1.25;
                rq.MaxSimRefine = 3;
                rq.MaxSimThreshold = 4;
            });

            var other = entity.HasIndex(x => x.Other);
            other.HasVectorChordRq(DistanceMetric.L2, rq =>
            {
                rq.Lists = [10, 100];
                rq.Probes = [1, 10];
                rq.Epsilon = 1.25;
            });
            // An option this library does not model must survive a rebuild.
            other.HasAnnotation("Npgsql:StorageParameter:options",
                "residual_quantization = true\nbuild.internal.sampling_factor = 64\nbuild.internal.lists = [10, 100]");

            entity.HasIndex(x => x.Long)
                .HasDatabaseName("ix_documents_with_a_very_long_name_that_exceeds_the_postgres_identifier_limit")
                .HasVectorChordRq(DistanceMetric.Cosine);
        }
    }

    private sealed class TpcDb : DbContext
    {
        public abstract class Animal
        {
            public int Id { get; set; }
            public Vector? Embedding { get; set; }
        }

        public sealed class Cat : Animal;

        public sealed class Dog : Animal;

        protected override void OnConfiguring(DbContextOptionsBuilder options)
            => options.UseNpgsql("Host=localhost;Database=unused", o => o.UseVectorChord());

        protected override void OnModelCreating(ModelBuilder model)
        {
            model.Entity<Animal>().UseTpcMappingStrategy();
            model.Entity<Animal>().Property(x => x.Embedding).HasColumnType("vector(3)");
            model.Entity<Animal>().HasIndex(x => x.Embedding).HasVectorChordRq(DistanceMetric.Cosine);
            model.Entity<Cat>();
            model.Entity<Dog>();
        }
    }
}
