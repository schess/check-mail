using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using MessageBox = System.Windows.MessageBox;

namespace CheckMail;

public partial class NotificationWindow : Window
{
    private readonly System.Windows.Threading.DispatcherTimer _autoCloseTimer;
    public event Action? ClosedByUser;

    public NotificationWindow(NewMailInfo mail)
    {
        InitializeComponent();

        FolderText.Text = mail.FolderName;
        SubjectText.Text = mail.Subject;
        SenderText.Text = string.IsNullOrEmpty(mail.Sender) ? "" : $"От: {mail.Sender}";

        // Автозакрытие через 15 секунд, если пользователь не нажал кнопку
        _autoCloseTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(15)
        };
        _autoCloseTimer.Tick += (_, _) => Close();
        _autoCloseTimer.Start();

        // Анимация появления (плавный fade-in)
        Loaded += (_, _) =>
        {
            Opacity = 0;
            var anim = new System.Windows.Media.Animation.DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(250)
            };
            BeginAnimation(OpacityProperty, anim);
        };

        MouseEnter += (_, _) => _autoCloseTimer.Stop();
        MouseLeave += (_, _) => _autoCloseTimer.Start();
    }

    /// <summary>Размещаем окно в правом верхнем углу рабочей области.</summary>
    public void PositionTopRight()
    {
        var wa = SystemParameters.WorkArea;
        Left = wa.Right - ActualWidth - 12;
        Top = wa.Top + 12;
    }

    private void OpenOutlook_Click(object sender, RoutedEventArgs e)
    {
        OpenOutlook();
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    public static void OpenOutlook()
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "outlook.exe",
                UseShellExecute = true
            };
            Process.Start(startInfo);
        }
        catch
        {
            // Если outlook.exe не найден в PATH — пробуем стандартный путь установки
            string[] candidates =
            {
                @"C:\Program Files\Microsoft Office\root\Office16\OUTLOOK.EXE",
                @"C:\Program Files (x86)\Microsoft Office\root\Office16\OUTLOOK.EXE",
                @"C:\Program Files\Microsoft Office\Office16\OUTLOOK.EXE",
                @"C:\Program Files (x86)\Microsoft Office\Office16\OUTLOOK.EXE"
            };
            foreach (var path in candidates)
            {
                if (System.IO.File.Exists(path))
                {
                    Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
                    return;
                }
            }
            MessageBox.Show("Не удалось найти Outlook. Убедитесь, что Microsoft Outlook установлен.",
                            "Открыть Outlook", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _autoCloseTimer.Stop();
        ClosedByUser?.Invoke();
        base.OnClosed(e);
    }
}
