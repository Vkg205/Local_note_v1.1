using Microsoft.Data.Sqlite;

namespace LocalNote.Storage.Services;

public sealed class SqliteDataStore
{
    public SqliteDataStore(string databasePath)
    {
        DatabasePath = Path.GetFullPath(databasePath);
        ConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true
        }.ToString();
    }

    public string DatabasePath { get; }
    public string ConnectionString { get; }
    public SqliteConnection CreateConnection() => new(ConnectionString);

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = SchemaSql;
        await command.ExecuteNonQueryAsync(cancellationToken);

        await EnsureColumnAsync(connection, "content_objects", "is_todo", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(connection, "content_objects", "todo_completed", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(connection, "content_objects", "is_important", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        var version = connection.CreateCommand();
        version.CommandText = "UPDATE app_meta SET value='3' WHERE key='schema_version';";
        await version.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task CheckpointAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureColumnAsync(SqliteConnection connection, string table, string column, string definition, CancellationToken cancellationToken)
    {
        var info = connection.CreateCommand();
        info.CommandText = $"PRAGMA table_info({table});";
        await using var reader = await info.ExecuteReaderAsync(cancellationToken);
        var exists = false;
        while (await reader.ReadAsync(cancellationToken))
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) { exists = true; break; }
        }
        await reader.DisposeAsync();
        if (exists) return;
        var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }

    private const string SchemaSql = """
        PRAGMA journal_mode=WAL;
        PRAGMA synchronous=NORMAL;
        PRAGMA foreign_keys=ON;

        CREATE TABLE IF NOT EXISTS app_meta (
            key TEXT PRIMARY KEY NOT NULL,
            value TEXT NOT NULL
        );
        INSERT OR IGNORE INTO app_meta(key, value) VALUES('schema_version', '3');
        INSERT OR IGNORE INTO app_meta(key, value) VALUES('clean_shutdown', '1');

        CREATE TABLE IF NOT EXISTS notebooks (
            id TEXT PRIMARY KEY NOT NULL,
            vault_id TEXT NOT NULL,
            name TEXT NOT NULL,
            color TEXT NOT NULL DEFAULT '#4F46E5',
            sort_order INTEGER NOT NULL DEFAULT 0,
            is_deleted INTEGER NOT NULL DEFAULT 0,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS sections (
            id TEXT PRIMARY KEY NOT NULL,
            notebook_id TEXT NOT NULL,
            parent_group_id TEXT NULL,
            name TEXT NOT NULL,
            color TEXT NOT NULL DEFAULT '#7C6CE7',
            sort_order INTEGER NOT NULL DEFAULT 0,
            is_deleted INTEGER NOT NULL DEFAULT 0,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            FOREIGN KEY(notebook_id) REFERENCES notebooks(id)
        );

        CREATE TABLE IF NOT EXISTS pages (
            id TEXT PRIMARY KEY NOT NULL,
            section_id TEXT NOT NULL,
            parent_page_id TEXT NULL,
            title TEXT NOT NULL,
            indent_level INTEGER NOT NULL DEFAULT 0,
            sort_order INTEGER NOT NULL DEFAULT 0,
            is_pinned INTEGER NOT NULL DEFAULT 0,
            is_deleted INTEGER NOT NULL DEFAULT 0,
            local_version INTEGER NOT NULL DEFAULT 1,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            FOREIGN KEY(section_id) REFERENCES sections(id)
        );

        CREATE TABLE IF NOT EXISTS content_objects (
            id TEXT PRIMARY KEY NOT NULL,
            page_id TEXT NOT NULL,
            type TEXT NOT NULL,
            x REAL NOT NULL,
            y REAL NOT NULL,
            width REAL NOT NULL,
            height REAL NOT NULL,
            z_index INTEGER NOT NULL DEFAULT 0,
            payload TEXT NOT NULL DEFAULT '',
            style_json TEXT NOT NULL DEFAULT '{}',
            is_todo INTEGER NOT NULL DEFAULT 0,
            todo_completed INTEGER NOT NULL DEFAULT 0,
            is_important INTEGER NOT NULL DEFAULT 0,
            local_version INTEGER NOT NULL DEFAULT 1,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            FOREIGN KEY(page_id) REFERENCES pages(id)
        );

        CREATE TABLE IF NOT EXISTS ink_layers (
            page_id TEXT PRIMARY KEY NOT NULL,
            isf_data BLOB NULL,
            updated_at TEXT NOT NULL,
            FOREIGN KEY(page_id) REFERENCES pages(id)
        );

        CREATE VIRTUAL TABLE IF NOT EXISTS search_fts USING fts5(
            object_id UNINDEXED,
            page_id UNINDEXED,
            source UNINDEXED,
            text,
            tokenize='unicode61'
        );

        CREATE INDEX IF NOT EXISTS idx_notebooks_vault_active ON notebooks(vault_id, is_deleted, sort_order);
        CREATE INDEX IF NOT EXISTS idx_sections_notebook_active ON sections(notebook_id, is_deleted, sort_order);
        CREATE INDEX IF NOT EXISTS idx_pages_section_active ON pages(section_id, is_deleted, is_pinned, sort_order);
        CREATE INDEX IF NOT EXISTS idx_content_page ON content_objects(page_id, z_index);
        """;
}
