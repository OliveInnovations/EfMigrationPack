using System;
using Microsoft.EntityFrameworkCore.Migrations;

namespace EfMigrationPack
{
    /// <summary>
    /// An original migration preserved inside a pack. ef-pack rewrites the original
    /// <c>protected override void Up/Down</c> into <c>public override</c> overrides of these members.
    /// </summary>
    public abstract class PackedStep
    {
        /// <summary>Mirrors <see cref="Migration.ActiveProvider"/> so original code that reads it still compiles.</summary>
        public string? ActiveProvider { get; internal set; }

        /// <summary>The original migration's Up.</summary>
        public abstract void Up(MigrationBuilder migrationBuilder);

        /// <summary>The original migration's Down. Throws, like <see cref="Migration"/>, if it never defined one.</summary>
        public virtual void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException($"Packed step '{GetType().Name}' has no Down implementation.");
        }
    }

    /// <summary>A step's original migration id and how to create it. Created by generated code.</summary>
    public sealed class PackedStepEntry
    {
        internal PackedStepEntry(string migrationId, Func<PackedStep> create)
        {
            MigrationId = migrationId;
            Create = create;
        }

        /// <summary>The id the migration had before it was packed.</summary>
        public string MigrationId { get; }

        internal Func<PackedStep> Create { get; }
    }
}
