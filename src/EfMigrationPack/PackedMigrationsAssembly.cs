#pragma warning disable EF1001 // MigrationsAssembly lives in an EF Core ".Internal" namespace. Its constructor is the same in EF Core 6 to 10.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Internal;
using Microsoft.Extensions.Logging;

namespace EfMigrationPack
{
    /// <summary>
    /// Replaces EF Core's <see cref="IMigrationsAssembly"/> so packed migrations can read the history table.
    /// Registered by <see cref="PackedMigrationsDbContextOptionsBuilderExtensions.UsePackedMigrations(DbContextOptionsBuilder, Action{PackedMigrationsOptionsBuilder})"/>.
    /// </summary>
    public class PackedMigrationsAssembly : MigrationsAssembly
    {
        private readonly ICurrentDbContext _currentContext;
        private readonly IDiagnosticsLogger<DbLoggerCategory.Migrations> _logger;
        private readonly bool _writeToConsole;

        /// <summary>Called by EF Core's dependency injection.</summary>
        public PackedMigrationsAssembly(
            ICurrentDbContext currentContext,
            IDbContextOptions options,
            IMigrationsIdGenerator idGenerator,
            IDiagnosticsLogger<DbLoggerCategory.Migrations> logger)
            : base(currentContext, options, idGenerator, logger)
        {
            _currentContext = currentContext;
            _logger = logger;
            _writeToConsole = options.FindExtension<PackedMigrationsOptionsExtension>()?.WriteToConsole ?? false;
        }

        /// <inheritdoc />
        public override Migration CreateMigration(TypeInfo migrationClass, string activeProvider)
        {
            var migration = base.CreateMigration(migrationClass, activeProvider);
            if (migration is PackedMigration packed)
            {
                packed.Attach(new PackedMigrationRuntime(_currentContext.Context, _logger, _writeToConsole));
            }

            return migration;
        }
    }

    /// <summary>What a packed migration needs while EF generates its SQL. One per migration instance.</summary>
    internal sealed class PackedMigrationRuntime
    {
        private readonly IDiagnosticsLogger<DbLoggerCategory.Migrations> _logger;
        private readonly bool _writeToConsole;
        private IHistoryRepository? _history;
        private HashSet<string>? _applied;

        public PackedMigrationRuntime(DbContext context, IDiagnosticsLogger<DbLoggerCategory.Migrations> logger, bool writeToConsole)
        {
            Context = context;
            _logger = logger;
            _writeToConsole = writeToConsole;
        }

        public DbContext Context { get; }

        public IHistoryRepository History => _history ??= Context.GetService<IHistoryRepository>();

        // Read lazily: only when EF actually evaluates Up/Down to generate SQL.
        public HashSet<string> AppliedMigrations =>
            _applied ??= new HashSet<string>(History.GetAppliedMigrations().Select(row => row.MigrationId), StringComparer.Ordinal);

        /// <summary>
        /// Writes through EF Core's migrations logger (shown by dotnet ef, Update-Database and bundles,
        /// and by the application's logging), and to the console as well when WriteToConsole is set.
        /// </summary>
        public void Report(LogLevel level, EventId eventId, string message)
        {
            _logger.Logger.Log(level, eventId, "{PackedMigrationMessage}", message);
            if (_writeToConsole && level >= LogLevel.Information)
            {
                Console.WriteLine(message);
            }
        }
    }

    /// <summary>Event ids used for ef-pack's log messages (category Microsoft.EntityFrameworkCore.Migrations).</summary>
    public static class PackedMigrationEvents
    {
        /// <summary>How many of a pack's migrations will be applied or reverted.</summary>
        public static readonly EventId Summary = new(46100, "EfMigrationPack.Summary");

        /// <summary>A packed migration was skipped (Debug).</summary>
        public static readonly EventId StepSkipped = new(46101, "EfMigrationPack.StepSkipped");

        /// <summary>A packed migration finished applying.</summary>
        public static readonly EventId StepApplied = new(46102, "EfMigrationPack.StepApplied");

        /// <summary>A packed migration finished reverting.</summary>
        public static readonly EventId StepReverted = new(46103, "EfMigrationPack.StepReverted");

        /// <summary>A command failed while a packed migration was running.</summary>
        public static readonly EventId StepFailed = new(46104, "EfMigrationPack.StepFailed");
    }
}
