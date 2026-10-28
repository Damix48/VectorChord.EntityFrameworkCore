# VectorChord.EntityFrameworkCore

> **Unofficial community integration.** This project and its author are not
> affiliated with, endorsed by, or sponsored by TensorChord or the VectorChord project.

VectorChord integration for **.NET 10, EF Core 10, and Npgsql 10**.
Tested against **PostgreSQL 17, pgvector 0.8.2, and VectorChord 1.1.1**.

Reuses `Pgvector.Vector` and `Pgvector.HalfVector`. RaBitQ is a **database storage
choice**, not another CLR type. Quantization and dequantization run in PostgreSQL.

## Installation

```sh
# nuget.org
dotnet add package VectorChord.EntityFrameworkCore
```

The same package is mirrored to GitHub Packages, which you can add as an extra source:

```text
https://nuget.pkg.github.com/Damix48/index.json
```

[Authenticate the feed](https://docs.github.com/en/packages/working-with-a-github-packages-registry/working-with-the-nuget-registry#authenticating-to-github-packages)
using a classic personal access token with `read:packages`, or an authorized
workflow's `GITHUB_TOKEN`. Authentication is required even for public packages.
Keep nuget.org enabled for dependencies, and never commit tokens to NuGet configuration.

## Setup

```csharp
using Microsoft.EntityFrameworkCore;
using Pgvector;
using Pgvector.EntityFrameworkCore;
using VectorChord.EntityFrameworkCore;

services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(connectionString, npgsql => npgsql.UseVectorChord()));
```

The server must already have pgvector and VectorChord installed and configured
(including `shared_preload_libraries`). Client registration does not execute DDL.

Declare the database extensions in the model if migrations should provision them:

```csharp
protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    modelBuilder.HasVectorChordExtension();
}
```

The migration generator ensures `vector` is created before `vchord`. Omit this
declaration if your DBA provisions extensions separately.

For an externally managed data source, configure its existing pgvector mappings
before building it, and keep the data source alive for the application's lifetime:

```csharp
using Npgsql;

var builder = new NpgsqlDataSourceBuilder(connectionString);
builder.UseVector();
var dataSource = builder.Build();

services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(dataSource, npgsql => npgsql.UseVectorChord()));
```

## Properties and storage

```csharp
public sealed class Document
{
    public int Id { get; set; }
    public required Vector Embedding { get; set; }
}
```

Choose the PostgreSQL storage type:

```csharp
modelBuilder.Entity<Document>()
    .Property(x => x.Embedding)
    .HasColumnType("vector(1536)");   // full precision

// Alternatives, configured instead of the above:
// .HasColumnType("halfvec(1536)")   // use a HalfVector property
// .HasColumnType("rabitq8(1536)")   // Vector or HalfVector
// .HasColumnType("rabitq4(1536)")   // Vector or HalfVector
```

The standard `[Column(TypeName = "rabitq8(1536)")]` attribute works too.
`HasVectorDimensions(1536)` sets dimensions while preserving an already selected
RaBitQ storage type.

A `Vector[]` or `HalfVector[]` property can use `vector(128)[]`,
`halfvec(128)[]`, `rabitq8(128)[]`, or `rabitq4(128)[]` as appropriate.
Null arrays, empty arrays, and order are preserved. VectorChord's indexes reject
arrays containing null elements; do not insert those into indexed properties.

### Ordinary writes, PostgreSQL-side quantization

```csharp
db.Documents.Add(new Document
{
    Embedding = new Vector(embeddingValues)
});
await db.SaveChangesAsync();
```

For RaBitQ storage, PostgreSQL quantizes inserts and updates automatically.
Reads return dequantized `Vector` or `HalfVector` values. After `SaveChanges`,
the tracked property is refreshed with the stored, approximate value.
`ExecuteUpdateAsync` also quantizes assignments, but does not refresh tracked entities.

**Quantization is lossy.** Saving or reloading a quantized property may change its
coordinates. Keep original vectors elsewhere if you need exact reconstruction.
RaBitQ properties cannot be keys, concurrency tokens, computed columns, or use
SQL defaults. Do not override their value-generation/save behavior.

## Indexes

### RQ

```csharp
modelBuilder.Entity<Document>()
    .HasIndex(x => x.Embedding)
    .HasVectorChordRq(DistanceMetric.Cosine);
```

The operator class follows the **stored type**, for example
`vector_cosine_ops` or `rabitq8_cosine_ops`.

Common tuning options:

```csharp
modelBuilder.Entity<Document>()
    .HasIndex(x => x.Embedding)
    .HasVectorChordRq(DistanceMetric.Cosine, rq =>
    {
        rq.Lists = [1000];
        rq.BuildThreads = 4;
        rq.Probes = [10];       // index-level search fallback
        rq.Epsilon = 1.9;
    });
```

`ResidualQuantization` and `SphericalCentroids` are also available.
Residual quantization is not supported for RaBitQ columns.

`Probes`, `Epsilon`, `MaxSimRefine`, and `MaxSimThreshold` set default search
options for this index. Unspecified values retain server defaults.

### Graph

```csharp
modelBuilder.Entity<Document>()
    .HasIndex(x => x.Embedding)
    .HasVectorChordGraph(DistanceMetric.Cosine, graph =>
    {
        graph.Bits = 2;
        graph.M = 32;
        graph.EfConstruction = 64;
        graph.EfSearch = 128;
    });
```

The graph access method is marked preview in VectorChord's documentation.
It does not support MaxSim.

Metrics are `L2`, `Cosine`, `NegativeInnerProduct`, and (RQ arrays only) `MaxSim`.
Indexes must be non-unique and single-column.

Advanced options remain available through Npgsql's `HasMethod`, `HasOperators`,
`HasStorageParameter`, and `IsCreatedConcurrently` APIs. Treat raw SQL/TOML as
trusted migration configuration.

## Queries

Use pgvector's existing distance methods:

```csharp
var query = new Vector(queryValues);

var matches = await db.Documents
    .OrderBy(x => x.Embedding.CosineDistance(query))
    .Take(10)
    .Select(x => new
    {
        x.Id,
        Distance = x.Embedding.CosineDistance(query)
    })
    .ToListAsync();
```

Supported RaBitQ distances: `L2Distance`, `CosineDistance`, and
`MaxInnerProduct`. The last returns **negative inner product**, so lower is better.

### Index-aware similarity filters

```csharp
var matches = await db.Documents
    .Where(x => x.Embedding.WithinCosineDistance(query, 0.2f))
    .OrderBy(x => x.Embedding.CosineDistance(query))
    .Take(10)
    .ToListAsync();
```

Equivalent:

```csharp
.Where(x => EF.Functions.WithinCosineDistance(x.Embedding, query, 0.2f))
```

Also available: `WithinL2Distance` and `WithinNegativeInnerProduct`, for both
`Vector` and `HalfVector`.

Thresholds are **strictly less than**, not less-than-or-equal. Null vectors or
centers do not match. Negative thresholds are meaningful for negative inner
product. These methods are server-only and throw if called outside EF queries.

Ordinary numeric distance comparisons are not rewritten into similarity filters.

For quantized storage, query inputs are quantized inside the SQL command; the
indexed column stays quantized during filtering and ordering. Dequantization is
only used when returning vector values to the application.

### Cosine compatibility

`CosineDistance` uses pgvector's `1 - cosine similarity` scale for all storage types.
VectorChord 1.1.1's raw RaBitQ cosine operators use a different scale; the library
adjusts scores and thresholds automatically. Verify compatibility before upgrading
VectorChord.

### Multi-vector / MaxSim

For a `Vector[] TokenEmbeddings` property:

```csharp
modelBuilder.Entity<Document>()
    .HasIndex(x => x.TokenEmbeddings)
    .HasVectorChordRq(DistanceMetric.MaxSim);

var matches = await db.Documents
    .OrderBy(x => EF.Functions.MaxSimDistance(x.TokenEmbeddings, queryTokens))
    .Take(10)
    .ToListAsync();
```

MaxSim translates to `@#`, which is **negative similarity**: lower is better.
The document array goes first; operand order matters. Dense, half-precision, and
both RaBitQ array storage types are supported.

## Temporary search settings

Prefer index-level fallbacks for normal operation. For temporary overrides:

```csharp
await using var tx = await db.Database.BeginTransactionAsync();

await db.Database.SetVectorChordRqSearchOptionsAsync(new()
{
    Probes = [20],
    Epsilon = 1.5,
    Prefilter = true
});

var matches = await db.Documents
    .OrderBy(x => x.Embedding.CosineDistance(query))
    .Take(10)
    .ToListAsync();

await tx.CommitAsync();
```

An active EF transaction is required. Overrides last until commit/rollback and
apply to all searches in that transaction, not a single index or LINQ expression.
They take precedence over index defaults. Probe levels must match the index's
partition hierarchy.

RQ also exposes `MaxScanTuples`, `MaxSimRefine`, and `MaxSimThreshold`.
`SetVectorChordGraphSearchOptionsAsync` exposes `EfSearch`, `BeamSearch`, and
`MaxScanTuples`.

## Migrations and operations

- Changing between dense and RaBitQ storage converts existing vectors. This is
  lossy; converting back cannot recover the original values.
- Review migration scripts. Type changes can lock/rewrite tables and rebuild indexes.
  Index-setting changes may result in drop/recreate, not a cheap `ALTER INDEX`.
- RQ partition training uses the data present at index creation. For bulk imports,
  load representative data before building a partitioned index.
- Raw SQL/direct Npgsql operations do not get EF's automatic storage conversions.
  For binary `COPY`, stage ordinary vectors and use `INSERT ... SELECT quantize_to_...`.

The supported vector API is distance queries, similarity filters, MaxSim, and
materialization. Arbitrary vector arithmetic/equality, general LINQ operations on
quantized arrays, reverse engineering, and compiled models are not part of the
tested surface. Dense and quantized columns in one distance operation must have
compatible representations; comparisons between different RaBitQ formats are rejected.

VectorChord 1.1.1 can produce `NaN` for some near-zero quantized L2 distances due to
floating-point rounding. The library preserves the server's result rather than
silently substituting another metric.

## Releasing

Pushing a SemVer tag (`v1.2.3` or `v1.2.3-rc.1`) or publishing a GitHub release packs
the library and pushes it to nuget.org and GitHub Packages. The version comes from the
tag; branch builds use `<Version>` from the project file and never publish.

nuget.org publishing uses
[trusted publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing),
so no long-lived API key is stored. Maintainer setup, once: on nuget.org, under
**Trusted Publishing**, add a policy with Repository Owner `Damix48`, Repository
`VectorChord.EntityFrameworkCore`, Workflow File `ci.yml` (file name only, no path),
Environment empty. The nuget.org profile name is in `ci.yml`; it is not a credential.

CI exchanges the OIDC token for a short-lived API key immediately before pushing;
those keys live one hour and are single-use, so the login step must stay next to the
push step.

## Credits

This library builds on the work of the maintainers and contributors of:

- [VectorChord](https://github.com/tensorchord/VectorChord), developed by TensorChord and its contributors.
- [pgvector](https://github.com/pgvector/pgvector) and [pgvector-dotnet](https://github.com/pgvector/pgvector-dotnet).
- [Npgsql's EF Core provider](https://github.com/npgsql/efcore.pg) and [Entity Framework Core](https://github.com/dotnet/efcore).

Upstream projects retain their own licenses. Project names and trademarks belong
to their respective owners; their use here identifies compatibility, not endorsement.
