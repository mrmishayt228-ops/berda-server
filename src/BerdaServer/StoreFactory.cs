using System;
using System.IO;

namespace BerdaServer;

public static class StoreFactory
{
    public static IStore Create(string? connectionString, string filePath)
    {
        string? conn = connectionString;
        if (string.IsNullOrWhiteSpace(conn))
        {
            string? env = Environment.GetEnvironmentVariable("DATABASE_URL");
            if (!string.IsNullOrWhiteSpace(env)) conn = env;
        }

        if (!string.IsNullOrWhiteSpace(conn))
        {
            Console.WriteLine("[i] Storage : PostgreSQL");
            return new PgStore(PgStore.ConvertUrl(conn));
        }

        Console.WriteLine($"[i] Storage : JSON file ({filePath})");
        return new Store(filePath);
    }
}