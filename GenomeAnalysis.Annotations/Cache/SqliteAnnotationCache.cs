using System;
using System.Data;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Data.SQLite;
using GenomeAnalysis.Core.Annotations;

namespace GenomeAnalysis.Annotations.Cache
{
    /// <summary>
    /// Local SQLite store for annotations fetched from external sources.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only public reference data lives here — what SNPedia or ClinVar say about a
    /// variant, independent of anyone's genotype. No marker call from the user's
    /// file is ever written to this database.
    /// </para>
    /// <para>
    /// Pre-populating it from a bulk export is the intended way to use it: with a
    /// filled cache, analysing a genome needs no network access at all, which
    /// removes the request-pattern leak rather than mitigating it.
    /// </para>
    /// </remarks>
    public sealed class SqliteAnnotationCache : IAnnotationCache, IDisposable
    {
        private readonly string _connectionString;
        private readonly bool _isInMemory;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private SQLiteConnection? _keepAlive;
        private bool _initialised;
        private bool _disposed;

        public SqliteAnnotationCache(string databasePath)
        {
            if (string.IsNullOrWhiteSpace(databasePath))
            {
                throw new ArgumentException("Database path is required.", nameof(databasePath));
            }

            var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));

            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory!);
            }

            _connectionString = new SQLiteConnectionStringBuilder
            {
                DataSource = databasePath,
                FailIfMissing = false
            }.ToString();

            _isInMemory = false;
        }

        private SqliteAnnotationCache(string connectionString, bool isInMemory)
        {
            _connectionString = connectionString;
            _isInMemory = isInMemory;
        }

        /// <summary>
        /// An in-memory cache, for tests.
        /// </summary>
        /// <remarks>
        /// The database is named and shared rather than a bare <c>:memory:</c>,
        /// because this class opens a connection per operation and SQLite discards
        /// an in-memory database once its last connection closes. A keep-alive
        /// connection held for the lifetime of the instance is what makes the data
        /// survive between calls.
        /// </remarks>
        public static SqliteAnnotationCache InMemory()
        {
            var connectionString = new SQLiteConnectionStringBuilder
            {
                FullUri = "file:genomeanalysis-" + Guid.NewGuid().ToString("N") +
                          "?mode=memory&cache=shared"
            }.ToString();

            return new SqliteAnnotationCache(connectionString, isInMemory: true);
        }

        public async Task<CachedAnnotation?> TryGetAsync(
            string sourceName,
            string rsId,
            CancellationToken cancellationToken = default)
        {
            await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

            using (var connection = await OpenAsync(cancellationToken).ConfigureAwait(false))
            using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    "SELECT payload, retrieved_at, expires_at " +
                    "FROM annotation_cache WHERE source = $source AND rsid = $rsid;";
                command.Parameters.AddWithValue("$source", sourceName);
                command.Parameters.AddWithValue("$rsid", Normalise(rsId));

                using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        return null;
                    }

                    var payload = reader.IsDBNull(0) ? null : reader.GetString(0);
                    var retrievedAt = DateTimeOffset.Parse(reader.GetString(1));
                    var expiresAt = DateTimeOffset.Parse(reader.GetString(2));

                    // A null payload is a recorded absence, not a corrupt row: the
                    // source was asked and had nothing.
                    var annotation = payload == null ? null : AnnotationSerializer.FromJson(payload);

                    if (payload != null && annotation == null)
                    {
                        // Written by an incompatible schema version. Treat as a miss.
                        return null;
                    }

                    return new CachedAnnotation(annotation, retrievedAt, expiresAt);
                }
            }
        }

        public async Task StoreAsync(
            string sourceName,
            string rsId,
            VariantAnnotation? annotation,
            DateTimeOffset expiresAt,
            CancellationToken cancellationToken = default)
        {
            await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

            using (var connection = await OpenAsync(cancellationToken).ConfigureAwait(false))
            using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    "INSERT INTO annotation_cache (source, rsid, payload, retrieved_at, expires_at) " +
                    "VALUES ($source, $rsid, $payload, $retrieved, $expires) " +
                    "ON CONFLICT(source, rsid) DO UPDATE SET " +
                    "payload = excluded.payload, " +
                    "retrieved_at = excluded.retrieved_at, " +
                    "expires_at = excluded.expires_at;";

                command.Parameters.AddWithValue("$source", sourceName);
                command.Parameters.AddWithValue("$rsid", Normalise(rsId));
                command.Parameters.AddWithValue(
                    "$payload",
                    annotation == null ? (object)DBNull.Value : AnnotationSerializer.ToJson(annotation));
                command.Parameters.AddWithValue("$retrieved", DateTimeOffset.UtcNow.ToString("O"));
                command.Parameters.AddWithValue("$expires", expiresAt.ToString("O"));

                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        public async Task<int> CountAsync(string sourceName, CancellationToken cancellationToken = default)
        {
            await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

            using (var connection = await OpenAsync(cancellationToken).ConfigureAwait(false))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT COUNT(*) FROM annotation_cache WHERE source = $source;";
                command.Parameters.AddWithValue("$source", sourceName);

                var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                return Convert.ToInt32(result);
            }
        }

        private async Task<SQLiteConnection> OpenAsync(CancellationToken cancellationToken)
        {
            var connection = new SQLiteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }

        private async Task EnsureSchemaAsync(CancellationToken cancellationToken)
        {
            if (_initialised)
            {
                return;
            }

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                if (_initialised)
                {
                    return;
                }

                if (_isInMemory && _keepAlive == null)
                {
                    // Opened before the schema is created and never closed, so the
                    // shared in-memory database outlives each individual operation.
                    _keepAlive = await OpenAsync(cancellationToken).ConfigureAwait(false);
                }

                using (var connection = await OpenAsync(cancellationToken).ConfigureAwait(false))
                using (var command = connection.CreateCommand())
                {
                    command.CommandText =
                        "CREATE TABLE IF NOT EXISTS annotation_cache (" +
                        "  source       TEXT NOT NULL," +
                        "  rsid         TEXT NOT NULL," +
                        "  payload      TEXT NULL," +
                        "  retrieved_at TEXT NOT NULL," +
                        "  expires_at   TEXT NOT NULL," +
                        "  PRIMARY KEY (source, rsid)" +
                        ");";

                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                _initialised = true;
            }
            finally
            {
                _gate.Release();
            }
        }

        private static string Normalise(string rsId) =>
            (rsId ?? string.Empty).Trim().ToLowerInvariant();

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _gate.Dispose();

            // Closing this drops an in-memory database, which is the intent.
            _keepAlive?.Dispose();
            _keepAlive = null;

            SQLiteConnection.ClearAllPools();
        }
    }
}
