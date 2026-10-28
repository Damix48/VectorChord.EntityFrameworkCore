using System.ComponentModel.DataAnnotations.Schema;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Pgvector;
using VectorChord.EntityFrameworkCore;
using Xunit;

namespace VectorChord.Tests;

public sealed class TestDb(DbContextOptions<TestDb> options) : DbContext(options)
{
    public DbSet<Document> Documents => Set<Document>();
    public DbSet<Folder> Folders => Set<Folder>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasVectorChordExtension();
        var documents = model.Entity<Document>();
        documents.Property(x => x.Dense).HasColumnType("vector(3)");
        documents.Property(x => x.Half).HasColumnType("halfvec(3)");
        documents.Property(x => x.Rq4).HasColumnType("rabitq4(3)");
        documents.Property(x => x.HalfRq8).HasColumnType("rabitq8(3)");
        documents.Property(x => x.HalfRq4).HasColumnType("rabitq4(3)");
        documents.Property(x => x.Tokens).HasColumnType("vector(3)[]");
        documents.Property(x => x.HalfTokens).HasColumnType("halfvec(3)[]");
        documents.Property(x => x.Rq8Tokens).HasColumnType("rabitq8(3)[]");
        documents.Property(x => x.Rq4Tokens).HasColumnType("rabitq4(3)[]");
        documents.Property(x => x.HalfRq8Tokens).HasColumnType("rabitq8(3)[]");
        documents.Property(x => x.HalfRq4Tokens).HasColumnType("rabitq4(3)[]");
        documents.HasIndex(x => x.Dense).HasVectorChordRq(DistanceMetric.Cosine);
        documents.HasIndex(x => x.Half).HasVectorChordRq(DistanceMetric.L2);
        documents.HasIndex(x => x.Rq8).HasVectorChordRq(DistanceMetric.Cosine);
        documents.HasIndex(x => x.Rq4).HasVectorChordGraph(DistanceMetric.Cosine, o =>
        {
            o.M = 16;
            o.EfConstruction = 32;
            o.EfSearch = 64;
        });
        documents.HasIndex(x => x.Tokens).HasVectorChordRq(DistanceMetric.MaxSim);
        documents.HasIndex(x => x.Rq8Tokens).HasVectorChordRq(DistanceMetric.MaxSim);
        documents.HasIndex(x => x.Rq4Tokens).HasVectorChordRq(DistanceMetric.MaxSim);
    }

    public static TestDb Create(string? connection = null, CommandLog? log = null)
    {
        var options = new DbContextOptionsBuilder<TestDb>()
            .UseNpgsql(connection ?? "Host=localhost;Database=unused;Username=unused", o => o.UseVectorChord())
            .EnableDetailedErrors();
        if (log is not null) options.AddInterceptors(log);
        return new TestDb(options.Options);
    }
}

public sealed class Document
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int? FolderId { get; set; }
    public Folder? Folder { get; set; }
    public Vector? Dense { get; set; }
    public HalfVector? Half { get; set; }
    [Column(TypeName = "rabitq8(3)")]
    public Vector? Rq8 { get; set; }
    public Vector? Rq4 { get; set; }
    public HalfVector? HalfRq8 { get; set; }
    public HalfVector? HalfRq4 { get; set; }
    public Vector[]? Tokens { get; set; }
    public HalfVector[]? HalfTokens { get; set; }
    public Vector[]? Rq8Tokens { get; set; }
    public Vector[]? Rq4Tokens { get; set; }
    public HalfVector[]? HalfRq8Tokens { get; set; }
    public HalfVector[]? HalfRq4Tokens { get; set; }

    public static Vector V(params float[] values) => new(values);
    public static HalfVector H(params float[] values) => new(values.Select(x => (Half)x).ToArray());

    public static Document Sample() => new()
    {
        Name = "sample",
        Dense = V(1, 2, 3),
        Half = H(1, 2, 3),
        Rq8 = V(1, 2, 3),
        Rq4 = V(1, 2, 3),
        HalfRq8 = H(1, 2, 3),
        HalfRq4 = H(1, 2, 3),
        Tokens = [V(1, 2, 3), V(3, 2, 1)],
        HalfTokens = [H(1, 2, 3), H(3, 2, 1)],
        Rq8Tokens = [V(1, 2, 3), V(3, 2, 1)],
        Rq4Tokens = [V(1, 2, 3), V(3, 2, 1)],
        HalfRq8Tokens = [H(1, 2, 3), H(3, 2, 1)],
        HalfRq4Tokens = [H(1, 2, 3), H(3, 2, 1)]
    };
}

public sealed class Folder
{
    public int Id { get; set; }
    public List<Document> Documents { get; set; } = [];
}

public sealed class CommandLog : DbCommandInterceptor
{
    public List<string> Commands { get; } = [];

    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command,
        CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Commands.Add(command.CommandText);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
        CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Commands.Add(command.CommandText);
        return ValueTask.FromResult(result);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
        CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Commands.Add(command.CommandText);
        return ValueTask.FromResult(result);
    }
}

public sealed class PostgreSqlFactAttribute : FactAttribute
{
    public PostgreSqlFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("VECTORCHORD_TEST_CONNECTION") is null)
            Skip = "Set VECTORCHORD_TEST_CONNECTION to a dedicated vectorchord_dotnet_test database.";
    }
}
