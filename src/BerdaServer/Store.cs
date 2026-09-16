using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BerdaShared;

namespace BerdaServer;

public sealed class Account
{
    public string Login { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = "user"; // "user" | "owner"
    public DateTime? PremiumUntil { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class Pay
{
    public string Code { get; set; } = "";
    public int TotalHours { get; set; }
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public bool Used { get; set; }
    public string? UsedBy { get; set; }
    public DateTime? UsedAt { get; set; }
}

public sealed class Db
{
    public List<Account> Accounts { get; set; } = new();
    public List<Pay> Keys { get; set; } = new();
}

// ============================================================
//  File-backed store. All mutations go through a single lock so
//  the JSON file never ends up half-written or torn.
// ============================================================
public sealed class Store : IStore
{
    private static readonly JsonSerializerOptions SaveOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _path;
    private readonly object _lock = new();
    private Db _db;

    public Store(string path)
    {
        _path = path;
        _db = Load();
    }

    private Db Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                string s = File.ReadAllText(_path);
                var db = JsonSerializer.Deserialize<Db>(s, SaveOpts);
                if (db != null) return db;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[!] DB corrupt ({ex.Message}) — starting fresh.");
        }
        return new Db();
    }

    private void Save()
    {
        try
        {
            string tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_db, SaveOpts));
            File.Move(tmp, _path, true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[!] DB save failed: {ex.Message}");
        }
    }

    public void EnsureOwner(string login, string password)
    {
        lock (_lock)
        {
            if (_db.Accounts.Any(a => string.Equals(a.Login, login, StringComparison.OrdinalIgnoreCase)))
                return;
            _db.Accounts.Add(new Account
            {
                Login = login,
                PasswordHash = Hashing.Hash(password),
                Role = "owner",
                CreatedAt = DateTime.UtcNow,
            });
            Save();
            Console.WriteLine($"[+] Owner account created: {login}");
        }
    }

    public Account? FindAccount(string login) =>
        _db.Accounts.FirstOrDefault(a => string.Equals(a.Login, login, StringComparison.OrdinalIgnoreCase));

    public bool TryRegister(string login, string password, out string error)
    {
        lock (_lock)
        {
            if (_db.Accounts.Any(a => string.Equals(a.Login, login, StringComparison.OrdinalIgnoreCase)))
            {
                error = "login already taken";
                return false;
            }
            _db.Accounts.Add(new Account
            {
                Login = login,
                PasswordHash = Hashing.Hash(password),
                Role = "user",
                CreatedAt = DateTime.UtcNow,
            });
            Save();
            error = "";
            return true;
        }
    }

    public bool ActivateKey(string keyCode, string login, out DateTime? until, out string error)
    {
        lock (_lock)
        {
            var key = _db.Keys.FirstOrDefault(k => string.Equals(k.Code, keyCode, StringComparison.OrdinalIgnoreCase));
            if (key == null)
            {
                until = null;
                error = "key not found";
                return false;
            }
            if (key.Used)
            {
                until = null;
                error = "key already used";
                return false;
            }
            var acc = _db.Accounts.FirstOrDefault(a => string.Equals(a.Login, login, StringComparison.OrdinalIgnoreCase));
            if (acc == null)
            {
                until = null;
                error = "account not found";
                return false;
            }

            DateTime now = DateTime.UtcNow;
            DateTime baseTime = (acc.PremiumUntil.HasValue && acc.PremiumUntil.Value > now)
                ? acc.PremiumUntil.Value
                : now;
            until = baseTime.AddHours(key.TotalHours);
            acc.PremiumUntil = until;

            key.Used = true;
            key.UsedBy = login;
            key.UsedAt = now;
            Save();
            error = "";
            return true;
        }
    }

    public bool CreateKey(string login, int totalHours, string? note, out string code, out string error)
    {
        lock (_lock)
        {
            var owner = _db.Accounts.FirstOrDefault(a => string.Equals(a.Login, login, StringComparison.OrdinalIgnoreCase));
            if (owner == null || owner.Role != "owner")
            {
                code = "";
                error = "owner only";
                return false;
            }
            code = Keys.Generate();
            _db.Keys.Add(new Pay
            {
                Code = code,
                TotalHours = totalHours,
                Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
                CreatedAt = DateTime.UtcNow,
            });
            Save();
            error = "";
            return true;
        }
    }

    public List<Pay> ListKeys(string login, out string error)
    {
        lock (_lock)
        {
            var owner = _db.Accounts.FirstOrDefault(a => string.Equals(a.Login, login, StringComparison.OrdinalIgnoreCase));
            if (owner == null || owner.Role != "owner")
            {
                error = "owner only";
                return new List<Pay>();
            }
            error = "";
            return _db.Keys.OrderByDescending(k => k.CreatedAt).ToList();
        }
    }

    public bool DeleteKey(string login, string code, out string error)
    {
        lock (_lock)
        {
            var owner = _db.Accounts.FirstOrDefault(a => string.Equals(a.Login, login, StringComparison.OrdinalIgnoreCase));
            if (owner == null || owner.Role != "owner")
            {
                error = "owner only";
                return false;
            }
            var key = _db.Keys.FirstOrDefault(k => string.Equals(k.Code, code, StringComparison.OrdinalIgnoreCase));
            if (key == null)
            {
                error = "key not found";
                return false;
            }
            _db.Keys.Remove(key);
            Save();
            error = "";
            return true;
        }
    }
}