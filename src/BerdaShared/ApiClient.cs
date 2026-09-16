using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace BerdaShared;

// Shared JSON settings — both sides must agree on camelCase on the wire.
public static class Json
{
    public static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
}

public class ApiClient
{
    private readonly HttpClient _http;

    public string BaseUrl { get; }

    public ApiClient(string baseUrl)
    {
        BaseUrl = string.IsNullOrWhiteSpace(baseUrl) ? NormalizeServer("") : baseUrl.TrimEnd('/');
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        _http.BaseAddress = new Uri(BaseUrl + "/", UriKind.Absolute);
    }

    public static string NormalizeServer(string input)
    {
        string s = (input ?? "").Trim();
        if (s.Length == 0) s = "http://127.0.0.1:8080";
        if (!s.StartsWith("http://") && !s.StartsWith("https://")) s = "http://" + s;
        return s.TrimEnd('/');
    }

    private async Task<TResp?> PostAsync<TReq, TResp>(string path, TReq req)
        where TResp : ApiResponse, new()
    {
        try
        {
            var resp = await _http.PostAsJsonAsync(path, req, Json.Opts).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return new TResp { Ok = false, Error = $"HTTP {(int)resp.StatusCode}" };
            return await resp.Content.ReadFromJsonAsync<TResp>(Json.Opts).ConfigureAwait(false);
        }
        catch (TaskCanceledException)
        {
            return new TResp { Ok = false, Error = "timed out" };
        }
        catch (HttpRequestException ex)
        {
            return new TResp { Ok = false, Error = $"server unreachable ({ex.Message})" };
        }
    }

    private async Task<TResp?> GetAsync<TResp>(string path) where TResp : ApiResponse, new()
    {
        try
        {
            var resp = await _http.GetAsync(path).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return new TResp { Ok = false, Error = $"HTTP {(int)resp.StatusCode}" };
            return await resp.Content.ReadFromJsonAsync<TResp>(Json.Opts).ConfigureAwait(false);
        }
        catch (TaskCanceledException)
        {
            return new TResp { Ok = false, Error = "timed out" };
        }
        catch (HttpRequestException ex)
        {
            return new TResp { Ok = false, Error = $"server unreachable ({ex.Message})" };
        }
    }

    public Task<LoginResponse?> RegisterAsync(string login, string password) =>
        PostAsync<RegisterRequest, LoginResponse>("/api/register", new RegisterRequest { Login = login, Password = password });

    public Task<LoginResponse?> LoginAsync(string login, string password) =>
        PostAsync<LoginRequest, LoginResponse>("/api/login", new LoginRequest { Login = login, Password = password });

    public Task<ProfileResponse?> ProfileAsync(string token) =>
        GetAsync<ProfileResponse>($"/api/profile?token={Uri.EscapeDataString(token)}");

    public Task<ActivateKeyResponse?> ActivateKeyAsync(string token, string code) =>
        PostAsync<ActivateKeyRequest, ActivateKeyResponse>("/api/premium/activate", new ActivateKeyRequest { Token = token, KeyCode = code });

    public Task<ApiResponse?> LogoutAsync(string token) =>
        PostAsync<LogoutRequest, ApiResponse>("/api/logout", new LogoutRequest { Token = token });

    public Task<CreateKeyResponse?> CreateKeyAsync(string token, int days, int hours, string? note) =>
        PostAsync<CreateKeyRequest, CreateKeyResponse>("/api/keys/create", new CreateKeyRequest { Token = token, Days = days, Hours = hours, Note = note });

    public Task<ListKeysResponse?> ListKeysAsync(string token) =>
        GetAsync<ListKeysResponse>($"/api/keys/list?token={Uri.EscapeDataString(token)}");

    public Task<ApiResponse?> DeleteKeyAsync(string token, string code) =>
        PostAsync<DeleteKeyRequest, ApiResponse>("/api/keys/delete", new DeleteKeyRequest { Token = token, KeyCode = code });
}