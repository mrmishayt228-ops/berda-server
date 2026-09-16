using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using BerdaShared;

namespace BerdaServer;

// ============================================================
//  Berda auth + key server. HttpListener-based (no ASP.NET
//  runtime needed). JSON file storage. Owner creates keys.
// ============================================================
public static class Program
{
    private static readonly ConcurrentDictionary<string, string> Tokens = new(); // token -> login
    private static IStore s_store = null!;
    private static IStore StoreInstance() => s_store;

    private static string ConfigPath => Path.Combine(AppContext.BaseDirectory, "server.json");
    private static string DataPath => Path.Combine(AppContext.BaseDirectory, "serverdb.json");

    public static void Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.Title = "Berda Server";

        // ---- config (server.json next to this exe) ----
        int port = 8080;
        string ownerLogin = "owner";
        string ownerPassword = "EstiAdmin2026";
        string? connectionString = null;
        if (File.Exists(ConfigPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(ConfigPath));
                var root = doc.RootElement;
                if (root.TryGetProperty("Port", out var p) && p.TryGetInt32(out var pv)) port = pv;
                if (root.TryGetProperty("OwnerLogin", out var ol)) ownerLogin = ol.GetString() ?? ownerLogin;
                if (root.TryGetProperty("OwnerPassword", out var op)) ownerPassword = op.GetString() ?? ownerPassword;
                if (root.TryGetProperty("ConnectionString", out var cs) && cs.ValueKind == JsonValueKind.String)
                    connectionString = cs.GetString();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[!] Config parse failed ({ex.Message}), using defaults.");
            }
        }
        else
        {
            WriteDefaultConfig(port, ownerLogin, ownerPassword, connectionString);
            Console.WriteLine($"[i] Created {Path.GetFileName(ConfigPath)} — edit it if needed (Port, OwnerLogin, OwnerPassword), then restart.");
        }

        // Platform injects the free port; PORT env wins over server.json.
        string? envPort = Environment.GetEnvironmentVariable("PORT");
        if (int.TryParse(envPort, out int envPortVal) && envPortVal > 0) port = envPortVal;

        // Cloud-friendly owner creds: env vars win over server.json.
        string? envOwnerLogin = Environment.GetEnvironmentVariable("OWNER_LOGIN");
        if (!string.IsNullOrWhiteSpace(envOwnerLogin)) ownerLogin = envOwnerLogin;
        string? envOwnerPassword = Environment.GetEnvironmentVariable("OWNER_PASSWORD");
        if (!string.IsNullOrWhiteSpace(envOwnerPassword)) ownerPassword = envOwnerPassword;

        s_store = StoreFactory.Create(connectionString, DataPath);
        StoreInstance().EnsureOwner(ownerLogin, ownerPassword);
        Console.WriteLine($"[i] Owner login : {ownerLogin}");
        Console.WriteLine($"[i] Owner pass  : {ownerPassword}   (change in server.json)");
        Console.WriteLine($"[i] Port        : {port}");

        // ---- listener ----
        var listener = CreateListener($"http://+:{port}/");
        try
        {
            listener.Start();
            Console.WriteLine($"[+] Listening: http://0.0.0.0:{port}/");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[!] Cannot bind http://+:{port}/ ({ex.Message}).");
            Console.WriteLine($"[>] As admin run:  netsh http add urlacl url=http://+:{port}/ user=everyone");
            Console.WriteLine("[>] Falling back to localhost+127.0.0.1 only...");
            try { listener.Close(); } catch { /* ignore */ }
            listener = CreateListener($"http://localhost:{port}/", $"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
                Console.WriteLine($"[+] Listening: http://localhost:{port}/ and http://127.0.0.1:{port}/");
            }
            catch (Exception ex2)
            {
                Console.WriteLine($"[!] Also failed on localhost: {ex2.Message}");
                return;
            }
        }

        Console.WriteLine("[i] Ctrl+C to stop.");
        while (true)
        {
            try
            {
                var ctx = listener.GetContext();
                System.Threading.Tasks.Task.Run(() => Handle(ctx));
            }
            catch (HttpListenerException) { break; }
            catch (Exception ex)
            {
                Console.WriteLine($"[!] Accept error: {ex.Message}");
            }
        }
    }

    private static void WriteDefaultConfig(int port, string ownerLogin, string ownerPassword, string? connectionString = null)
    {
        try
        {
            var doc = new Dictionary<string, object?>
            {
                ["Port"] = port,
                ["OwnerLogin"] = ownerLogin,
                ["OwnerPassword"] = ownerPassword,
                ["ConnectionString"] = connectionString,
            };
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[!] Cannot write server.json: {ex.Message}");
        }
    }

    private static HttpListener CreateListener(params string[] prefixes)
    {
        var l = new HttpListener();
        foreach (var p in prefixes) l.Prefixes.Add(p);
        return l;
    }

    private static void Handle(HttpListenerContext ctx)
    {
        try
        {
            string path = ctx.Request.Url?.AbsolutePath ?? "/";
            string method = ctx.Request.HttpMethod;

            if (method == "POST" && path == "/api/register") { Register(ctx); return; }
            if (method == "POST" && path == "/api/login") { Login(ctx); return; }
            if (method == "POST" && path == "/api/logout") { Logout(ctx); return; }
            if (method == "POST" && path == "/api/premium/activate") { ActivateKey(ctx); return; }
            if (method == "GET" && path == "/api/profile") { Profile(ctx); return; }
            if (method == "POST" && path == "/api/keys/create") { CreateKey(ctx); return; }
            if (method == "GET" && path == "/api/keys/list") { ListKeys(ctx); return; }
            if (method == "POST" && path == "/api/keys/delete") { DeleteKey(ctx); return; }

            JsonOk(ctx, new ApiResponse { Ok = false, Error = "unknown route" });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[!] Handler error: {ex.Message}");
            try { JsonOk(ctx, new ApiResponse { Ok = false, Error = "internal error" }); }
            catch { /* ignore */ }
        }
    }

    // ---------- helpers ----------

    private static void JsonOk(HttpListenerContext ctx, object payload)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(payload, Json.Opts);
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.ContentLength64 = body.Length;
        ctx.Response.OutputStream.Write(body, 0, body.Length);
        ctx.Response.OutputStream.Close();
    }

    private static void JsonErr(HttpListenerContext ctx, int status, string error)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(new ApiResponse { Ok = false, Error = error }, Json.Opts);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.ContentLength64 = body.Length;
        ctx.Response.OutputStream.Write(body, 0, body.Length);
        ctx.Response.OutputStream.Close();
    }

    private static T? ReadBody<T>(HttpListenerContext ctx)
    {
        using var sr = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
        string s = sr.ReadToEnd();
        try { return JsonSerializer.Deserialize<T>(s, Json.Opts); }
        catch { return default; }
    }

    private static bool TryAuth(HttpListenerContext ctx, out string login, string inputToken)
    {
        login = "";
        if (string.IsNullOrEmpty(inputToken)) return false;
        if (!Tokens.TryGetValue(inputToken, out login)) return false;
        return StoreInstance().FindAccount(login) != null;
    }

    private static bool IsOwner(HttpListenerContext ctx, string token, out string login)
    {
        login = "";
        if (!Tokens.TryGetValue(token, out login)) return false;
        return StoreInstance().FindAccount(login)?.Role == "owner";
    }

    // ---------- endpoints ----------

    private static void Register(HttpListenerContext ctx)
    {
        var req = ReadBody<RegisterRequest>(ctx);
        if (req == null) { JsonErr(ctx, 400, "bad body"); return; }
        string login = (req.Login ?? "").Trim();
        if (login.Length < 3 || login.Length > 24 || !System.Text.RegularExpressions.Regex.IsMatch(login, "^[A-Za-z0-9_]+$"))
        {
            JsonErr(ctx, 400, "login: 3-24 chars, letters/digits/_");
            return;
        }
        if (string.IsNullOrEmpty(req.Password) || req.Password.Length < 4)
        {
            JsonErr(ctx, 400, "password: at least 4 characters");
            return;
        }
        if (!StoreInstance().TryRegister(login, req.Password, out string err))
        {
            JsonErr(ctx, 409, err);
            return;
        }
        string token = Hashing.NewToken();
        Tokens[token] = login;
        JsonOk(ctx, new LoginResponse
        {
            Ok = true,
            Token = token,
            Login = login,
            Role = "user",
        });
        Console.WriteLine($"[+] Registered: {login}");
    }

    private static void Login(HttpListenerContext ctx)
    {
        var req = ReadBody<LoginRequest>(ctx);
        if (req == null) { JsonErr(ctx, 400, "bad body"); return; }
        var acc = StoreInstance().FindAccount((req.Login ?? "").Trim());
        DateTime now = DateTime.UtcNow;
        bool premium = acc != null && acc.PremiumUntil.HasValue && acc.PremiumUntil.Value > now;
        if (acc == null || !Hashing.Verify(req.Password ?? "", acc.PasswordHash))
        {
            JsonErr(ctx, 401, "invalid login or password");
            return;
        }
        string token = Hashing.NewToken();
        Tokens[token] = acc.Login;
        Console.WriteLine($"[+] Login: {acc.Login} ({(premium ? "premium" : "free")})");
        JsonOk(ctx, new LoginResponse
        {
            Ok = true,
            Token = token,
            Login = acc.Login,
            Role = acc.Role,
            IsPremium = premium,
            PremiumUntil = premium ? acc.PremiumUntil : null,
        });
    }

    private static void Logout(HttpListenerContext ctx)
    {
        var req = ReadBody<LogoutRequest>(ctx);
        if (req != null && !string.IsNullOrEmpty(req.Token)) Tokens.TryRemove(req.Token, out _);
        JsonOk(ctx, new ApiResponse { Ok = true });
    }

    private static void Profile(HttpListenerContext ctx)
    {
        string? token = ctx.Request.QueryString["token"];
        if (!TryAuth(ctx, out string login, token ?? ""))
        {
            JsonErr(ctx, 401, "not authorized");
            return;
        }
        var acc = StoreInstance().FindAccount(login)!;
        DateTime now = DateTime.UtcNow;
        bool premium = acc.PremiumUntil.HasValue && acc.PremiumUntil.Value > now;
        JsonOk(ctx, new ProfileResponse
        {
            Ok = true,
            Login = acc.Login,
            Role = acc.Role,
            IsPremium = premium,
            PremiumUntil = premium ? acc.PremiumUntil : null,
        });
    }

    private static void ActivateKey(HttpListenerContext ctx)
    {
        var req = ReadBody<ActivateKeyRequest>(ctx);
        if (req == null) { JsonErr(ctx, 400, "bad body"); return; }
        if (!TryAuth(ctx, out string login, req.Token ?? ""))
        {
            JsonErr(ctx, 401, "not authorized");
            return;
        }
        string code = (req.KeyCode ?? "").Trim().ToUpperInvariant();
        if (!Keys.LooksValid(code))
        {
            JsonErr(ctx, 400, "invalid key format");
            return;
        }
        if (!StoreInstance().ActivateKey(code, login, out DateTime? until, out string err))
        {
            JsonErr(ctx, 409, err);
            return;
        }
        Console.WriteLine($"[+] Premium activated for {login} until {until:u} (key {code})");
        JsonOk(ctx, new ActivateKeyResponse { Ok = true, PremiumUntil = until });
    }

    private static void CreateKey(HttpListenerContext ctx)
    {
        var req = ReadBody<CreateKeyRequest>(ctx);
        if (req == null) { JsonErr(ctx, 400, "bad body"); return; }
        if (!IsOwner(ctx, req.Token ?? "", out string login))
        {
            JsonErr(ctx, 403, "owner only");
            return;
        }
        int total = Keys.TotalHours(Math.Clamp(req.Days, 0, 3650), Math.Clamp(req.Hours, 0, 23));
        if (total <= 0)
        {
            JsonErr(ctx, 400, "duration must be at least 1 hour");
            return;
        }
        if (!StoreInstance().CreateKey(login, total, req.Note, out string code, out string err))
        {
            JsonErr(ctx, 403, err);
            return;
        }
        Console.WriteLine($"[+] Key created by {login}: {code} ({Keys.FormatDuration(total)})");
        JsonOk(ctx, new CreateKeyResponse { Ok = true, KeyCode = code, TotalHours = total });
    }

    private static void ListKeys(HttpListenerContext ctx)
    {
        string? token = ctx.Request.QueryString["token"];
        if (!IsOwner(ctx, token ?? "", out string login))
        {
            JsonErr(ctx, 403, "owner only");
            return;
        }
        var keys = StoreInstance().ListKeys(login, out _);
        var list = keys.Select(k => new KeyInfo
        {
            Code = k.Code,
            TotalHours = k.TotalHours,
            Note = k.Note,
            CreatedAt = k.CreatedAt,
            Used = k.Used,
            UsedBy = k.UsedBy,
            UsedAt = k.UsedAt,
        }).ToList();
        JsonOk(ctx, new ListKeysResponse { Ok = true, Keys = list });
    }

    private static void DeleteKey(HttpListenerContext ctx)
    {
        var req = ReadBody<DeleteKeyRequest>(ctx);
        if (req == null) { JsonErr(ctx, 400, "bad body"); return; }
        if (!IsOwner(ctx, req.Token ?? "", out string login))
        {
            JsonErr(ctx, 403, "owner only");
            return;
        }
        string code = (req.KeyCode ?? "").Trim().ToUpperInvariant();
        if (!StoreInstance().DeleteKey(login, code, out string err))
        {
            JsonErr(ctx, 404, err);
            return;
        }
        Console.WriteLine($"[+] Key deleted: {code}");
        JsonOk(ctx, new ApiResponse { Ok = true });
    }
}