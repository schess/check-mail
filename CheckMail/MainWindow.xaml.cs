using System.Windows;
using System.Windows.Controls;
using Microsoft.Exchange.WebServices.Data;
using MessageBox = System.Windows.MessageBox;
using Task = System.Threading.Tasks.Task;

namespace CheckMail;

public partial class MainWindow : Window
{
    private readonly Settings _settings;
    private readonly MailChecker _checker;
    private readonly MailPollingService _polling;
    private readonly App _app;

    public MainWindow(Settings settings, MailChecker checker, MailPollingService polling, App app)
    {
        InitializeComponent();
        _settings = settings;
        _checker = checker;
        _polling = polling;
        _app = app;

        // Заполняем форму из настроек
        EwsUrlBox.Text = settings.EwsUrl;
        ImapServerBox.Text = settings.ImapServer;
        ImapPortBox.Text = settings.ImapPort.ToString();
        ImapSslCheck.IsChecked = settings.ImapUseSsl;
        ImapRevocationCheck.IsChecked = settings.ImapSkipRevocationCheck;
        ExcludedFoldersBox.Text = settings.ExcludedFolders;
        ProtocolCombo.SelectedIndex = settings.Protocol == MailProtocol.Imap ? 0 : 1;
        EmailBox.Text = settings.Email;
        UsernameBox.Text = settings.Username;
        PasswordBox.Password = settings.Password;
        DomainBox.Text = settings.Domain;
        AuthCombo.SelectedIndex = settings.UseDefaultCredentials ? 1 : 0;
        AllFoldersCheck.IsChecked = settings.CheckAllFolders;
        StartupCheck.IsChecked = settings.RunAtStartup;

        UpdateProtocolVisibility();

        // Интервал
        var minutes = settings.CheckIntervalSeconds / 60;
        var idx = IntervalCombo.Items
            .Cast<ComboBoxItem>()
            .Select((it, i) => new { it, i })
            .FirstOrDefault(x => x.it.Content.ToString() == minutes.ToString());
        IntervalCombo.SelectedIndex = idx != null ? idx.i : 3;

        // Подписываемся на события проверки
        _polling.StatusChanged += s => SetStatusAsync(s);
        _polling.ErrorOccurred += ex => SetStatusAsync("Ошибка: " + ex.Message);
    }

    private void SaveSettingsFromUi()
    {
        _settings.EwsUrl = EwsUrlBox.Text.Trim();
        _settings.ImapServer = ImapServerBox.Text.Trim();
        if (int.TryParse(ImapPortBox.Text.Trim(), out var port) && port > 0)
            _settings.ImapPort = port;
        _settings.ImapUseSsl = ImapSslCheck.IsChecked == true;
        _settings.ImapSkipRevocationCheck = ImapRevocationCheck.IsChecked == true;
        _settings.ExcludedFolders = ExcludedFoldersBox.Text.Trim();
        _settings.Protocol = ProtocolCombo.SelectedIndex == 0 ? MailProtocol.Imap : MailProtocol.Ews;
        _settings.Email = EmailBox.Text.Trim();
        _settings.Username = UsernameBox.Text.Trim();
        _settings.Password = PasswordBox.Password;
        _settings.Domain = DomainBox.Text.Trim();
        _settings.UseDefaultCredentials = AuthCombo.SelectedIndex == 1 && _settings.Protocol == MailProtocol.Ews;
        _settings.CheckAllFolders = AllFoldersCheck.IsChecked == true;
        _settings.RunAtStartup = StartupCheck.IsChecked == true;
        if (IntervalCombo.SelectedItem is ComboBoxItem item &&
            int.TryParse(item.Content.ToString(), out var minutes))
        {
            _settings.CheckIntervalSeconds = minutes * 60;
            _polling.IntervalSeconds = minutes * 60;
        }
        _settings.Save();
        ApplyAutoStart(_settings.RunAtStartup);
    }

