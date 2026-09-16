using System;
using System.Security.Cryptography;

namespace BerdaShared;

public static class Hashing
{
    private const int Iterations = 100_000;
    private const int SaltSize = 16;
    private const int HashSize = 32;

    // Returns "iterations.saltB64.hashB64"
    public static string Hash(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(
            password, salt, Iterations, HashAlgorithmName.SHA256, HashSize);
        return $"{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('.');
        if (parts.Length != 3) return false;
        if (!int.TryParse(parts[0], out int iters)) return false;
        try
        {
            byte[] salt = Convert.FromBase64String(parts[1]);
            byte[] expected = Convert.FromBase64String(parts[2]);
            byte[] actual = Rfc2898DeriveBytes.Pbkdf2(
                password, salt, iters, HashAlgorithmName.SHA256, expected.Length);
            return actual.AsSpan().SequenceEqual(expected);
        }
        catch
        {
            return false;
        }
    }

    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
}