using System;
using System.Security.Cryptography;
using System.Text;

namespace BerdaShared;

public static class Keys
{
    // unambiguous alphabet: no 0/O, 1/I/L
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    // "XXXX-XXXX-XXXX-XXXX"
    public static string Generate()
    {
        byte[] rnd = RandomNumberGenerator.GetBytes(16);
        var sb = new StringBuilder(19);
        for (int i = 0; i < 16; i++)
        {
            if (i > 0 && i % 4 == 0) sb.Append('-');
            sb.Append(Alphabet[rnd[i] % Alphabet.Length]);
        }
        return sb.ToString();
    }

    public static bool LooksValid(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return false;
        foreach (char c in code)
        {
            if (c == '-') continue;
            if (Alphabet.IndexOf(c) < 0) return false;
        }
        return true;
    }

    public static int TotalHours(int days, int hours) => Math.Clamp(days, 0, 3650) * 24 + Math.Clamp(hours, 0, 23);

    public static string FormatDuration(int totalHours)
    {
        if (totalHours <= 0) return "0h";
        int d = totalHours / 24, h = totalHours % 24;
        if (d > 0 && h > 0) return $"{d}d {h}h";
        if (d > 0) return $"{d}d";
        return $"{h}h";
    }
}