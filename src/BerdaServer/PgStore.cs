using System;
using System.Collections.Generic;
using System.Threading;
using Npgsql;
using BerdaShared;

namespace BerdaServer;

// ============================================================
//  PostgreSQL-backed store for cloud deployments (Northflank,
//  Render, etc). Reads DATABASE_URL-style connection strings.
//  File-based persistence does not survive container restarts,
//  so this is required when running serverless-agnostic.
// ============================================================
public sealed class PgStore : IStore
{
    private readonly NpgsqlDataSource _ds;

    public PgStore(string connectionString)
    {
        _ds = NpgsqlDataSource.Create(connectionString);
        EnsureSchema();
    }

    public static string ConvertUrl(string url)
    {
        // postgres://user:pass@host:port/db -> Npgsql key/value string
        if (!url.Contains("://")) return url;
        var u = new Uri(url.Replace("postgres://", "http://"));
        var b = new System.Text.StringBuilder();
        b.Append($"Host={u.Host};Port={u.Port};Username={Uri.UnescapeDataString(u.UserInfo.Split(':')[0])};");
        if (u.UserInfo.Contains(':')) b.Append($"Password={Uri.UnescapeDataString(u.UserInfo.Substring(u.UserInfo.IndexOf(':') + 1))};");
        b.Append($"Database={Uri.UnescapeDataString(u.AbsolutePath.TrimStart('/'))};SSL Mode=Require;Trust Server Certificate=true;Timeout=15;");
        return b.ToString();
    }

    private void EnsureSchema()
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS accounts(
                login TEXT PRIMARY KEY,
                password_hash TEXT NOT NULL,
                role TEXT NOT NULL DEFAULT 'user',
                premium_until TIMESTAMPTZ NULL,
                created_at TIMESTAMPTZ NOT NULL DEFAULT now()
            );
            CREATE TABLE IF NOT EXISTS berda_keys(
                code TEXT PRIMARY KEY,
                total_hours INTEGER NOT NULL,
                note TEXT NULL,
                created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
                used BOOLEAN NOT NULL DEFAULT false,
                used_by TEXT NULL,
                used_at TIMESTAMPTZ NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public void EnsureOwner(string login, string password)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO accounts(login, password_hash, role)
            VALUES(@l, @h, 'owner')
            ON CONFLICT (login) DO NOTHING
            """;
        cmd.Parameters.AddWithValue("l", login);
        cmd.Parameters.AddWithValue("h", Hashing.Hash(password));
        cmd.ExecuteNonQuery();
    }

    public Account? FindAccount(string login)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT login, password_hash, role, premium_until, created_at
            FROM accounts WHERE login = @l
            """;
        cmd.Parameters.AddWithValue("l", login);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new Account
        {
            Login = r.GetString(0),
            PasswordHash = r.GetString(1),
            Role = r.GetString(2),
            PremiumUntil = r.IsDBNull(3) ? null : r.GetDateTime(3),
            CreatedAt = r.GetDateTime(4),
        };
    }

    public bool TryRegister(string login, string password, out string error)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO accounts(login, password_hash)
            VALUES(@l, @h)
            ON CONFLICT (login) DO NOTHING
            """;
        cmd.Parameters.AddWithValue("l", login);
        cmd.Parameters.AddWithValue("h", Hashing.Hash(password));
        int affected = cmd.ExecuteNonQuery();
        error = affected == 1 ? "" : "login already taken";
        return affected == 1;
    }

    public bool ActivateKey(string keyCode, string login, out DateTime? until, out string error)
    {
        using var conn = _ds.OpenConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            // lock the key row, refuse if already used
            using (var ck = conn.CreateCommand())
            {
                ck.Transaction = tx;
                ck.CommandText = "SELECT total_hours FROM berda_keys WHERE code = @c AND NOT used FOR UPDATE";
                ck.Parameters.AddWithValue("c", keyCode);
                var hours = ck.ExecuteScalar();
                if (hours == null)
                {
                    tx.Rollback();
                    until = null;
                    error = "key not found or already used";
                    return false;
                }

                using var ca = conn.CreateCommand();
                ca.Transaction = tx;
                ca.CommandText = "SELECT premium_until FROM accounts WHERE login = @l FOR UPDATE";
                ca.Parameters.AddWithValue("l", login);
                var val = ca.ExecuteScalar();
                if (val == null || val is DBNull)
                {
                    tx.Rollback();
                    until = null;
                    error = "account not found";
                    return false;
                }

                DateTime now = DateTime.UtcNow;
                DateTime current = val is DateTime cur && cur > now ? cur : now;
                until = current.AddHours(Convert.ToInt32(hours));

                using var u1 = conn.CreateCommand();
                u1.Transaction = tx;
                u1.CommandText = "UPDATE berda_keys SET used = true, used_by = @b, used_at = now() WHERE code = @c";
                u1.Parameters.AddWithValue("b", login);
                u1.Parameters.AddWithValue("c", keyCode);
                u1.ExecuteNonQuery();

                using var u2 = conn.CreateCommand();
                u2.Transaction = tx;
                u2.CommandText = "UPDATE accounts SET premium_until = @u WHERE login = @l";
                u2.Parameters.AddWithValue("u", until.Value);
                u2.Parameters.AddWithValue("l", login);
                u2.ExecuteNonQuery();

                tx.Commit();
                error = "";
                return true;
            }
        }
        catch
        {
            try { tx.Rollback(); } catch { /* ignore */ }
            until = null;
            error = "database error";
            return false;
        }
    }

    public bool CreateKey(string login, int totalHours, string? note, out string code, out string error)
    {
        using var conn = _ds.OpenConnection();
        var role = FindAccount(login)?.Role;
        if (role != "owner")
        {
            code = "";
            error = "owner only";
            return false;
        }
        code = Keys.Generate();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO berda_keys(code, total_hours, note)
            VALUES(@c, @h, @n)
            """;
        cmd.Parameters.AddWithValue("c", code);
        cmd.Parameters.AddWithValue("h", totalHours);
        string? noteVal = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        cmd.Parameters.AddWithValue("n", (object?)noteVal ?? DBNull.Value);
        cmd.ExecuteNonQuery();
        error = "";
        return true;
    }

    public List<Pay> ListKeys(string login, out string error)
    {
        var result = new List<Pay>();
        if (FindAccount(login)?.Role != "owner")
        {
            error = "owner only";
            return result;
        }
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT code, total_hours, note, created_at, used, used_by, used_at
            FROM berda_keys ORDER BY created_at DESC
            """;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            result.Add(new Pay
            {
                Code = r.GetString(0),
                TotalHours = r.GetInt32(1),
                Note = r.IsDBNull(2) ? null : r.GetString(2),
                CreatedAt = r.GetDateTime(3),
                Used = r.GetBoolean(4),
                UsedBy = r.IsDBNull(5) ? null : r.GetString(5),
                UsedAt = r.IsDBNull(6) ? null : r.GetDateTime(6),
            });
        }
        error = "";
        return result;
    }

    public bool DeleteKey(string login, string code, out string error)
    {
        if (FindAccount(login)?.Role != "owner")
        {
            error = "owner only";
            return false;
        }
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM berda_keys WHERE code = @c";
        cmd.Parameters.AddWithValue("c", code);
        int affected = cmd.ExecuteNonQuery();
        error = affected == 1 ? "" : "key not found";
        return affected == 1;
    }
}