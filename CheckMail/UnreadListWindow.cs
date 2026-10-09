using System.Collections.ObjectModel;
using System.Windows;

namespace CheckMail;

/// <summary>
/// Постоянное окно со списком непрочитанных писем по всем папкам.
/// В отличие от всплывающих уведомлений, само не закрывается по таймауту —
/// пользователь может спокойно посмотреть список.
/// </summary>
public partial class UnreadListWindow : Window
{
    /// <summary>Коллекция для привязки к ListView.</summary>
    public ObservableCollection<NewMailInfo> Items { get; } = new();

    /// <summary>Вызывается по кнопке «Обновить» — запросить свежие данные.</summary>
    public event Action? RefreshRequested;

    public UnreadListWindow()
    {
        InitializeComponent();
        UnreadList.ItemsSource = Items;
    }

    /// <summary>Заменяет содержимое списка свежими данными.</summary>
    public void UpdateUnread(IEnumerable<NewMailInfo> unread)
    {
        Items.Clear();
        foreach (var m in unread)
            Items.Add(m);

        var count = Items.Count;
        HeaderText.Text = count > 0
            ? $"Непрочитанные письма: {count}"
            : "Непрочитанных писем нет";

        // Сводка по папкам
        var byFolder = unread
            .GroupBy(m => m.FolderName)
            .Select(g => $"{g.Key}: {g.Count()}");
        SummaryText.Text = byFolder.Any()
            ? string.Join("   •   ", byFolder)
            : "Все письма прочитаны.";
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        RefreshRequested?.Invoke();
    }

    private void Outlook_Click(object sender, RoutedEventArgs e)
    {
        NotificationWindow.OpenOutlook();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}