using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BerdaShared;

namespace BerdaKeyGen;

public partial class MainWindow : Window
{
    private sealed class KeyRow
    {
        public string Code { get; set; } = "";
        public string DurationText { get; set; } = "";
        public string StatusText { get; set; } = "";
        public string UsedByText { get; set; } = "";
        public string Raw { get; set; } = "";
    }

    private sealed class Config
    {
        public string ServerUrl { get; set; } = "http://127.0.0.1:8080";
    }

    private readonly string _configPath;
    private Config _cfg;
    private ApiClient _api;
    private string _token = "";
    private bool _isOwner;

    public MainWindow()
    {
        InitializeComponent();
        _configPath = Path.Combine(AppContext.BaseDirectory, "keygen_config.json");
        _cfg = LoadConfig();
        ServerField.Text = _cfg.ServerUrl;
        _api = new ApiClient(_cfg.ServerUrl);

        HintHelper.HookPasswordHint(PassField, PassFieldHint);
        KeysList.SelectionChanged += (_, _) => DelKeyBtn.IsEnabled = KeysList.SelectedItem != null;
        PreviewKeyDown += OnPreviewKeyDown;
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
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    // ---------------- config ----------------

    private static readonly JsonSerializerOptions CfgJson = new() { WriteIndented = true };

    private Config LoadConfig()
    {
        try
        {
            if (File.Exists(_configPath))
                return JsonSerializer.Deserialize<Config>(File.ReadAllText(_configPath), CfgJson) ?? new Config();
        }
        catch { /* ignore */ }
        var c = new Config();
        try { File.WriteAllText(_configPath, JsonSerializer.Serialize(c, CfgJson)); } catch { /* ignore */ }
        return c;
    }

    private void SaveConfig()
    {
        try { File.WriteAllText(_configPath, JsonSerializer.Serialize(_cfg, CfgJson)); } catch { /* ignore */ }
    }

    // ---------------- nav ----------------

    private void ShowLogin()
    {
        LoginPanel.Visibility = Visibility.Visible;
        MainPanel.Visibility = Visibility.Collapsed;
    }

    private void ShowMain()
    {
        LoginPanel.Visibility = Visibility.Collapsed;
        MainPanel.Visibility = Visibility.Visible;
        DaysField.Text = "30";
        HoursField.Text = "0";
        NoteField.Text = "";
    }

    // ---------------- login ----------------

    private void SetStatus(System.Windows.Controls.TextBlock t, string s, bool ok = false)
    {
        t.Text = s;
        t.Visibility = Visibility.Visible;
        t.Foreground = ok
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x7F, 0xFF, 0xA0))
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
            if (r.Role != "owner")
            {
                SetStatus(LoginStatus, "Доступ только для владельца (owner)");
                return;
            }
            _token = r.Token ?? "";
            _isOwner = true;
            LoginField.Text = "";
            PassField.Password = "";
            LoginStatus.Visibility = Visibility.Collapsed;
            ShowMain();
            await RefreshKeys();
        }
        catch (Exception ex)
        {
            SetStatus(LoginStatus, "Сервер недоступен: " + ex.Message);
        }
    }

    private async void LogoutBtn_Click(object sender, RoutedEventArgs e)
    {
        try { await _api.LogoutAsync(_token); } catch { /* ignore */ }
        _token = "";
        _isOwner = false;
        ShowLogin();
    }

    private void PassField_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) LoginBtn_Click(sender, e);
    }

    // ---------------- server ----------------

    private void ServerToggle_Click(object sender, RoutedEventArgs e)
        => ServerGrid.Visibility = ServerGrid.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

    private void ServerSave_Click(object sender, RoutedEventArgs e)
    {
        _cfg.ServerUrl = ServerField.Text.Trim();
        SaveConfig();
        _api = new ApiClient(_cfg.ServerUrl);
        ServerGrid.Visibility = Visibility.Collapsed;
    }

    // ---------------- keys ----------------

    private async Task RefreshKeys()
    {
        try
        {
            var r = await _api.ListKeysAsync(_token);
            if (r == null || !r.Ok) return;
            var rows = new List<KeyRow>();
            foreach (var k in r.Keys ?? new List<KeyInfo>())
            {
                rows.Add(new KeyRow
                {
                    Code = k.Code ?? "",
                    DurationText = Keys.FormatDuration(k.TotalHours),
                    StatusText = k.Used ? "ИСПОЛЬЗОВАН" : "АКТИВЕН",
                    UsedByText = k.Used ? $"{k.UsedBy} {k.UsedAt:dd.MM}" : "—",
                    Raw = k.Code ?? "",
                });
            }
            KeysList.ItemsSource = rows;
        }
        catch { /* ignore */ }
    }

    private async void CreateBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!_isOwner) return;
        if (!int.TryParse(DaysField.Text, out int days) || days < 0 || days > 3650)
        {
            SetStatus(CreateStatus, "Дней: 0–3650");
            return;
        }
        if (!int.TryParse(HoursField.Text, out int hours) || hours < 0 || hours > 23)
        {
            SetStatus(CreateStatus, "Часов: 0–23");
            return;
        }
        if (Keys.TotalHours(days, hours) <= 0)
        {
            SetStatus(CreateStatus, "Минимум 1 час");
            return;
        }
        try
        {
            var r = await _api.CreateKeyAsync(_token, days, hours, NoteField.Text.Trim());
            if (r == null || !r.Ok) { SetStatus(CreateStatus, r?.Error ?? "Ошибка"); return; }
            ResultKey.Text = r.KeyCode ?? "";
            ResPanelVisible(true);
            SetStatus(CreateStatus, "", false);
            CreateStatus.Visibility = Visibility.Collapsed;
            await RefreshKeys();
        }
        catch (Exception ex)
        {
            SetStatus(CreateStatus, "Сервер недоступен: " + ex.Message);
        }
    }

    private void ResPanelVisible(bool on)
    {
        var grid = ResultKey.Parent as Grid;
        if (grid != null) grid.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (CopyBtn != null) CopyBtn.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CopyBtn_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(ResultKey.Text)) return;
        try
        {
            Clipboard.SetText(ResultKey.Text);
            CopyBtn.Content = "СКОПИРОВАНО";
            CopyBtn.IsEnabled = false;
            _ = Task.Delay(1400).ContinueWith(_ =>
            {
                Dispatcher.Invoke(() =>
                {
                    CopyBtn.Content = "КОПИРОВАТЬ";
                    CopyBtn.IsEnabled = true;
                });
            });
        }
        catch (Exception ex)
        {
            SetStatus(CreateStatus, "Буфер обмена: " + ex.Message);
        }
    }

    private async void DelKeyBtn_Click(object sender, RoutedEventArgs e)
    {
        if (KeysList.SelectedItem is not KeyRow row) return;
        try
        {
            var r = await _api.DeleteKeyAsync(_token, row.Raw);
            if (r == null || !r.Ok) return;
            await RefreshKeys();
        }
        catch { /* ignore */ }
    }
}