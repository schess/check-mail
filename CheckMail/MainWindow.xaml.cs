using System.Windows;
using System.Windows.Controls;
using Microsoft.Exchange.WebServices.Data;
using MessageBox = System.Windows.MessageBox;

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
        EmailBox.Text = settings.Email;
        UsernameBox.Text = settings.Username;
        PasswordBox.Password = settings.Password;
        DomainBox.Text = settings.Domain;
        AuthCombo.SelectedIndex = settings.UseDefaultCredentials ? 1 : 0;
        AllFoldersCheck.IsChecked = settings.CheckAllFolders;
        StartupCheck.IsChecked = settings.RunAtStartup;

        // Интервал
        var minutes = settings.CheckIntervalSeconds / 60;
        var idx = IntervalCombo.Items
            .Cast<ComboBoxItem>()
            .Select((it, i) => new { it, i })
            .FirstOrDefault(x => x.it.Content.ToString() == minutes.ToString());
        IntervalCombo.SelectedIndex = idx != null ? idx.i : 3;

        // Подписываемся на события проверки
        _polling.StatusChanged += s => SetStatusAsync(s);
        _polling.NewMailFound += ShowNotificationAsync;
        _polling.ErrorOccurred += ex => SetStatusAsync("Ошибка: " + ex.Message);
    }

    private void SaveSettingsFromUi()
    {
        _settings.Email = EmailBox.Text.Trim();
        _settings.Username = UsernameBox.Text.Trim();
        _settings.Password = PasswordBox.Password;
        _settings.Domain = DomainBox.Text.Trim();
        _settings.UseDefaultCredentials = AuthCombo.SelectedIndex == 1;
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
        try
        {
            var service = _checker.CreateService(_settings);
            // Простой способ проверить подключение — получить имя текущего пользователя
            var user = service.ResolveName(_settings.Email, ResolveNameSearchLocation.DirectoryThenContacts, true).FirstOrDefault();
            SetStatus(user == null
                ? "Подключение успешно (сервер доступен)."
                : $"Подключение успешно. Пользователь: {user.Mailbox?.Address ?? _settings.Email}");
            TestBtn.IsEnabled = false;
            MessageBox.Show("Подключение к Exchange прошло успешно.", "Проверка", MessageBoxButton.OK, MessageBoxImage.Information);
            TestBtn.IsEnabled = true;
        }
        catch (Exception ex)
        {
            TestBtn.IsEnabled = true;
            SetStatus("Ошибка: " + ex.Message);
            MessageBox.Show("Не удалось подключиться к Exchange.\n\n" + ex.Message,
                            "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
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

    private void ShowNotificationAsync(NewMailInfo mail)
    {
        Dispatcher.Invoke(() =>
        {
            var win = new NotificationWindow(mail);
            win.PositionTopRight();
            win.Show();
        });
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
