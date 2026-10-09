using System.Diagnostics;
using System.Windows.Threading;

namespace CheckMail;

/// <summary>
/// Запускает проверку почты по расписанию (каждые N секунд).
/// Управляется с UI-потока через DispatcherTimer.
/// </summary>
public class MailPollingService
{
    private readonly MailChecker _checker;
    private DispatcherTimer? _timer;
    private DateTime _lastRun;

    /// <summary>Сервис включён (таймер создан и запущен).</summary>
    private bool _isRunning;

    /// <summary>Сейчас выполняется проверка — защита от наложения прогонов.</summary>
    private bool _isChecking;

    public event Action<NewMailInfo>? NewMailFound;
    public event Action<string>? StatusChanged;
    public event Action<Exception>? ErrorOccurred;

    public int IntervalSeconds { get; set; } = 300;

    public DateTime LastRun => _lastRun;
    public bool IsRunning => _isRunning;

    public MailPollingService(MailChecker checker)
    {
        _checker = checker;
        _checker.NewMailFound += m => NewMailFound?.Invoke(m);
    }

    public void Start(Settings settings)
    {
        Stop();

        IntervalSeconds = settings.CheckIntervalSeconds;
        if (IntervalSeconds < 30) IntervalSeconds = 30; // минимальный интервал 30 секунд

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(IntervalSeconds)
        };
        _timer.Tick += async (_, _) =>
        {
            // Пропускаем тик, только если предыдущая проверка ещё идёт.
            // Проверять нужно _isChecking, а не _isRunning: последний остаётся
            // true всё время работы таймера, и с ним тики не срабатывали бы.
            if (_isChecking) return;
            await RunCheckAsync(settings);
        };

        _isRunning = true;
        _timer.Start();
        StatusChanged?.Invoke("Проверка почты запущена");

        // Немедленная первая проверка (без ожидания интервала)
        _ = RunCheckAsync(settings);
    }

    public void Stop()
    {
        _isRunning = false;
        _isChecking = false;
        if (_timer != null)
        {
            _timer.Stop();
            _timer = null;
        }
        StatusChanged?.Invoke("Проверка остановлена");
    }

    public async Task RunCheckAsync(Settings settings)
    {
        if (_isChecking) return;       // проверка уже идёт
        _isChecking = true;

        try
        {
            _lastRun = DateTime.Now;
            StatusChanged?.Invoke($"Проверка почты... ({_lastRun:HH:mm:ss})");
            int found = await _checker.CheckForNewMailAsync(settings);

            // Показываем, сколько всего непрочитанных в ящике — это то, что
            // интересует пользователя, а не только «сколько окон мы открыли».
            var unread = _checker.LastUnreadTotal;
            var where = string.IsNullOrEmpty(_checker.LastUnreadFolders)
                ? ""
                : $" ({_checker.LastUnreadFolders})";

            StatusChanged?.Invoke(unread > 0
                ? $"Непрочитанных в ящике: {unread}{where}. Показано новых: {found}. Следующая проверка через {IntervalSeconds / 60} мин."
                : $"Непрочитанных нет. Следующая проверка через {IntervalSeconds / 60} мин.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            ErrorOccurred?.Invoke(ex);
            StatusChanged?.Invoke($"Ошибка проверки: {ex.Message}");
        }
        finally
        {
            _isChecking = false;
        }
    }
}