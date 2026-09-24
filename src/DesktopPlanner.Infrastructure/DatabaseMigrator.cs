using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace DesktopPlanner.Infrastructure;

public static class DatabaseMigrator
{
    public const string Baseline = "20260920213307_InitialFoundation";
    public static async Task UpgradeAsync(PlannerDbContext db, string path)
    {
        await db.Database.OpenConnectionAsync();
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        var schema = await ReadSchemaAsync(connection);
        var applied = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
        var pending = (await db.Database.GetPendingMigrationsAsync()).ToArray();
        if (schema.Count > 0 && applied.Length == 0)
        {
            // Compare with the immutable initial migration, never with a future model.
            await using var expectedConnection = new SqliteConnection("Data Source=:memory:");
            await expectedConnection.OpenAsync();
            await using var expected = new PlannerDbContext(new DbContextOptionsBuilder<PlannerDbContext>().UseSqlite(expectedConnection).Options);
            await expected.GetService<IMigrator>().MigrateAsync(Baseline);
            var expectedSchema = await ReadSchemaAsync(expectedConnection);
            if (!schema.SequenceEqual(expectedSchema))
                throw new InvalidOperationException("Схема базы не соответствует Foundation. Автоматическое обновление остановлено; исходная база не изменена.");
            Backup(connection, path);
            await using var transaction = await db.Database.BeginTransactionAsync();
            var history = db.GetService<IHistoryRepository>();
            await db.Database.ExecuteSqlRawAsync(history.GetCreateIfNotExistsScript());
            await db.Database.ExecuteSqlRawAsync(history.GetInsertScript(new HistoryRow(Baseline, "10.0.12")));
            await transaction.CommitAsync();
        }
        else if (schema.Count > 0 && pending.Length > 0) Backup(connection, path);
        await db.Database.MigrateAsync();
    }
    private static void Backup(SqliteConnection connection, string path)
    {
        var destination = Path.GetFullPath(path) + ".backup-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" + Guid.NewGuid().ToString("N") + ".db";
        using var backup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destination }.ToString());
        backup.Open(); connection.BackupDatabase(backup);
    }
    private static async Task<List<string>> ReadSchemaAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT type, name, sql FROM sqlite_master WHERE sql IS NOT NULL AND name NOT LIKE 'sqlite_%' AND name NOT LIKE '__EF%' ORDER BY type, name";
        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) result.Add(reader.GetString(0) + ":" + reader.GetString(1) + ":" + Regex.Replace(reader.GetString(2), @"\s+", " ").Trim().TrimEnd(';'));
        return result;
    }
}
