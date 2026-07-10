using Microsoft.Data.Sqlite;

namespace DevMX.Core.Persistence;

public sealed class ConversationStore : IAsyncDisposable
{
    private readonly string _dbPath;
    private bool _disposed;

    private ConversationStore(string dbPath)
    {
        _dbPath = dbPath;
    }

    #region Factory & Schema

    public static async Task<ConversationStore> OpenAsync(string dbPath)
    {
        var store = new ConversationStore(dbPath);

        // Run schema initialization (creates tables if needed, applies migrations).
        await using var conn = new SqliteConnection($"Data Source={dbPath}");
        await conn.OpenAsync();
        await store.ApplyMigrationsAsync(conn);

        return store;
    }

    private async Task ApplyMigrationsAsync(SqliteConnection conn)
    {
        // Ensure foreign keys are enforced.
        await using var fkCmd = conn.CreateCommand();
        fkCmd.CommandText = "PRAGMA foreign_keys=ON";
        await fkCmd.ExecuteNonQueryAsync();

        // Read current schema version.
        await using var verCmd = conn.CreateCommand();
        verCmd.CommandText = "PRAGMA user_version";
        int currentVersion = Convert.ToInt32(await verCmd.ExecuteScalarAsync());

        // Migration scaffold: apply each version in order.
        if (currentVersion < 1)
        {
            await ExecuteMigration1Async(conn);
        }

        // --- Future migrations go here ---
        // if (currentVersion < 2) await ExecuteMigration2Async(conn);

        // Set the user_version to the latest applied migration.
        await using var setVerCmd = conn.CreateCommand();
        setVerCmd.CommandText = "PRAGMA user_version=1";
        await setVerCmd.ExecuteNonQueryAsync();
    }

