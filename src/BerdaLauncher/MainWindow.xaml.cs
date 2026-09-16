using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using BerdaShared;

namespace BerdaLauncher;

public partial class MainWindow : Window
{
    private sealed class LauncherConfig
    {
        public string ServerUrl { get; set; } = "http://127.0.0.1:8080";
        public string LoaderPath { get; set; } = "";
        public string GamePath { get; set; } = "";
    }

    private readonly string _configPath;
    private LauncherConfig _cfg;
    private ApiClient _api;

    private string _token = "";
    private string _login = "";
    private bool _isPremium;
    private DateTime? _premiumUntil;

    private FileSystemWatcher? _lgw;
    private long _lgPos;
    private string _logPath = "";
    private DateTime _lastLogRead = DateTime.MinValue;

    public MainWindow()
    {
        InitializeComponent();

        _configPath = Path.Combine(AppContext.BaseDirectory, "launcher_config.json");
        _cfg = LoadConfig();
        ServerField.Text = _cfg.ServerUrl;
        _api = new ApiClient(_cfg.ServerUrl);

        PreviewKeyDown += OnPreviewKeyDown;
        HintHelper.HookPasswordHint(PassField, PassFieldHint);
        HintHelper.HookPasswordHint(RegPass, RegPassHint);
        HintHelper.HookPasswordHint(RegPass2, RegPass2Hint);
        LogLine("Лаунчер запущен");
    }

