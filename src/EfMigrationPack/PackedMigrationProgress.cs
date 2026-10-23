using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace EfMigrationPack
{
    /// <summary>
    /// Reports each packed migration as it actually runs. EF generates a pack's SQL before executing it,
    /// so progress is taken from execution: when a step's history-row command executes, that step has
    /// finished. If a command fails first, the step that was running is named.
    /// </summary>
    internal static class PackedMigrationProgress
    {
        private static readonly ConditionalWeakTable<DbContext, ContextProgress> ByContext = new();

        internal sealed class Item
        {
            public Item(string packId, string migrationId, string historyScript, int index, int total, bool up, Action<LogLevel, EventId, string> report)
            {
                PackId = packId;
                MigrationId = migrationId;
                HistoryScript = Normalize(historyScript);
                Index = index;
                Total = total;
                Up = up;
                Report = report;
            }

            public string PackId { get; }

            public string MigrationId { get; }

            /// <summary>The step's history-row SQL, whitespace-normalized for matching.</summary>
            public string HistoryScript { get; }

            public int Index { get; }

            public int Total { get; }

            public bool Up { get; }

            public Action<LogLevel, EventId, string> Report { get; }

            public bool Done { get; set; }
        }

        private sealed class ContextProgress
        {
            public readonly object Gate = new();

            // In execution order: EF executes migrations in the order it generates them.
            public readonly List<Item> Items = new();
        }

        /// <summary>Records a pack's selected steps, replacing anything recorded earlier for the same pack.</summary>
        public static void Track(DbContext context, string packId, IReadOnlyList<Item> items)
        {
            var progress = ByContext.GetValue(context, _ => new ContextProgress());
            lock (progress.Gate)
            {
                progress.Items.RemoveAll(i => i.PackId == packId);
                progress.Items.AddRange(items);
            }
        }

        public static void OnExecuted(DbContext? context, string? commandText)
        {
            if (context == null || string.IsNullOrEmpty(commandText) || !ByContext.TryGetValue(context, out var progress))
            {
                return;
            }

            Item? finished = null;
            lock (progress.Gate)
            {
                // Providers may add whitespace or wrap the SQL, so compare normalized text and allow containment.
                var text = Normalize(commandText!);
                finished = progress.Items.FirstOrDefault(i => !i.Done && text.Contains(i.HistoryScript, StringComparison.Ordinal));
                if (finished == null)
                {
                    return;
                }

                finished.Done = true;
                if (progress.Items.Where(i => i.PackId == finished.PackId).All(i => i.Done))
                {
                    progress.Items.RemoveAll(i => i.PackId == finished.PackId);
                }
            }

            finished.Report(
                LogLevel.Information,
                finished.Up ? PackedMigrationEvents.StepApplied : PackedMigrationEvents.StepReverted,
                $"{(finished.Up ? "Applied" : "Reverted")} packed migration '{finished.MigrationId}' ({finished.Index}/{finished.Total}).");
        }

        private static string Normalize(string sql) => Regex.Replace(sql, @"\s+", " ").Trim();

        public static void OnFailed(DbContext? context)
        {
            if (context == null || !ByContext.TryGetValue(context, out var progress))
            {
                return;
            }

            Item? running;
            lock (progress.Gate)
            {
                // The step that was running: the first one not finished after the last one that did.
                var lastDone = progress.Items.FindLastIndex(i => i.Done);
                running = progress.Items.Skip(lastDone + 1).FirstOrDefault(i => !i.Done);
                if (running == null)
                {
                    return;
                }

                progress.Items.RemoveAll(i => i.PackId == running.PackId);
            }

            running.Report(
                LogLevel.Error,
                PackedMigrationEvents.StepFailed,
                $"Packed migration '{running.PackId}' failed while {(running.Up ? "applying" : "reverting")} '{running.MigrationId}' ({running.Index}/{running.Total}).");
        }
    }

    /// <summary>Feeds executed and failed migration commands to <see cref="PackedMigrationProgress"/>.</summary>
    internal sealed class PackedMigrationsInterceptor : DbCommandInterceptor
    {
        public static readonly PackedMigrationsInterceptor Instance = new();

        private PackedMigrationsInterceptor()
        {
        }

        public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
        {
            Executed(command, eventData);
            return result;
        }

        public override ValueTask<int> NonQueryExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            int result,
            CancellationToken cancellationToken = default)
        {
            Executed(command, eventData);
            return new ValueTask<int>(result);
        }

        public override void CommandFailed(DbCommand command, CommandErrorEventData eventData)
        {
            Failed(eventData);
        }

        public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            Failed(eventData);
            return Task.CompletedTask;
        }

        private static void Executed(DbCommand command, CommandExecutedEventData eventData)
        {
            if (eventData.CommandSource == CommandSource.Migrations)
            {
                PackedMigrationProgress.OnExecuted(eventData.Context, command.CommandText);
            }
        }

        private static void Failed(CommandErrorEventData eventData)
        {
            if (eventData.CommandSource == CommandSource.Migrations)
            {
                PackedMigrationProgress.OnFailed(eventData.Context);
            }
        }
    }
}