    private static async Task ExecuteMigration1Async(SqliteConnection conn)
    {
        var sql = @"
            CREATE TABLE IF NOT EXISTS conversations(
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                title        TEXT    NOT NULL DEFAULT '',
                provider     TEXT    NOT NULL,
                model        TEXT    NOT NULL,
                working_dir  TEXT    NOT NULL,
                created_at   TEXT    NOT NULL,
                updated_at   TEXT    NOT NULL
            );

            CREATE TABLE IF NOT EXISTS messages(
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                conversation_id INTEGER NOT NULL REFERENCES conversations(id) ON DELETE CASCADE,
                seq             INTEGER NOT NULL,
                role            TEXT    NOT NULL CHECK(role IN ('user','assistant')),
                content_json    TEXT    NOT NULL,
                model           TEXT,
                created_at      TEXT    NOT NULL,
                UNIQUE(conversation_id, seq)
            );

            CREATE TABLE IF NOT EXISTS delegations(
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                conversation_id INTEGER NOT NULL REFERENCES conversations(id) ON DELETE CASCADE,
                job_id          TEXT    NOT NULL,
                brief           TEXT    NOT NULL,
                final_state     TEXT,
                journal_json    TEXT,
                created_at      TEXT    NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_messages_conversation_id ON messages(conversation_id);
            CREATE INDEX IF NOT EXISTS idx_delegations_conversation_id ON delegations(conversation_id);
        ";

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    #endregion

    #region Connection helper

    private SqliteConnection CreateConnection()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        // Enforce foreign keys on every connection.
        using var fkCmd = conn.CreateCommand();
        fkCmd.CommandText = "PRAGMA foreign_keys=ON";
        fkCmd.ExecuteNonQuery();
        return conn;
    }

    #endregion

    #region Public API

    public async Task<long> CreateConversationAsync(string provider, string model, string workingDir, string title = "")
    {
        var now = DateTime.UtcNow.ToString("o");
        using var conn = CreateConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO conversations (title, provider, model, working_dir, created_at, updated_at)
            VALUES (@title, @provider, @model, @workingDir, @now, @now);
            SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("@title", title);
        cmd.Parameters.AddWithValue("@provider", provider);
        cmd.Parameters.AddWithValue("@model", model);
        cmd.Parameters.AddWithValue("@workingDir", workingDir);
        cmd.Parameters.AddWithValue("@now", now);

        var result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt64(result!);
    }

    public async Task<long> AppendMessageAsync(long conversationId, string role, string contentJson, string? model = null)
    {
        using var conn = CreateConnection();
        await using var tx = await conn.BeginTransactionAsync();

        try
        {
            // Get max seq for this conversation.
            await using var seqCmd = conn.CreateCommand();
            seqCmd.Transaction = (SqliteTransaction)tx;
            seqCmd.CommandText = @"
                SELECT COALESCE(MAX(seq), 0) FROM messages WHERE conversation_id = @convId;";
            seqCmd.Parameters.AddWithValue("@convId", conversationId);
            int maxSeq = Convert.ToInt32(await seqCmd.ExecuteScalarAsync());
            int nextSeq = maxSeq + 1;

            var now = DateTime.UtcNow.ToString("o");

            // Insert the message.
            await using var insertCmd = conn.CreateCommand();
            insertCmd.Transaction = (SqliteTransaction)tx;
            insertCmd.CommandText = @"
                INSERT INTO messages (conversation_id, seq, role, content_json, model, created_at)
                VALUES (@convId, @seq, @role, @contentJson, @model, @now);
                SELECT last_insert_rowid();";
            insertCmd.Parameters.AddWithValue("@convId", conversationId);
            insertCmd.Parameters.AddWithValue("@seq", nextSeq);
            insertCmd.Parameters.AddWithValue("@role", role);
            insertCmd.Parameters.AddWithValue("@contentJson", contentJson);
            insertCmd.Parameters.Add(new SqliteParameter("@model", model ?? (object)DBNull.Value));
            insertCmd.Parameters.AddWithValue("@now", now);
            var msgId = Convert.ToInt64(await insertCmd.ExecuteScalarAsync()!);

            // Update conversations.updated_at.
            await using var updCmd = conn.CreateCommand();
            updCmd.Transaction = (SqliteTransaction)tx;
            updCmd.CommandText = @"
                UPDATE conversations SET updated_at = @now WHERE id = @convId;";
            updCmd.Parameters.AddWithValue("@now", now);
            updCmd.Parameters.AddWithValue("@convId", conversationId);
            await updCmd.ExecuteNonQueryAsync();

            await tx.CommitAsync();
            return msgId;
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    public async Task<IReadOnlyList<StoredMessage>> GetMessagesAsync(long conversationId)
    {
        using var conn = CreateConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id, conversation_id, seq, role, content_json, model, created_at
            FROM messages
            WHERE conversation_id = @convId
            ORDER BY seq;";
        cmd.Parameters.AddWithValue("@convId", conversationId);

        var results = new List<StoredMessage>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new StoredMessage(
                Id: reader.GetInt64(0),
                ConversationId: reader.GetInt64(1),
                Seq: reader.GetInt32(2),
                Role: reader.GetString(3),
                ContentJson: reader.GetString(4),
                Model: reader.IsDBNull(5) ? null : reader.GetString(5),
                CreatedAt: reader.GetString(6)));
        }
        return results;
    }

    public async Task<IReadOnlyList<ConversationSummary>> ListConversationsAsync()
    {
        using var conn = CreateConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id, title, provider, model, working_dir, updated_at
            FROM conversations
            ORDER BY updated_at DESC;";

        var results = new List<ConversationSummary>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new ConversationSummary(
                Id: reader.GetInt64(0),
                Title: reader.GetString(1),
                Provider: reader.GetString(2),
                Model: reader.GetString(3),
                WorkingDir: reader.GetString(4),
                UpdatedAt: reader.GetString(5)));
        }
        return results;
    }

    public async Task DeleteConversationAsync(long conversationId)
    {
        using var conn = CreateConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"DELETE FROM conversations WHERE id = @convId;";
        cmd.Parameters.AddWithValue("@convId", conversationId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task UpdateTitleAsync(long conversationId, string title)
    {
        using var conn = CreateConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE conversations SET title = @title WHERE id = @convId;";
        cmd.Parameters.AddWithValue("@title", title);
        cmd.Parameters.AddWithValue("@convId", conversationId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<IReadOnlyList<ConversationSummary>> SearchConversationsAsync(string query)
    {
        using var conn = CreateConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT DISTINCT c.id, c.title, c.provider, c.model, c.working_dir, c.updated_at
            FROM conversations c
            WHERE LOWER(c.title) LIKE LOWER(@query)
               OR EXISTS (
                   SELECT 1 FROM messages m
                   WHERE m.conversation_id = c.id
                     AND LOWER(m.content_json) LIKE LOWER(@query)
               )
            ORDER BY c.updated_at DESC
            LIMIT 100;";
        cmd.Parameters.AddWithValue("@query", "%" + query + "%");

        var results = new List<ConversationSummary>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new ConversationSummary(
                Id: reader.GetInt64(0),
                Title: reader.GetString(1),
                Provider: reader.GetString(2),
                Model: reader.GetString(3),
                WorkingDir: reader.GetString(4),
                UpdatedAt: reader.GetString(5)));
        }
        return results;
    }

    public async Task<long> RecordDelegationAsync(long conversationId, string jobId, string brief)
    {
        var now = DateTime.UtcNow.ToString("o");
        using var conn = CreateConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO delegations (conversation_id, job_id, brief, final_state, journal_json, created_at)
            VALUES (@convId, @jobId, @brief, NULL, NULL, @now);
            SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("@convId", conversationId);
        cmd.Parameters.AddWithValue("@jobId", jobId);
        cmd.Parameters.AddWithValue("@brief", brief);
        cmd.Parameters.AddWithValue("@now", now);

        var result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt64(result!);
    }

    public async Task CompleteDelegationAsync(string jobId, string finalState, string? journalJson)
    {
        using var conn = CreateConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE delegations
            SET final_state = @finalState, journal_json = @journalJson
            WHERE job_id = @jobId;";
        cmd.Parameters.AddWithValue("@finalState", finalState);
        cmd.Parameters.Add(new SqliteParameter("@journalJson", journalJson ?? (object)DBNull.Value));
        cmd.Parameters.AddWithValue("@jobId", jobId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<IReadOnlyList<StoredDelegation>> GetDelegationsAsync(long conversationId)
    {
        using var conn = CreateConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id, conversation_id, job_id, brief, final_state, journal_json, created_at
            FROM delegations
            WHERE conversation_id = @convId
            ORDER BY id;";
        cmd.Parameters.AddWithValue("@convId", conversationId);

        var results = new List<StoredDelegation>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new StoredDelegation(
                Id: reader.GetInt64(0),
                ConversationId: reader.GetInt64(1),
                JobId: reader.GetString(2),
                Brief: reader.GetString(3),
                FinalState: reader.IsDBNull(4) ? null : reader.GetString(4),
                JournalJson: reader.IsDBNull(5) ? null : reader.GetString(5),
                CreatedAt: reader.GetString(6)));
        }
        return results;
    }

    #endregion

    #region IAsyncDisposable

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        // No long-lived connection to dispose; each method opens its own.
        await Task.CompletedTask;
    }

    #endregion
}
