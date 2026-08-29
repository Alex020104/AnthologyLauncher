using System.Text.Json;
using Anthology.Contracts;
using Microsoft.Data.Sqlite;

namespace Anthology.Community.Api;

public sealed class CommunityDatabase
{
    private readonly string _connectionString;

    public CommunityDatabase(string communityRoot)
    {
        Directory.CreateDirectory(communityRoot);
        DatabasePath = Path.Combine(communityRoot, "community.db");
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false,
        };
        _connectionString = builder.ToString();
        Initialize();
    }

    public string DatabasePath { get; }

    public T Load<T>(string legacyJsonPath, T empty)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT json FROM community_state WHERE id = 1;";
        var json = command.ExecuteScalar() as string;
        if (!string.IsNullOrWhiteSpace(json))
        {
            return JsonSerializer.Deserialize<T>(json, ManifestJson.Options) ?? empty;
        }

        if (File.Exists(legacyJsonPath))
        {
            try
            {
                var imported = JsonSerializer.Deserialize<T>(File.ReadAllText(legacyJsonPath), ManifestJson.Options);
                if (imported is not null)
                {
                    Save(imported);
                    return imported;
                }
            }
            catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
            {
                // A damaged legacy state must not prevent the server from starting.
            }
        }

        return empty;
    }

    public void Save<T>(T state)
    {
        var json = JsonSerializer.Serialize(state, ManifestJson.Options);
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO community_state(id, json, updated_at)
            VALUES(1, $json, $updatedAt)
            ON CONFLICT(id) DO UPDATE SET
                json = excluded.json,
                updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$json", json);
        command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    public void CreateSnapshot(string destinationPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationPath))!);
        if (File.Exists(destinationPath))
        {
            File.Delete(destinationPath);
        }

        using var source = OpenConnection();
        var destinationBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(destinationPath),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        };
        using var destination = new SqliteConnection(destinationBuilder.ToString());
        destination.Open();
        source.BackupDatabase(destination);
    }

    private void Initialize()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS community_state(
                id INTEGER NOT NULL PRIMARY KEY CHECK(id = 1),
                json TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000;";
        command.ExecuteNonQuery();
        return connection;
    }
}
