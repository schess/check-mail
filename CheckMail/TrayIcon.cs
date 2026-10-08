using System.Drawing;
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

    public TrayIcon()
    {
        _notifyIcon = new NotifyIcon
        {
            Text = "Проверка почты Exchange",
            Visible = true
        };

        // Иконка — рисуем простую конвертную иконку программно
        _notifyIcon.Icon = CreateMailIcon();

        _notifyIcon.DoubleClick += (_, _) => OpenRequested?.Invoke();

        var menu = new ContextMenuStrip();
        menu.Items.Add("Открыть настройки", null, (_, _) => OpenRequested?.Invoke());
        menu.Items.Add("Проверить сейчас", null, async (_, _) =>
        {
            if (App.Current is CheckMail.App app)
                await app.Polling.RunCheckAsync(app.Settings);
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => ExitRequested?.Invoke());
        _notifyIcon.ContextMenuStrip = menu;
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

    /// <summary>Простая иконка-«конверт» в синем цвете.</summary>
    private static Icon CreateMailIcon()
    {
        using var bmp = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bmp);
        g.Clear(System.Drawing.Color.Transparent);
        using var pen = new Pen(System.Drawing.Color.FromArgb(46, 95, 163), 1.5f);
        using var brush = new SolidBrush(System.Drawing.Color.FromArgb(46, 95, 163));

        // Квадрат «конверта»
        g.DrawRectangle(pen, 1, 3, 13, 10);
        // Линия внизу (как V-образный низ конверта)
        g.DrawLine(pen, 1, 3, 8, 9);
        g.DrawLine(pen, 15, 3, 8, 9);

        var hicon = bmp.GetHicon();
        return Icon.FromHandle(hicon);
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
