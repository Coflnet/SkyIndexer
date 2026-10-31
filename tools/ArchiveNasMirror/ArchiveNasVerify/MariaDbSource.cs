using Coflnet.Sky.Core.Archive;
using MySqlConnector;

namespace Coflnet.Sky.Indexer.Tools.ArchiveNasVerify;

/// <summary>Read-only access to archived_months. The connection string never leaves this class (no logging, no exception text).</summary>
public sealed class MariaDbSource : IMariaDbSource
{
    private static readonly string[] KnownKeys = { "DBConnection", "MARIADB_CONNECTION", "MARIADB_CONNECTION_STRING", "CONNECTION_STRING" };
    private readonly string _connectionString;

    public MariaDbSource(string connectionString) => _connectionString = connectionString;

    public static MariaDbSource FromEnvFile(string path)
    {
        var lines = File.ReadAllLines(path).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).Select(l => l.StartsWith("export ") ? l[7..].TrimStart() : l).ToList();
        string? value = null;
        foreach (var line in lines)
        {
            var eq = line.IndexOf('=');
            if (eq > 0 && KnownKeys.Contains(line[..eq].Trim()))
            {
                value = line[(eq + 1)..].Trim();
                break;
            }
        }
        if (value == null && lines.Count == 1 && lines[0].Contains("=") && lines[0].Contains(';'))
            value = lines[0]; // bare "server=...;user=...;" line
        if (value == null)
            throw new InvalidOperationException($"{path}: expected one of {string.Join(", ", KnownKeys)}=<connection string>");
        if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0])
            value = value[1..^1];
        return new MariaDbSource(value);
    }

    public async Task<IReadOnlyDictionary<(string Tag, int Month), MariaMonthRow>> LoadAsync(CancellationToken ct)
    {
        var result = new Dictionary<(string, int), MariaMonthRow>();
        try
        {
            await using var conn = new MySqlConnection(_connectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);
            await using (var ro = conn.CreateCommand())
            {
                ro.CommandText = "SET SESSION TRANSACTION READ ONLY";
                await ro.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT t.tag, m.month, m.parts, m.`rows`, m.sha256, m.deleted_at FROM archived_months m JOIN auction_tags t ON t.id = m.tag_id WHERE m.tag_id <> 0";
            cmd.CommandTimeout = 600;
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var month = ArchiveMonths.FromIndex((ushort)reader.GetInt32(1));
                var tag = reader.GetString(0);
                result[(tag, month)] = new MariaMonthRow(tag, month, reader.GetInt32(2), reader.GetInt64(3), reader.GetString(4).ToLowerInvariant(),
                    reader.IsDBNull(5) ? null : reader.GetDateTime(5));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // exception texts can contain host/user names or connection string options: report the type only
            throw new InvalidOperationException($"MariaDB query failed ({ex.GetType().Name}{(ex is MySqlException m ? " number " + m.Number : "")})");
        }
        return result;
    }
}
