using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Pgvector;
using Pgvector.EntityFrameworkCore;
using VectorChord.EntityFrameworkCore;
using Xunit;

namespace VectorChord.Tests;

public sealed class DatabaseFixture : IAsyncLifetime
{
    public string Connection => Environment.GetEnvironmentVariable("VECTORCHORD_TEST_CONNECTION")!;
    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("VECTORCHORD_TEST_CONNECTION") is null) return;
        var name = new NpgsqlConnectionStringBuilder(Connection).Database;
        if (name is null || !name.StartsWith("vectorchord_dotnet_test", StringComparison.Ordinal))
            throw new InvalidOperationException("Tests recreate their database; its name must start with vectorchord_dotnet_test.");
        await using var db = TestDb.Create(Connection);
        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();
    }
    public Task DisposeAsync() => Task.CompletedTask;
}

public sealed class PostgreSqlTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    [PostgreSqlFact]
    public async Task Insert_and_update_quantize_and_read_back_in_the_same_command()
    {
        var log = new CommandLog();
        await using var db = TestDb.Create(fixture.Connection, log);
        var document = Document.Sample();
        db.Add(document);
        await db.SaveChangesAsync();
        Assert.Single(log.Commands);
        Assert.Contains("quantize_to_rabitq8(", log.Commands[0]);
        Assert.Contains("quantize_to_rabitq4(", log.Commands[0]);
        Assert.Contains("RETURNING", log.Commands[0]);
        Assert.Contains("dequantize_to_vector", log.Commands[0]);

        var stored = await db.Documents.AsNoTracking().SingleAsync(x => x.Id == document.Id);
        Assert.Equal(stored.Rq8, document.Rq8);
        Assert.Equal(stored.Rq4, document.Rq4);
        Assert.Equal(stored.HalfRq8, document.HalfRq8);
        Assert.Equal(stored.HalfRq4, document.HalfRq4);
        Assert.Equal(stored.Rq8Tokens, document.Rq8Tokens);
        Assert.Equal(stored.Rq4Tokens, document.Rq4Tokens);
        Assert.Equal(stored.HalfRq8Tokens, document.HalfRq8Tokens);
        Assert.Equal(stored.HalfRq4Tokens, document.HalfRq4Tokens);
        Assert.False(db.ChangeTracker.HasChanges());

        log.Commands.Clear();
        document.Rq8 = Document.V(4, 5, 6);
        document.HalfRq4 = Document.H(6, 5, 4);
        await db.SaveChangesAsync();
        Assert.Single(log.Commands);
        stored = await db.Documents.AsNoTracking().SingleAsync(x => x.Id == document.Id);
        Assert.Equal(stored.Rq8, document.Rq8);
        Assert.Equal(stored.HalfRq4, document.HalfRq4);
        Assert.False(db.ChangeTracker.HasChanges());
    }

    [PostgreSqlFact]
    public async Task Batched_inserts_refresh_each_entity_without_preliminary_quantization_queries()
    {
        var log = new CommandLog();
        await using var db = TestDb.Create(fixture.Connection, log);
        var documents = new[] { Document.Sample(), Document.Sample() };
        documents[1].Rq8 = Document.V(7, 8, 9);
        db.AddRange(documents);
        await db.SaveChangesAsync();
        Assert.Single(log.Commands);
        foreach (var document in documents)
        {
            var stored = await db.Documents.AsNoTracking().SingleAsync(x => x.Id == document.Id);
            Assert.Equal(document.Rq8, stored.Rq8);
            Assert.Equal(document.Rq4Tokens, stored.Rq4Tokens);
        }
    }

    [PostgreSqlFact]
    public async Task Compiled_queries_quantize_parameters_server_side()
    {
        await using var db = TestDb.Create(fixture.Connection);
        db.Add(Document.Sample());
        await db.SaveChangesAsync();
        var search = EF.CompileAsyncQuery((TestDb context, Vector query) =>
            context.Documents.Where(x => x.Rq8.WithinCosineDistance(query, .2f))
                .OrderBy(x => x.Rq8!.CosineDistance(query)).Take(5).Select(x => x.Id));
        var ids = new List<int>();
        await foreach (var id in search(db, Document.V(1, 2, 3))) ids.Add(id);
        Assert.NotEmpty(ids);
    }

    [PostgreSqlFact]
    public async Task Dense_and_quantized_distance_queries_execute()
    {
        await using var db = TestDb.Create(fixture.Connection);
        var document = Document.Sample();
        db.Add(document);
        await db.SaveChangesAsync();
        var query = Document.V(1, 2, 3);
        var half = Document.H(1, 2, 3);
        var halfFilter = Document.H(1, 2, 4);
        var result = await db.Documents.Where(x => x.Id == document.Id)
            .Select(x => new
            {
                Dense = x.Dense!.L2Distance(query),
                Rq8 = x.Rq8!.CosineDistance(query),
                Rq4 = x.Rq4!.MaxInnerProduct(query),
                Half = x.Half!.L2Distance(half),
                HalfRq8 = x.HalfRq8!.CosineDistance(half),
                Within = x.Rq8.WithinCosineDistance(query, .01f),
                WithinHalf = EF.Functions.WithinL2Distance(x.HalfRq4, halfFilter, 2f)
            }).SingleAsync();
        Assert.Equal(0, result.Dense);
        Assert.Equal(0, result.Half);
        Assert.InRange(result.Rq8, -.01, .01);
        Assert.True(result.Rq4 < 0);
        Assert.True(result.Within);
        Assert.True(result.WithinHalf);
    }

    [PostgreSqlFact]
    public async Task Cosine_semantics_match_pgvector_for_opposite_vectors_and_null_query_centers()
    {
        await using var db = TestDb.Create(fixture.Connection);
        var document = Document.Sample();
        db.Add(document);
        await db.SaveChangesAsync();
        var opposite = Document.V(-1, -2, -3);
        var result = await db.Documents.Where(x => x.Id == document.Id).Select(x => new
        {
            Dense = x.Dense!.CosineDistance(opposite),
            Rq8 = x.Rq8!.CosineDistance(opposite),
            Rq4 = x.Rq4!.CosineDistance(opposite),
            Match = x.Rq8.WithinCosineDistance(opposite, .2f)
        }).SingleAsync();
        Assert.InRange(result.Dense, 1.99, 2.01);
        Assert.InRange(result.Rq8, 1.99, 2.01);
        Assert.InRange(result.Rq4, 1.99, 2.01);
        Assert.False(result.Match);
        var distinctDistances = await db.Documents.Where(x => x.Rq8 != null)
            .Select(x => x.Rq8!.CosineDistance(opposite)).Distinct().OrderBy(x => x).ToListAsync();
        Assert.NotEmpty(distinctDistances);
        Vector? missing = null;
        Assert.False(await db.Documents.Where(x => x.Id == document.Id)
            .Select(x => x.Rq8.WithinCosineDistance(missing, .2f)).SingleAsync());
    }

    [PostgreSqlFact]
    public async Task Synchronous_save_and_query_use_the_same_quantization_path()
    {
        using var db = TestDb.Create(fixture.Connection);
        var document = Document.Sample();
        db.Add(document);
        db.SaveChanges();
        var stored = db.Documents.AsNoTracking().Single(x => x.Id == document.Id);
        Assert.Equal(document.Rq8, stored.Rq8);
        await Task.CompletedTask;
    }

    [PostgreSqlFact]
    public async Task MaxSim_executes_for_dense_and_both_quantized_arrays()
    {
        await using var db = TestDb.Create(fixture.Connection);
        var document = Document.Sample();
        db.Add(document);
        await db.SaveChangesAsync();
        Vector[] query = [Document.V(1, 2, 3)];
        HalfVector[] half = [Document.H(1, 2, 3)];
        var result = await db.Documents.Where(x => x.Id == document.Id)
            .Select(x => new
            {
                Dense = EF.Functions.MaxSimDistance(x.Tokens!, query),
                Rq8 = EF.Functions.MaxSimDistance(x.Rq8Tokens!, query),
                Rq4 = EF.Functions.MaxSimDistance(x.Rq4Tokens!, query),
                Half = EF.Functions.MaxSimDistance(x.HalfTokens!, half),
                HalfRq8 = EF.Functions.MaxSimDistance(x.HalfRq8Tokens!, half),
                HalfRq4 = EF.Functions.MaxSimDistance(x.HalfRq4Tokens!, half)
            }).SingleAsync();
        Assert.Equal(-14f, result.Dense);
        Assert.True(result.Rq8 < 0 && result.Rq4 < 0 && result.HalfRq8 < 0 && result.HalfRq4 < 0);
        Assert.Equal(-14f, result.Half);
    }

    [PostgreSqlFact]
    public async Task Nulls_empty_arrays_and_strict_thresholds_are_preserved()
    {
        await using var db = TestDb.Create(fixture.Connection);
        var document = new Document { Rq8Tokens = [], HalfRq4Tokens = [null!, Document.H(1, 2, 3)] };
        db.Add(document);
        await db.SaveChangesAsync();
        var stored = await db.Documents.AsNoTracking().SingleAsync(x => x.Id == document.Id);
        Assert.Null(stored.Rq8);
        Assert.Null(stored.HalfRq4);
        Assert.Empty(stored.Rq8Tokens!);
        Assert.Null(stored.HalfRq4Tokens![0]);
        Assert.NotNull(stored.HalfRq4Tokens[1]);
        var query = Document.V(1, 2, 3);
        var match = await db.Documents.Where(x => x.Id == document.Id)
            .Select(x => x.Rq8.WithinCosineDistance(query, .2f)).SingleAsync();
        Assert.False(match);
        Assert.True(await db.Documents.Where(x => x.Id == document.Id)
            .AnyAsync(x => !x.Rq8.WithinCosineDistance(query, .2f)));

        var dense = Document.Sample();
        db.Add(dense);
        await db.SaveChangesAsync();
        Assert.False(await db.Documents.Where(x => x.Id == dense.Id)
            .AnyAsync(x => x.Dense.WithinL2Distance(query, 0f)));
    }

    [PostgreSqlFact]
    public async Task Coalesced_quantized_properties_convert_fallback_parameters()
    {
        await using var db = TestDb.Create(fixture.Connection);
        var document = new Document();
        db.Add(document);
        await db.SaveChangesAsync();
        var fallback = Document.V(1, 2, 3);
        var value = await db.Documents.Where(x => x.Id == document.Id)
            .Select(x => x.Rq8 ?? fallback).SingleAsync();
        Assert.InRange(value.ToArray()[0], .9f, 1.1f);
    }

    [PostgreSqlFact]
    public async Task ExecuteUpdate_quantizes_on_server()
    {
        await using var db = TestDb.Create(fixture.Connection);
        var document = Document.Sample();
        db.Add(document);
        await db.SaveChangesAsync();
        var replacement = Document.V(4, 5, 6);
        Assert.Equal(1, await db.Documents.Where(x => x.Id == document.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Rq8, replacement)));
        var stored = await db.Documents.AsNoTracking().SingleAsync(x => x.Id == document.Id);
        Assert.NotNull(stored.Rq8);
        Assert.InRange(stored.Rq8!.ToArray()[0], 3.9f, 4.1f);
    }

    [PostgreSqlFact]
    public async Task Collection_includes_materialize_quantized_values_in_single_and_split_queries()
    {
        await using var db = TestDb.Create(fixture.Connection);
        var folder = new Folder { Documents = [Document.Sample()] };
        db.Add(folder);
        await db.SaveChangesAsync();
        foreach (var split in new[] { false, true })
        {
            var query = db.Folders.Include(x => x.Documents).AsNoTracking();
            if (split) query = query.AsSplitQuery();
            var stored = await query.SingleAsync(x => x.Id == folder.Id);
            Assert.Equal(folder.Documents[0].Rq8, stored.Documents[0].Rq8);
            Assert.Equal(folder.Documents[0].Rq8Tokens, stored.Documents[0].Rq8Tokens);
        }
    }

    [PostgreSqlFact]
    public async Task Replacing_array_elements_is_tracked_and_unchanged_values_are_not_rewritten()
    {
        var log = new CommandLog();
        await using var db = TestDb.Create(fixture.Connection, log);
        var document = Document.Sample();
        db.Add(document);
        await db.SaveChangesAsync();
        log.Commands.Clear();
        Assert.Equal(0, await db.SaveChangesAsync());
        Assert.Empty(log.Commands);
        document.Rq8Tokens![0] = Document.V(5, 6, 7);
        Assert.True(db.ChangeTracker.HasChanges());
        await db.SaveChangesAsync();
        var stored = await db.Documents.AsNoTracking().SingleAsync(x => x.Id == document.Id);
        Assert.Equal(stored.Rq8Tokens, document.Rq8Tokens);
    }

    [PostgreSqlFact]
    public async Task Storage_migrations_convert_existing_scalar_and_array_values()
    {
        await using var db = TestDb.Create(fixture.Connection);
        foreach (var array in new[] { false, true })
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            await db.Database.ExecuteSqlRawAsync(array
                ? """CREATE TABLE "StorageMigration" ("Embedding" vector(3)[]); INSERT INTO "StorageMigration" VALUES (ARRAY['[1,2,3]'::vector])"""
                : """CREATE TABLE "StorageMigration" ("Embedding" vector(3)); INSERT INTO "StorageMigration" VALUES ('[1,2,3]')""");
            var suffix = array ? "[]" : "";
            var model = db.GetService<IDesignTimeModel>().Model;
            foreach (var (from, to) in new[] { ("vector(3)", "rabitq8(3)"), ("rabitq8(3)", "rabitq4(3)"), ("rabitq4(3)", "vector(3)") })
            {
                var commands = db.GetService<IMigrationsSqlGenerator>()
                    .Generate([MigrationTests.StorageChange(from + suffix, to + suffix, array)], model);
                foreach (var command in commands)
                    await command.ExecuteNonQueryAsync(db.GetService<IRelationalConnection>());
            }
            var value = await db.Database.SqlQueryRaw<float[]>(array
                ? """SELECT "Embedding"[1]::real[] AS "Value" FROM "StorageMigration" """
                : """SELECT "Embedding"::real[] AS "Value" FROM "StorageMigration" """).SingleAsync();
            Assert.InRange(value[0], .7f, 1.3f);
            Assert.InRange(value[1], 1.7f, 2.3f);
            Assert.InRange(value[2], 2.7f, 3.3f);
            await tx.RollbackAsync();
        }
    }

    [PostgreSqlFact]
    public async Task External_data_source_uses_existing_pgvector_driver_mapping()
    {
        var builder = new NpgsqlDataSourceBuilder(fixture.Connection);
        builder.UseVector();
        await using var source = builder.Build();
        var options = new DbContextOptionsBuilder<TestDb>()
            .UseNpgsql(source, o => o.UseVectorChord()).Options;
        await using var db = new TestDb(options);
        var document = Document.Sample();
        db.Add(document);
        await db.SaveChangesAsync();
        Assert.NotNull(await db.Documents.AsNoTracking().Where(x => x.Id == document.Id).Select(x => x.Rq8).SingleAsync());
    }

    [PostgreSqlFact]
    public async Task Settings_are_transaction_local_after_commit_and_rollback()
    {
        await using var db = TestDb.Create(fixture.Connection);
        await db.Database.OpenConnectionAsync();
        foreach (var commit in new[] { false, true })
        {
            var original = await db.Database.SqlQueryRaw<string>("SELECT current_setting('vchordrq.epsilon') AS \"Value\"").SingleAsync();
            await using (var tx = await db.Database.BeginTransactionAsync())
            {
                await db.Database.SetVectorChordRqSearchOptionsAsync(new() { Epsilon = .123, Prefilter = true, Probes = [] });
                var value = await db.Database.SqlQueryRaw<string>("SELECT current_setting('vchordrq.epsilon') AS \"Value\"").SingleAsync();
                Assert.Equal("0.123", value);
                await db.Database.SetVectorChordGraphSearchOptionsAsync(new() { EfSearch = 123 });
                if (commit) await tx.CommitAsync(); else await tx.RollbackAsync();
            }
            var restored = await db.Database.SqlQueryRaw<string>("SELECT current_setting('vchordrq.epsilon') AS \"Value\"").SingleAsync();
            Assert.Equal(original, restored);
        }
    }

    [PostgreSqlFact]
    public async Task Quantized_ordering_and_range_are_index_conditions()
    {
        await using var db = TestDb.Create(fixture.Connection);
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SET LOCAL enable_seqscan = off");
        var vector = Document.V(1, 2, 3);
        IQueryable<Vector?>[] queries =
        [
            db.Documents.Where(x => x.Rq8.WithinCosineDistance(vector, .2f))
                .OrderBy(x => x.Rq8!.CosineDistance(vector)).Take(5).Select(x => x.Rq8),
            db.Documents.Where(x => x.Rq4.WithinCosineDistance(vector, .2f))
                .OrderBy(x => x.Rq4!.CosineDistance(vector)).Take(5).Select(x => x.Rq4)
        ];
        foreach (var query in queries)
        {
            await using var command = query.CreateDbCommand();
            command.Transaction = tx.GetDbTransaction();
            command.CommandText = "EXPLAIN (COSTS OFF) " + command.CommandText;
            await using var reader = await command.ExecuteReaderAsync();
            var plan = new List<string>();
            while (await reader.ReadAsync()) plan.Add(reader.GetString(0));
            var text = string.Join("\n", plan);
            Assert.Contains("Index Scan", text);
            Assert.Contains("Index Cond:", text);
            Assert.Contains("Order By:", text);
        }
    }
}
