using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace EfMigrationPack
{
    /// <summary>Enables packed migrations on a DbContext.</summary>
    public static class PackedMigrationsDbContextOptionsBuilderExtensions
    {
        /// <summary>
        /// Enables packed migrations. Call it everywhere the context is configured, including any
        /// <c>IDesignTimeDbContextFactory</c>, so <c>dotnet ef</c> and the Package Manager Console use it too.
        /// </summary>
        /// <example>
        /// <code>
        /// services.AddDbContext&lt;AppDbContext&gt;(options => options
        ///     .UseSqlServer(connectionString)
        ///     .UsePackedMigrations());
        /// </code>
        /// </example>
        public static DbContextOptionsBuilder UsePackedMigrations(
            this DbContextOptionsBuilder optionsBuilder,
            Action<PackedMigrationsOptionsBuilder>? configure = null)
        {
            if (optionsBuilder == null)
            {
                throw new ArgumentNullException(nameof(optionsBuilder));
            }

            var builder = new PackedMigrationsOptionsBuilder(
                optionsBuilder.Options.FindExtension<PackedMigrationsOptionsExtension>() ?? new PackedMigrationsOptionsExtension());
            configure?.Invoke(builder);
            ((IDbContextOptionsBuilderInfrastructure)optionsBuilder).AddOrUpdateExtension(builder.Extension);

            optionsBuilder.ReplaceService<IMigrationsAssembly, PackedMigrationsAssembly>();

            var interceptors = optionsBuilder.Options.FindExtension<CoreOptionsExtension>()?.Interceptors;
            if (interceptors == null || !interceptors.Contains(PackedMigrationsInterceptor.Instance))
            {
                optionsBuilder.AddInterceptors(PackedMigrationsInterceptor.Instance);
            }

            return optionsBuilder;
        }

        /// <inheritdoc cref="UsePackedMigrations(DbContextOptionsBuilder, Action{PackedMigrationsOptionsBuilder})"/>
        public static DbContextOptionsBuilder<TContext> UsePackedMigrations<TContext>(
            this DbContextOptionsBuilder<TContext> optionsBuilder,
            Action<PackedMigrationsOptionsBuilder>? configure = null)
            where TContext : DbContext
        {
            return (DbContextOptionsBuilder<TContext>)UsePackedMigrations((DbContextOptionsBuilder)optionsBuilder, configure);
        }
    }

    /// <summary>Options for <see cref="PackedMigrationsDbContextOptionsBuilderExtensions.UsePackedMigrations(DbContextOptionsBuilder, Action{PackedMigrationsOptionsBuilder})"/>.</summary>
    public sealed class PackedMigrationsOptionsBuilder
    {
        internal PackedMigrationsOptionsBuilder(PackedMigrationsOptionsExtension extension)
        {
            Extension = extension;
        }

        internal PackedMigrationsOptionsExtension Extension { get; private set; }

        /// <summary>
        /// Also write each packed migration to <see cref="Console"/> as it runs. Messages always go to EF Core's
        /// migrations logger, which <c>dotnet ef</c>, <c>Update-Database</c> and migration bundles print, so this is
        /// only needed when the application calls <c>Migrate()</c> without console logging configured.
        /// </summary>
        public PackedMigrationsOptionsBuilder WriteToConsole(bool enabled = true)
        {
            Extension = Extension.WithWriteToConsole(enabled);
            return this;
        }
    }

    /// <summary>Holds the packed-migrations options on the DbContext options.</summary>
    public sealed class PackedMigrationsOptionsExtension : IDbContextOptionsExtension
    {
        private DbContextOptionsExtensionInfo? _info;

        /// <summary>Creates the extension with default options.</summary>
        public PackedMigrationsOptionsExtension()
        {
        }

        private PackedMigrationsOptionsExtension(PackedMigrationsOptionsExtension copyFrom)
        {
            WriteToConsole = copyFrom.WriteToConsole;
        }

        /// <summary>Whether progress is also written to the console.</summary>
        public bool WriteToConsole { get; private set; }

        /// <inheritdoc />
        public DbContextOptionsExtensionInfo Info => _info ??= new ExtensionInfo(this);

        internal PackedMigrationsOptionsExtension WithWriteToConsole(bool enabled)
        {
            return new PackedMigrationsOptionsExtension(this) { WriteToConsole = enabled };
        }

        /// <inheritdoc />
        public void ApplyServices(IServiceCollection services)
        {
            // Nothing to add: UsePackedMigrations replaces IMigrationsAssembly through ReplaceService.
        }

        /// <inheritdoc />
        public void Validate(IDbContextOptions options)
        {
        }

        private sealed class ExtensionInfo : DbContextOptionsExtensionInfo
        {
            public ExtensionInfo(IDbContextOptionsExtension extension)
                : base(extension)
            {
            }

            private new PackedMigrationsOptionsExtension Extension => (PackedMigrationsOptionsExtension)base.Extension;

            public override bool IsDatabaseProvider => false;

            public override string LogFragment => Extension.WriteToConsole ? "PackedMigrations(WriteToConsole) " : "PackedMigrations ";

            // Options are read per context, so they never require a different internal service provider.
            public override int GetServiceProviderHashCode() => 0;

            public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other) => other is ExtensionInfo;

            public override void PopulateDebugInfo(IDictionary<string, string> debugInfo)
            {
                debugInfo["EfMigrationPack:WriteToConsole"] = Extension.WriteToConsole ? "1" : "0";
            }
        }
    }
}
