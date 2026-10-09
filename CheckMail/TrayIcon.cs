using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace CheckMail;

/// <summary>
/// Управляет иконкой в системном трее и контекстным меню.
/// </summary>
public class TrayIcon : IDisposable
{
    private NotifyIcon _notifyIcon;

    public event Action? OpenRequested;
    public event Action? ExitRequested;

    /// <summary>Показать окно со списком непрочитанных писем.</summary>
    public event Action? OpenUnreadRequested;

    private ToolStripMenuItem? _unreadMenuItem;

    public TrayIcon()
    {
        _notifyIcon = new NotifyIcon
        {
            Text = "Проверка почты Exchange",
            Visible = true
        };

        // Иконка — загружаем из файла app.ico (тот же, что у окна и .exe)
        _notifyIcon.Icon = LoadAppIcon();

        _notifyIcon.DoubleClick += (_, _) => OpenRequested?.Invoke();

        var menu = new ContextMenuStrip();
        menu.Items.Add("Открыть настройки", null, (_, _) => OpenRequested?.Invoke());

        _unreadMenuItem = new ToolStripMenuItem("Непрочитанные: нет данных");
        _unreadMenuItem.Enabled = false;
        _unreadMenuItem.Click += (_, _) => OpenUnreadRequested?.Invoke();
        menu.Items.Add(_unreadMenuItem);

        menu.Items.Add("Проверить сейчас", null, async (_, _) =>
        {
            if (App.Current is CheckMail.App app)
                await app.Polling.RunCheckAsync(app.Settings);
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => ExitRequested?.Invoke());
        _notifyIcon.ContextMenuStrip = menu;
    }

    /// <summary>
    /// Обновляет счётчик непрочитанных: он виден в подсказке (tooltip) иконки
    /// и в меню трея — то есть не пропадает вместе с всплывающими окнами.
    /// </summary>
    public void SetUnreadCount(int count)
    {
        var text = count > 0
            ? $"Проверка почты Exchange — непрочитанных: {count}"
            : "Проверка почты Exchange — непрочитанных нет";

        try
        {
            // Подсказка NotifyIcon ограничена 63 символами
            _notifyIcon.Text = text.Length > 63 ? text[..63] : text;
        }
        catch
        {
            // игнорируем (некоторые версии Windows отвергают длинный текст)
        }

        if (_unreadMenuItem != null)
        {
            _unreadMenuItem.Text = count > 0
                ? $"Непрочитанные: {count}"
                : "Непрочитанных нет";
            _unreadMenuItem.Enabled = count > 0;
        }
    }

    public void ShowBalloon(string title, string message)
    {
        try
        {
            _notifyIcon.ShowBalloonTip(5000, title, message, ToolTipIcon.Info);
        }
        catch
        {
            // игнорируем ошибки показа баллона
        }
    }

    public void Show() => _notifyIcon.Visible = true;
    public void Hide() => _notifyIcon.Visible = false;

    /// <summary>
    /// Загружает иконку приложения из файла app.ico.
    /// Раньше здесь рисовался Bitmap и брался Icon.FromHandle(hicon) — такой
    /// дескриптор не принадлежал объекту Icon и не освобождался, из-за чего
    /// иконка в трее часто не отображалась. Теперь читаем файл, копируя его
    /// в память, чтобы файл не был заблокирован на диске.
    /// </summary>
    private static Icon LoadAppIcon()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "app.ico");
        if (File.Exists(path))
        {
            // Копируем байты и пересоздаём Icon — так файл не держится открытым.
            using var fs = File.OpenRead(path);
            using var ms = new MemoryStream();
            fs.CopyTo(ms);
            ms.Position = 0;
            return new Icon(ms);
        }

        // Файла нет — рисуем простой конверт программно (без GetHicon, с ручным
        // DestroyIcon, чтобы не течь дескрипторами).
        return CreateFallbackIcon();
    }

    /// <summary>Резервная иконка «конверт», если app.ico отсутствует.</summary>
    private static Icon CreateFallbackIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);

            var body = new Rectangle(2, 8, 28, 18);
            using (var brush = new System.Drawing.Drawing2D.LinearGradientBrush(body,
                       System.Drawing.Color.FromArgb(64, 122, 196),
                       System.Drawing.Color.FromArgb(28, 62, 110), 90f))
            {
                g.FillRectangle(brush, body);
            }
            using (var pen = new Pen(System.Drawing.Color.FromArgb(255, 255, 255), 2f))
            {
                g.DrawLine(pen, 3, 9, 16, 19);
                g.DrawLine(pen, 29, 9, 16, 19);
                g.DrawLine(pen, 3, 25, 12, 16);
                g.DrawLine(pen, 29, 25, 20, 16);
            }
        }

        // Клонируем через Bitmap -> Icon и сразу уничтожаем исходный HICON,
        // чтобы он не утекал.
        IntPtr hIcon = bmp.GetHicon();
        try
        {
            using var tmp = Icon.FromHandle(hIcon);
            return (Icon)tmp.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