    // ---------------- window ----------------

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            try { DragMove(); } catch { /* ignore */ }
    }

    private void MinimizeBtn_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && PremiumOverlay.Visibility == Visibility.Visible)
        {
            HideOverlay();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && RegisterPanel.Visibility == Visibility.Visible)
        {
            BackToLoginBtn_Click(sender, e);
            e.Handled = true;
        }
    }

    // ---------------- config ----------------

    private static readonly JsonSerializerOptions CfgJson = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private LauncherConfig LoadConfig()
    {
        try
        {
            if (File.Exists(_configPath))
                return JsonSerializer.Deserialize<LauncherConfig>(File.ReadAllText(_configPath), CfgJson)
                       ?? new LauncherConfig();
        }
        catch { /* ignore */ }
        var c = new LauncherConfig();
        SaveConfig(c);
        return c;
    }

    private void SaveConfig(LauncherConfig c)
    {
        try { File.WriteAllText(_configPath, JsonSerializer.Serialize(c, CfgJson)); }
        catch { /* ignore */ }
    }

    private void SaveConfig() => SaveConfig(_cfg);

    // ---------------- nav ----------------

    private void ShowLogin()
    {
        LoginPanel.Visibility = Visibility.Visible;
        RegisterPanel.Visibility = Visibility.Collapsed;
        MainPanel.Visibility = Visibility.Collapsed;
    }

    private void ShowRegister()
    {
        LoginPanel.Visibility = Visibility.Collapsed;
        RegisterPanel.Visibility = Visibility.Visible;
        MainPanel.Visibility = Visibility.Collapsed;
    }

    private void ShowMain()
    {
        LoginPanel.Visibility = Visibility.Collapsed;
        RegisterPanel.Visibility = Visibility.Collapsed;
        MainPanel.Visibility = Visibility.Visible;

        HelloText.Text = $"Привет, {_login}!";
        PremiumBadge.Visibility = _isPremium ? Visibility.Visible : Visibility.Collapsed;
        PremiumBadgeText.Text = _isPremium ? "PREMIUM" : "FREE";
        PremiumStatus.Text = _isPremium
            ? $"Premium активен до {_premiumUntil:dd.MM.yyyy}"
            : "Free аккаунт — нажмите Premium и активируйте ключ";
        InjectBtn.IsEnabled = _isPremium;
    }

    private void GotoRegBtn_Click(object sender, RoutedEventArgs e) { LoginStatus.Visibility = Visibility.Collapsed; ShowRegister(); }
    private void BackToLoginBtn_Click(object sender, RoutedEventArgs e) { RegStatus.Visibility = Visibility.Collapsed; ShowLogin(); }

    // ---------------- auth ----------------

    private void SetStatus(System.Windows.Controls.TextBlock t, string s, bool ok = false)
    {
        t.Text = s;
        t.Visibility = Visibility.Visible;
        t.Foreground = ok ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x7F, 0xFF, 0xA0))
                          : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x6E, 0x6E));
    }

    private async void LoginBtn_Click(object sender, RoutedEventArgs e)
    {
        var login = LoginField.Text.Trim();
        if (login.Length == 0 || PassField.Password.Length == 0)
        {
            SetStatus(LoginStatus, "Введите логин и пароль");
            return;
        }
        try
        {
            var r = await _api.LoginAsync(login, PassField.Password);
            if (!r.Ok) { SetStatus(LoginStatus, r.Error ?? "Ошибка входа"); return; }
            ApplySession(r);
        }
        catch (Exception ex)
        {
            SetStatus(LoginStatus, "Сервер недоступен: " + ex.Message);
        }
    }

    private async void RegBtn_Click(object sender, RoutedEventArgs e)
    {
        var login = RegLoginField.Text.Trim();
        if (login.Length < 3 || login.Length > 24)
        {
            SetStatus(RegStatus, "Логин: 3–24 символа (буквы/цифры/_)");
            return;
        }
        if (RegPass.Password.Length < 4)
        {
            SetStatus(RegStatus, "Пароль должен быть не короче 4 символов");
            return;
        }
        if (RegPass.Password != RegPass2.Password)
        {
            SetStatus(RegStatus, "Пароли не совпадают");
            return;
        }
        try
        {
            var r = await _api.RegisterAsync(login, RegPass.Password);
            if (!r.Ok) { SetStatus(RegStatus, r.Error ?? "Ошибка регистрации"); return; }
            ApplySession(r);
        }
        catch (Exception ex)
        {
            SetStatus(RegStatus, "Сервер недоступен: " + ex.Message);
        }
    }

    private void ApplySession(LoginResponse r)
    {
        _token = r.Token ?? "";
        _login = r.Login ?? "";
        _isPremium = r.IsPremium;
        _premiumUntil = r.PremiumUntil;
        LoginField.Text = "";
        PassField.Password = "";
        RegLoginField.Text = "";
        RegPass.Password = "";
        RegPass2.Password = "";
        LoginStatus.Visibility = Visibility.Collapsed;
        RegStatus.Visibility = Visibility.Collapsed;
        LogLine($"Вход выполнен: {_login} ({(r.IsPremium ? "premium" : "free")})");
        ShowMain();
    }

    private async void LogoutBtn_Click(object sender, RoutedEventArgs e)
    {
        try { await _api.LogoutAsync(_token); } catch { /* ignore */ }
        _token = "";
        _login = "";
        _isPremium = false;
        _premiumUntil = null;
        StopLogWatch();
        LogLine("Сеанс завершён");
        ShowLogin();
        PassField.Clear();
    }

    // ---------------- server config ----------------

    private void ServerToggle_Click(object sender, RoutedEventArgs e)
        => ServerGrid.Visibility = ServerGrid.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

    private void ServerSave_Click(object sender, RoutedEventArgs e)
    {
        _cfg.ServerUrl = ServerField.Text.Trim();
        SaveConfig();
        _api = new ApiClient(_cfg.ServerUrl);
        ServerGrid.Visibility = Visibility.Collapsed;
        LogLine("Сервер: " + _cfg.ServerUrl);
    }

    // ---------------- premium ----------------

    private void ShowOverlay()
    {
        PremiumKey.Text = "";
        PremiumOverlayStatus.Visibility = Visibility.Collapsed;
        PremiumOverlay.Visibility = Visibility.Visible;
        PremiumKey.Focus();
    }

    private void HideOverlay()
    {
        PremiumOverlay.Visibility = Visibility.Collapsed;
        PremiumOverlayStatus.Visibility = Visibility.Collapsed;
    }

    private void OverlayClose_Click(object sender, RoutedEventArgs e) => HideOverlay();

    private void PremiumOverlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == PremiumOverlay || e.OriginalSource == OverlayCard)
        {
            if (e.OriginalSource == PremiumOverlay) HideOverlay();
        }
    }

    private void PremiumKey_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) ActivateBtn_Click(sender, e);
    }

    private async void ActivateBtn_Click(object sender, RoutedEventArgs e)
    {
        var code = PremiumKey.Text.Trim();
        if (code.Length == 0)
        {
            SetStatus(PremiumOverlayStatus, "Введите ключ");
            return;
        }
        try
        {
            var r = await _api.ActivateKeyAsync(_token, code);
            if (!r.Ok) { SetStatus(PremiumOverlayStatus, r.Error ?? "Ошибка активации"); return; }
            _isPremium = true;
            _premiumUntil = r.PremiumUntil;
            SetStatus(PremiumOverlayStatus, "Ключ активирован!", true);
            MainPanel_RefreshPremium();
            LogLine($"Premium активирован до {r.PremiumUntil:dd.MM.yyyy}");
            await Task.Delay(900);
            HideOverlay();
        }
        catch (Exception ex)
        {
            SetStatus(PremiumOverlayStatus, "Сервер недоступен: " + ex.Message);
        }
    }

    private void PremiumBtn_Click(object sender, RoutedEventArgs e) => ShowOverlay();

    private void MainPanel_RefreshPremium()
    {
        PremiumBadge.Visibility = _isPremium ? Visibility.Visible : Visibility.Collapsed;
        PremiumBadgeText.Text = _isPremium ? "PREMIUM" : "FREE";
        PremiumStatus.Text = _isPremium
            ? $"Premium активен до {_premiumUntil:dd.MM.yyyy}"
            : "Free аккаунт — нажмите Premium и активируйте ключ";
        InjectBtn.IsEnabled = _isPremium;
    }

    // ---------------- inject ----------------

    private static bool IsRunning(string processName)
    {
        try { return Process.GetProcessesByName(processName).Length > 0; }
        catch { return false; }
    }

    private string? ResolveLoader()
    {
        if (!string.IsNullOrEmpty(_cfg.LoaderPath) && File.Exists(_cfg.LoaderPath)) return _cfg.LoaderPath;
        var def = Path.Combine(AppContext.BaseDirectory, "EstikClient.exe");
        return File.Exists(def) ? def : null;
    }

    private async void InjectBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!_isPremium)
        {
            LogLine("[!] Premium не активирован");
            return;
        }
        var loader = ResolveLoader();
        if (loader == null)
        {
            LogLine("[!] EstikClient.exe не найден рядом с лаунчером. Укажите LoaderPath в launcher_config.json");
            return;
        }

        if (!string.IsNullOrEmpty(_cfg.GamePath) && !IsRunning("Berda"))
        {
            try
            {
                var gp = _cfg.GamePath;
                var psi = new ProcessStartInfo
                {
                    FileName = gp,
                    WorkingDirectory = Path.GetDirectoryName(gp) ?? "",
                    UseShellExecute = true,
                };
                Process.Start(psi);
                LogLine("[i] Запускаю игру... ждём Berda.exe");
                await Task.Delay(800);
            }
            catch (Exception ex)
            {
                LogLine($"[!] Не удалось запустить игру: {ex.Message}");
                return;
            }
        }
        else if (IsRunning("Berda"))
        {
            LogLine("[i] Игра уже запущена");
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = loader,
                WorkingDirectory = Path.GetDirectoryName(loader) ?? "",
                Arguments = "-w",
                CreateNoWindow = true,
                UseShellExecute = false,
            };
            var p = Process.Start(psi)!;
            p.EnableRaisingEvents = true;
            p.Exited += (_, _) => Dispatch(() => LogLine($"[i] Инжектор завершён (код {p.ExitCode})"));
            LogLine("[i] Инжектор запущен (EstikClient.exe -w)");
            StartLogWatch(loader);
        }
        catch (Exception ex)
        {
            LogLine($"[!] Ошибка запуска инжектора: {ex.Message}");
        }
    }

    // ---------------- loader log tailing ----------------

    private void StartLogWatch(string loaderExe)
    {
        StopLogWatch();
        string dir = Path.GetDirectoryName(loaderExe) ?? AppContext.BaseDirectory;
        string logName = Path.GetFileNameWithoutExtension(loaderExe) + "_log.txt";
        _logPath = Path.Combine(dir, logName);
        try { _lgPos = new FileInfo(_logPath).Length; } catch { _lgPos = 0; }
        try
        {
            _lgw = new FileSystemWatcher(dir, logName) { EnableRaisingEvents = true };
            _lgw.Changed += Lgw_Changed;
            _lgw.Created += Lgw_Changed;
        }
        catch (Exception ex)
        {
            LogLine($"[!] Watcher: {ex.Message}");
        }
        foreach (var line in TryReadTail()) LogLine("  " + line);
    }

    private void StopLogWatch()
    {
        if (_lgw != null)
        {
            _lgw.Changed -= Lgw_Changed;
            _lgw.Created -= Lgw_Changed;
            _lgw.Dispose();
            _lgw = null;
        }
    }

    private string[] TryReadTail()
    {
        try
        {
            using var fs = new FileStream(_logPath, FileMode.Open, FileAccess.Read,
                                          FileShare.ReadWrite | FileShare.Delete);
            fs.Seek(_lgPos, SeekOrigin.Begin);
            using var sr = new StreamReader(fs, Encoding.Latin1,
                                            detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
            var text = sr.ReadToEnd();
            _lgPos = fs.Position;
            if (string.IsNullOrEmpty(text)) return Array.Empty<string>();
            return text.Split('\n');
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private void Lgw_Changed(object sender, FileSystemEventArgs e)
    {
        var now = DateTime.UtcNow;
        if ((now - _lastLogRead).TotalMilliseconds < 120) return;
        _lastLogRead = now;
        string[] lines;
        lock (this) { lines = TryReadTail(); }
        if (lines.Length == 0) return;
        foreach (var line in lines)
        {
            var s = line.TrimEnd('\r');
            if (s.Length > 0) Dispatch(() => LogLine("  " + s));
        }
    }

    // ---------------- log ----------------

    private void Dispatch(Action a)
    {
        if (Dispatcher.CheckAccess()) a();
        else Dispatcher.BeginInvoke(a);
    }

    private void LogLine(string s)
    {
        LogList.Items.Add($"[{DateTime.Now:HH:mm:ss}] {s}");
        if (LogList.Items.Count > 400) LogList.Items.RemoveAt(0);
        LogList.ScrollIntoView(LogList.Items[LogList.Items.Count - 1]);
    }

    private void PassField_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) LoginBtn_Click(sender, e);
    }

    private void RegPass2_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) RegBtn_Click(sender, e);
    }
}