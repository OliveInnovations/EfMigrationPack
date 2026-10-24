using System;
using System.Collections.Generic;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace TestApp
{
    public static class Program
    {
        /// <summary>
        /// "TestApp dump &lt;database&gt;" prints the migrations history and the tables' columns as one line of JSON,
        /// for Run-IntegrationTests.ps1 to check.
        /// </summary>
        public static int Main(string[] args)
        {
            if (args.Length != 2 || args[0] != "dump")
            {
                Console.Error.WriteLine("usage: TestApp dump <database file>");
                return 2;
            }

            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = args[1],
                Mode = SqliteOpenMode.ReadOnly,
            }.ToString();

            using var connection = new SqliteConnection(connectionString);
            connection.Open();

            var history = new List<string>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\" ORDER BY \"MigrationId\"";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    history.Add(reader.GetString(0));
                }
            }

            var tableNames = new List<string>();
            using (var command = connection.CreateCommand())
            {
                // Leaves out SQLite's own tables and EF's (__EFMigrationsHistory, and __EFMigrationsLock in EF Core 9+).
                command.CommandText =
                    "SELECT name FROM sqlite_master WHERE type = 'table' " +
                    "AND name NOT LIKE 'sqlite!_%' ESCAPE '!' AND name NOT LIKE '!_!_EF%' ESCAPE '!' ORDER BY name";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    tableNames.Add(reader.GetString(0));
                }
            }

            var tables = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var table in tableNames)
            {
                var columns = new List<string>();
                using var command = connection.CreateCommand();
                command.CommandText = $"PRAGMA table_info(\"{table.Replace("\"", "\"\"")}\")";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    columns.Add(reader.GetString(1));
                }

                tables[table] = columns;
            }

            Console.WriteLine(JsonSerializer.Serialize(new { history, tables }));
            return 0;
        }
    }
}