    /// <summary>Показывает/скрывает поля в зависимости от выбранного протокола.</summary>
    private void UpdateProtocolVisibility()
    {
        bool isImap = ProtocolCombo.SelectedIndex == 0;

        // IMAP-специфичные поля
        ImapServerLabel.Visibility = isImap ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        ImapServerBox.Visibility = isImap ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        ImapPortPanel.Visibility = isImap ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        ImapRevocationCheck.Visibility = isImap ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        ExcludedFoldersLabel.Visibility = isImap ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        ExcludedFoldersBox.Visibility = isImap ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

        // EWS-специфичные поля
        EwsUrlLabel.Visibility = isImap ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
        EwsUrlBox.Visibility = isImap ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
        AuthLabel.Visibility = isImap ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
        AuthCombo.Visibility = isImap ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
        AllFoldersCheck.Visibility = isImap ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
    }

    private void ProtocolCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        UpdateProtocolVisibility();
    }

    /// <summary>Добавляет/убирает автозапуск через реестр (HKCU\...\Run).</summary>
    private static void ApplyAutoStart(bool enable)
    {
        const string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string valueName = "CheckMail";
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(runKey, true);
            if (key == null) return;
            if (enable)
            {
                key.SetValue(valueName, "\"" + System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName + "\"");
            }
            else
            {
                if (key.GetValue(valueName) != null) key.DeleteValue(valueName, false);
            }
        }
        catch
        {
            // не критично
        }
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        SaveSettingsFromUi();
        SetStatus("Проверка подключения...");
        TestBtn.IsEnabled = false;
        // Сетевые вызовы синхронные и могут идти долго, поэтому выполняем их
        // в фоновом потоке, чтобы не замораживать UI.
        try
        {
            if (_settings.Protocol == MailProtocol.Imap)
            {
                await Task.Run(() => _checker.TestImapConnection(_settings));
                SetStatus("Подключение по IMAP успешно. Входящие доступны.");
                MessageBox.Show("Подключение по IMAP прошло успешно.\nLogin: " + (_settings.Username ?? _settings.Email),
                                "Проверка", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                var service = await Task.Run(() => _checker.CreateService(_settings));
                // Простой способ проверить подключение — получить имя текущего пользователя
                var user = await Task.Run(() => service.ResolveName(_settings.Email, ResolveNameSearchLocation.DirectoryThenContacts, true).FirstOrDefault());
                SetStatus(user == null
                    ? "Подключение успешно (сервер доступен)."
                    : $"Подключение успешно. Пользователь: {user.Mailbox?.Address ?? _settings.Email}");
                MessageBox.Show("Подключение к Exchange прошло успешно.", "Проверка", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            SetStatus("Ошибка: " + ex.Message);
            MessageBox.Show("Не удалось подключиться.\n\n" + ex.Message,
                            "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            TestBtn.IsEnabled = true;
        }
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        SaveSettingsFromUi();
        StartBtn.IsEnabled = false;
        StopBtn.IsEnabled = true;
        TestBtn.IsEnabled = false;
        _polling.Start(_settings);
        SetStatus("Проверка почты запущена.");
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _polling.Stop();
        StartBtn.IsEnabled = true;
        StopBtn.IsEnabled = false;
        TestBtn.IsEnabled = true;
    }

    private void SetStatus(string text)
    {
        Dispatcher.Invoke(() =>
        {
            StatusText.Text = text;
            LastRunText.Text = _polling.LastRun != default
                ? "Последняя проверка: " + _polling.LastRun.ToString("HH:mm:ss")
                : "";
        });
    }

    private void SetStatusAsync(string text) => SetStatus(text);

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // При закрытии окна настроек оставляем приложение работать в трее
        e.Cancel = true;
        Hide();
        _app.ShowTrayBalloon("Проверка почты работает в фоне",
                             "Приложение продолжает работать из системного трея.");
    }
}
