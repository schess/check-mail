using System.Windows;
using Application = System.Windows.Application;

namespace CheckMail;

public partial class App : Application
{
    private TrayIcon? _tray;
    private MainWindow? _mainWindow;

    public Settings Settings { get; private set; } = new();
    public MailChecker Checker { get; private set; } = null!;
    public MailPollingService Polling { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Читаем настройки
        Settings = Settings.Load();

        // Если адрес не задан — подставляем ваш Exchange по умолчанию
        if (string.IsNullOrEmpty(Settings.EwsUrl))
        {
            Settings.EwsUrl = "https://mail.cit.gov.ru";
            Settings.Save();
        }

        // Создаём сервисы
        Checker = new MailChecker();
        Polling = new MailPollingService(Checker);

        // Иконка в трее
        _tray = new TrayIcon();
        _tray.OpenRequested += () => Dispatcher.Invoke(ShowSettings);
        _tray.ExitRequested += () => Dispatcher.Invoke(ShutdownApp);

        // Уведомление о новой почте
        Polling.NewMailFound += m => Dispatcher.Invoke(() => ShowNewMailNotification(m));

        // Показываем окно настроек
        ShowSettings();

        // Автозапуск проверки
        // (интервал и параметры берутся из сохранённых настроек)
        if (!string.IsNullOrEmpty(Settings.Email) || !string.IsNullOrEmpty(Settings.EwsUrl))
        {
            Polling.Start(Settings);
        }
    }

    private void ShowSettings()
    {
        if (_mainWindow == null)
        {
            _mainWindow = new MainWindow(Settings, Checker, Polling, this);
        }
        _mainWindow.Show();
        _mainWindow.Activate();
    }

    private void ShowNewMailNotification(NewMailInfo mail)
    {
        var win = new NotificationWindow(mail);
        win.PositionTopRight();
        win.Show();
    }

    public void ShowTrayBalloon(string title, string message)
    {
        _tray?.ShowBalloon(title, message);
    }

    private void ShutdownApp()
    {
        _tray?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        base.OnExit(e);
    }
}
