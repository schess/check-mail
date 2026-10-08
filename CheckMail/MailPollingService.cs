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
    private bool _isRunning;

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
            if (_isRunning) return; // не запускаем повторно, пока идёт проверка
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
        if (_timer != null)
        {
            _timer.Stop();
            _timer = null;
        }
        StatusChanged?.Invoke("Проверка остановлена");
    }

    public async Task RunCheckAsync(Settings settings)
    {
        if (_isRunning && _timer != null) _timer.Stop(); // приостановить таймер на время проверки

        try
        {
            _lastRun = DateTime.Now;
            StatusChanged?.Invoke($"Проверка почты... ({_lastRun:HH:mm:ss})");
            await _checker.CheckForNewMailAsync(settings);
            StatusChanged?.Invoke($"Проверка завершена. Следующая через {IntervalSeconds / 60} мин.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            ErrorOccurred?.Invoke(ex);
            StatusChanged?.Invoke($"Ошибка проверки: {ex.Message}");
        }
        finally
        {
            if (_isRunning && _timer != null) _timer.Start();
        }
    }
}
