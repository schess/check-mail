using System.Windows;
using Application = System.Windows.Application;

namespace CheckMail;

public partial class App : Application
{
    private TrayIcon? _tray;
    private MainWindow? _mainWindow;
    private UnreadListWindow? _unreadWindow;

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
        }
        if (string.IsNullOrEmpty(Settings.ImapServer))
        {
            Settings.ImapServer = "mail.cit.gov.ru";
        }
        Settings.Save();

        // Создаём сервисы
        Checker = new MailChecker();
        Polling = new MailPollingService(Checker);

        // Иконка в трее
        _tray = new TrayIcon();
        _tray.OpenRequested += () => Dispatcher.Invoke(ShowSettings);
        _tray.ExitRequested += () => Dispatcher.Invoke(ShutdownApp);
        _tray.OpenUnreadRequested += () => Dispatcher.Invoke(ShowUnreadList);

        // Уведомление о новой почте
        Polling.NewMailFound += m => Dispatcher.Invoke(() => ShowNewMailNotification(m));

        // Список непрочитанных изменился — обновляем счётчик в трее и окно списка
        Checker.UnreadListChanged += list => Dispatcher.Invoke(() => OnUnreadListChanged(list));

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

    /// <summary>
    /// Показывает (или обновляет) постоянное окно со списком непрочитанных.
    /// Оно не закрывается само по себе, в отличие от всплывающих уведомлений.
    /// </summary>
    public void ShowUnreadList(List<NewMailInfo>? unread = null)
    {
        unread ??= Checker.LastUnread;

        if (_unreadWindow == null)
        {
            // ВАЖНО: намеренно НЕ задаём Owner. В WPF окно с Owner скрывается
            // вместе с владельцем и закрывается при его закрытии — из-за этого
            // список непрочитанных пропадал вместе с окном настроек.
            _unreadWindow = new UnreadListWindow
            {
                ShowInTaskbar = true
            };
            _unreadWindow.RefreshRequested += () =>
                _ = Polling.RunCheckAsync(Settings);
            _unreadWindow.Closed += (_, _) => _unreadWindow = null;
        }

        _unreadWindow.UpdateUnread(unread);
        _unreadWindow.Show();
        _unreadWindow.Activate();
    }

    /// <summary>Список непрочитанных обновился: счётчик в трее + окно списка.</summary>
    private void OnUnreadListChanged(List<NewMailInfo> unread)
    {
        int count = unread.Count;

        // Счётчик непрочитанных всегда виден в подсказке трея
        _tray?.SetUnreadCount(count);

        // Если окно списка уже открыто — обновляем его на месте
        if (_unreadWindow != null)
        {
            _unreadWindow.UpdateUnread(unread);
        }
        else if (count > 0)
        {
            ShowUnreadList(unread);
        }
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