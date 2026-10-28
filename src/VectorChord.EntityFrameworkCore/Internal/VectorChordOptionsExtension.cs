using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Update;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace VectorChord.EntityFrameworkCore.Internal;

internal sealed class VectorChordOptionsExtension : IDbContextOptionsExtension
{
    private DbContextOptionsExtensionInfo? _info;
    public DbContextOptionsExtensionInfo Info => _info ??= new ExtensionInfo(this);

    public void ApplyServices(IServiceCollection services)
    {
        // These must precede pgvector's general-purpose mappings and translators.
        services.Insert(0, ServiceDescriptor.Singleton<IRelationalTypeMappingSourcePlugin, QuantizedVectorTypeMappingPlugin>());
        services.Insert(0, ServiceDescriptor.Scoped<IMethodCallTranslatorPlugin, VectorChordTranslatorPlugin>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConventionSetPlugin, VectorChordConventionPlugin>());
        services.Replace(ServiceDescriptor.Singleton<IUpdateSqlGenerator, VectorChordUpdateSqlGenerator>());
        services.Replace(ServiceDescriptor.Scoped<IMigrationsSqlGenerator, VectorChordMigrationsSqlGenerator>());
        services.Replace(ServiceDescriptor.Singleton<IModificationCommandFactory, VectorChordModificationCommandFactory>());
        services.Replace(ServiceDescriptor.Singleton<IQuerySqlGeneratorFactory, VectorChordQuerySqlGeneratorFactory>());
        services.Replace(ServiceDescriptor.Scoped<IQueryTranslationPostprocessorFactory, VectorChordPostprocessorFactory>());
    }

    public void Validate(IDbContextOptions options) { }

    private sealed class ExtensionInfo(IDbContextOptionsExtension extension) : DbContextOptionsExtensionInfo(extension)
    {
        public override bool IsDatabaseProvider => false;
        public override string LogFragment => "using VectorChord ";
        public override int GetServiceProviderHashCode() => 0;
        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other)
            => other is ExtensionInfo;
        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo)
            => debugInfo["VectorChord:Enabled"] = "1";
    }
}
