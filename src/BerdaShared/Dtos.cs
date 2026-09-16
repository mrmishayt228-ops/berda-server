using System;
using System.Collections.Generic;

namespace BerdaShared;

// ============================================================
//  Wire DTOs shared by the server, the launcher and the keygen.
//  JSON is camelCase on the wire (server + clients agree on the
//  same serializer options).
// ============================================================

public class ApiResponse
{
    public bool Ok { get; set; } = true;
    public string? Error { get; set; }
}

public sealed class RegisterRequest
{
    public string Login { get; set; } = "";
    public string Password { get; set; } = "";
}

public sealed class LoginRequest
{
    public string Login { get; set; } = "";
    public string Password { get; set; } = "";
}

public sealed class LoginResponse : ApiResponse
{
    public string? Token { get; set; }
    public string? Login { get; set; }
    public string Role { get; set; } = "user";
    public bool IsPremium { get; set; }
    public DateTime? PremiumUntil { get; set; }
}

public sealed class ProfileRequest
{
    public string Token { get; set; } = "";
}

public sealed class ProfileResponse : ApiResponse
{
    public string? Login { get; set; }
    public string Role { get; set; } = "user";
    public bool IsPremium { get; set; }
    public DateTime? PremiumUntil { get; set; }
}

public sealed class ActivateKeyRequest
{
    public string Token { get; set; } = "";
    public string KeyCode { get; set; } = "";
}

public sealed class ActivateKeyResponse : ApiResponse
{
    public DateTime? PremiumUntil { get; set; }
}

public sealed class LogoutRequest
{
    public string Token { get; set; } = "";
}

public sealed class CreateKeyRequest
{
    public string Token { get; set; } = "";
    public int Days { get; set; }
    public int Hours { get; set; }
    public string? Note { get; set; }
}

public sealed class CreateKeyResponse : ApiResponse
{
    public string? KeyCode { get; set; }
    public int TotalHours { get; set; }
}

public sealed class KeyInfo
{
    public string Code { get; set; } = "";
    public int TotalHours { get; set; }
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool Used { get; set; }
    public string? UsedBy { get; set; }
    public DateTime? UsedAt { get; set; }
}

public sealed class ListKeysResponse : ApiResponse
{
    public List<KeyInfo> Keys { get; set; } = new();
}

public sealed class DeleteKeyRequest
{
    public string Token { get; set; } = "";
    public string KeyCode { get; set; } = "";
}