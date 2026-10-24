using System;
using EfMigrationPack;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace TestApp
{
    public class TestDbContext : DbContext
    {
        public TestDbContext(DbContextOptions<TestDbContext> options)
            : base(options)
        {
        }

        // The entities live in Model.cs, which the test swaps between stages.
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            TestModel.Configure(modelBuilder);
        }
    }

    /// <summary>Used by dotnet ef. The database file comes from the EFPACK_TEST_DB environment variable.</summary>
    public class TestDbContextFactory : IDesignTimeDbContextFactory<TestDbContext>
    {
        public TestDbContext CreateDbContext(string[] args)
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = Environment.GetEnvironmentVariable("EFPACK_TEST_DB") ?? "test.db",
            }.ToString();

            var options = new DbContextOptionsBuilder<TestDbContext>()
                .UseSqlite(connectionString)
                .UsePackedMigrations()
                .Options;

            return new TestDbContext(options);
        }
    }
}
