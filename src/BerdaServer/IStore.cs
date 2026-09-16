using System;
using System.Collections.Generic;

namespace BerdaServer;

// ============================================================
//  Storage contract. Two implementations:
//    - Store   : plain JSON file (local / self-hosted)
//    - PgStore : PostgreSQL (Northflank / Render / any cloud)
// ============================================================
public interface IStore
{
    void EnsureOwner(string login, string password);
    Account? FindAccount(string login);
    bool TryRegister(string login, string password, out string error);
    bool ActivateKey(string keyCode, string login, out DateTime? until, out string error);
    bool CreateKey(string login, int totalHours, string? note, out string code, out string error);
    List<Pay> ListKeys(string login, out string error);
    bool DeleteKey(string login, string code, out string error);
}