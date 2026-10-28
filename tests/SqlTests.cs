using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Pgvector;
using Pgvector.EntityFrameworkCore;
using VectorChord.EntityFrameworkCore;
using Xunit;

namespace VectorChord.Tests;

public sealed class SqlTests
{
    [Fact]
    public void Quantized_projection_is_dequantized_but_ordering_is_not()
    {
        using var db = TestDb.Create();
        var query = Document.V(1, 2, 3);
        var sql = db.Documents.OrderBy(x => x.Rq8!.CosineDistance(query)).Take(5).ToQueryString();
        Assert.Contains("dequantize_to_vector", sql);
        var ordering = sql[sql.LastIndexOf("ORDER BY", StringComparison.Ordinal)..];
        Assert.Contains("<=> quantize_to_rabitq8(", ordering);
        Assert.DoesNotContain("dequantize", ordering);
    }

    [Fact]
    public void Fluent_and_DbFunctions_filters_translate_identically()
    {
        using var db = TestDb.Create();
        var query = Document.V(1, 2, 3);
        var fluent = db.Documents.Where(x => x.Rq8.WithinCosineDistance(query, .2f)).ToQueryString();
        var functions = db.Documents.Where(x => EF.Functions.WithinCosineDistance(x.Rq8, query, .2f)).ToQueryString();
        Assert.Equal(fluent, functions);
        Assert.Contains("<<=>> sphere(quantize_to_rabitq8(", fluent);
        Assert.Contains("0.2 - 1", fluent);
    }

    [Fact]
    public void Dense_and_half_filters_use_their_own_sphere_types()
    {
        using var db = TestDb.Create();
        var vector = Document.V(1, 2, 3);
        var half = Document.H(1, 2, 3);
        Assert.Contains("<<->> sphere(", db.Documents.Where(x => x.Half.WithinL2Distance(half, 1f)).ToQueryString());
        Assert.Contains("<<#>> sphere(", db.Documents.Where(x => x.Dense.WithinNegativeInnerProduct(vector, -1f)).ToQueryString());
    }

    [Fact]
    public void Index_operator_classes_follow_storage_not_CLR_type()
    {
        using var db = TestDb.Create();
        var indexes = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(Document))!.GetIndexes()
            .ToDictionary(x => x.Properties.Single().Name);
        Assert.Equal(["rabitq8_cosine_ops"], indexes["Rq8"].GetOperators());
        Assert.Equal(["rabitq4_cosine_ops"], indexes["Rq4"].GetOperators());
        Assert.Equal(["rabitq8_maxsim_ops"], indexes["Rq8Tokens"].GetOperators());
        Assert.Equal("vchordg", indexes["Rq4"].GetMethod());
        Assert.DoesNotContain(indexes.Values, x => x.FindAnnotation("VectorChord:DistanceMetric") is not null);
    }

    [Fact]
    public void Create_script_contains_extensions_quantized_columns_and_indexes()
    {
        using var db = TestDb.Create();
        var script = db.Database.GenerateCreateScript();
        Assert.Contains("CREATE EXTENSION IF NOT EXISTS vector", script);
        Assert.Contains("CREATE EXTENSION IF NOT EXISTS vchord", script);
        Assert.Contains("rabitq8(3)", script);
        Assert.Contains("USING vchordrq", script);
        Assert.Contains("USING vchordg", script);
        Assert.Contains("rabitq8_maxsim_ops", script);
    }

    [Fact]
    public void MaxSim_uses_negative_score_operator_and_quantizes_array_parameter()
    {
        using var db = TestDb.Create();
        Vector[] query = [Document.V(1, 2, 3)];
        var sql = db.Documents.OrderBy(x => EF.Functions.MaxSimDistance(x.Rq8Tokens!, query)).ToQueryString();
        Assert.Contains(" @# ", sql);
        Assert.Contains("quantize_to_rabitq8(__vchord.value)", sql);
        Assert.Contains("WITH ORDINALITY", sql);
        Assert.DoesNotContain("__vectorchord_array_", sql);
    }

    [Fact]
    public void Subquery_keeps_quantized_values_until_outer_materialization()
    {
        using var db = TestDb.Create();
        var query = Document.V(1, 2, 3);
        var sql = db.Documents.OrderBy(x => x.Id).Take(10)
            .OrderBy(x => x.Rq8!.CosineDistance(query))
            .Select(x => x.Rq8).ToQueryString();
        Assert.Equal(1, sql.Split("dequantize_to_vector").Length - 1);
        Assert.Contains("quantize_to_rabitq8(", sql);
    }

    [Fact]
    public void Client_execution_has_a_clear_error()
    {
        Assert.Throws<InvalidOperationException>(() =>
            Document.V(1, 2, 3).WithinCosineDistance(Document.V(1, 2, 3), .2f));
    }

    [Fact]
    public async Task Search_overrides_require_transaction()
    {
        await using var db = TestDb.Create();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            db.Database.SetVectorChordRqSearchOptionsAsync(new() { Probes = [10] }));
    }

    [Fact]
    public void Invalid_index_settings_fail_before_connecting()
    {
        var model = new ModelBuilder();
        var entity = model.Entity<Document>();
        Assert.Throws<ArgumentException>(() => entity.HasIndex(x => x.Dense)
            .HasVectorChordRq(DistanceMetric.L2, o => { o.Lists = [100, 10]; }));
        Assert.Throws<ArgumentException>(() => entity.HasIndex(x => x.Dense)
            .HasVectorChordRq(DistanceMetric.L2, o => { o.Lists = [100]; o.Probes = [1, 2]; }));
        Assert.Throws<ArgumentOutOfRangeException>(() => entity.HasIndex(x => x.Dense)
            .HasVectorChordRq(DistanceMetric.L2, o => o.Epsilon = double.NaN));
        Assert.Throws<ArgumentException>(() => entity.HasIndex(x => x.Tokens)
            .HasVectorChordGraph(DistanceMetric.MaxSim));
    }
}
