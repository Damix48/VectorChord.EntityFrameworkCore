using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Design;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql.EntityFrameworkCore.PostgreSQL.Design.Internal;
using Pgvector;
using Xunit;

namespace VectorChord.Tests;

public sealed class MigrationTests
{
    [Fact]
    public void Snapshot_generation_preserves_standard_storage_and_index_annotations()
    {
        using var db = TestDb.Create();
        var services = new ServiceCollection();
        services.AddEntityFrameworkDesignTimeServices();
        services.AddDbContextDesignTimeServices(db);
        new NpgsqlDesignTimeServices().ConfigureDesignTimeServices(services);
        using var provider = services.BuildServiceProvider();
        var output = new IndentedStringBuilder();
        provider.GetRequiredService<ICSharpSnapshotGenerator>()
            .Generate("modelBuilder", db.GetService<IDesignTimeModel>().Model, output);
        var code = output.ToString();
        Assert.Contains("rabitq8(3)", code);
        Assert.Contains("rabitq8_cosine_ops", code);
        Assert.Contains("ValueGeneratedOnAddOrUpdate", code);
        Assert.DoesNotContain("VectorChord:DistanceMetric", code);

        var source = """
            using Microsoft.EntityFrameworkCore;
            using Microsoft.EntityFrameworkCore.Infrastructure;
            using Microsoft.EntityFrameworkCore.Metadata;
            using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
            using Pgvector;
            public sealed class GeneratedSnapshot : ModelSnapshot
            {
                protected override void BuildModel(ModelBuilder modelBuilder)
                {
            """ + code + "\n}}";
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("Snapshot_" + Guid.NewGuid().ToString("N"),
            [CSharpSyntaxTree.ParseText(source)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Where(x => x.Severity == DiagnosticSeverity.Error)));
        var assembly = Assembly.Load(stream.ToArray());
        var snapshot = (ModelSnapshot)Activator.CreateInstance(assembly.GetType("GeneratedSnapshot")!)!;
        var restored = db.GetService<IModelRuntimeInitializer>().Initialize(snapshot.Model, designTime: true);
        Assert.False(db.GetService<IMigrationsModelDiffer>().HasDifferences(
            restored.GetRelationalModel(), db.GetService<IDesignTimeModel>().Model.GetRelationalModel()));
    }

    [Fact]
    public void Extensions_are_created_once_with_dependencies_first()
    {
        using var db = TestDb.Create();
        var model = db.GetService<IDesignTimeModel>().Model;
        var operations = db.GetService<IMigrationsModelDiffer>()
            .GetDifferences(null, model.GetRelationalModel());
        var generator = db.GetService<IMigrationsSqlGenerator>();

        var first = string.Join("\n", generator.Generate(operations, model).Select(x => x.CommandText));
        var second = string.Join("\n", generator.Generate(operations, model).Select(x => x.CommandText));

        Assert.Equal(first, second);
        Assert.Single(first.Split('\n'), line => line.Contains("CREATE EXTENSION IF NOT EXISTS vector", StringComparison.Ordinal));
        Assert.Single(first.Split('\n'), line => line.Contains("CREATE EXTENSION IF NOT EXISTS vchord", StringComparison.Ordinal));
        Assert.True(
            first.IndexOf("CREATE EXTENSION IF NOT EXISTS vector", StringComparison.Ordinal)
            < first.IndexOf("CREATE EXTENSION IF NOT EXISTS vchord", StringComparison.Ordinal));
    }

    [Fact]
    public void Repeated_generation_is_deterministic_and_does_not_mutate_operations()
    {
        using var db = TestDb.Create();
        var operation = StorageChange("vector(3)", "rabitq8(3)", false);
        var generator = db.GetService<IMigrationsSqlGenerator>();
        var model = db.GetService<IDesignTimeModel>().Model;
        var first = generator.Generate([operation], model).Select(x => x.CommandText).ToArray();
        var second = generator.Generate([operation], model).Select(x => x.CommandText).ToArray();
        Assert.Equal(first, second);
        Assert.Equal("vector(3)", operation.OldColumn.ColumnType);
        Assert.Contains("USING quantize_to_rabitq8", string.Join("\n", first));
    }

    internal static AlterColumnOperation StorageChange(string from, string to, bool array) => new()
    {
        Table = "StorageMigration",
        Name = "Embedding",
        ClrType = array ? typeof(Vector[]) : typeof(Vector),
        ColumnType = to,
        IsNullable = true,
        OldColumn = new AddColumnOperation
        {
            ClrType = array ? typeof(Vector[]) : typeof(Vector),
            ColumnType = from,
            IsNullable = true
        }
    };
}
