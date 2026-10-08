using System.Collections.Concurrent;
using System.Threading.Tasks;
using Microsoft.Exchange.WebServices.Data;
using Task = System.Threading.Tasks.Task;

namespace CheckMail;

/// <summary>Информация о новом письме.</summary>
public class NewMailInfo
{
    public string FolderName { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Sender { get; set; } = string.Empty;
    public DateTime ReceivedAt { get; set; }
    public string MessageId { get; set; } = string.Empty;
}

/// <summary>
/// Проверяет почтовый ящик Exchange через EWS.
/// Хранит набор уже виденных MessageId, чтобы находить только новые письма.
/// </summary>
public class MailChecker
{
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _lock = new(1, 1);

    public event Action<NewMailInfo>? NewMailFound;

    /// <summary>Подключение к серверу Exchange через EWS.</summary>
    public ExchangeService CreateService(Settings s)
    {
        var service = new ExchangeService(ExchangeVersion.Exchange2013_SP1);

        if (s.UseDefaultCredentials)
        {
            // Windows-аутентификация (Kerberos/NTLM) — как в Outlook
            service.UseDefaultCredentials = true;
        }
        else
        {
            // Явная аутентификация логин/пароль
            var username = string.IsNullOrEmpty(s.Username) ? s.Email : s.Username;
            if (!string.IsNullOrEmpty(s.Domain))
            {
                service.Credentials = new WebCredentials(username, s.Password, s.Domain);
            }
            else
            {
                service.Credentials = new WebCredentials(username, s.Password);
            }
        }

        // Используем указанный EWS URL напрямую
        if (!string.IsNullOrEmpty(s.EwsUrl))
        {
            service.Url = new Uri(s.EwsUrl);
        }
        else
        {
            // Если URL не указан — используем стандартный путь
            service.Url = new Uri($"https://mail.cit.gov.ru/EWS/Exchange.asmx");
        }

        service.Timeout = 60000;
        return service;
    }

    /// <summary>
    /// Диагностика: делает прямой HTTP-запрос к EWS-URL и возвращает ответ сервера.
    /// Помогает понять, что именно возвращает сервер (XML, HTML, ошибку).
    /// </summary>
    public static async Task<string> DiagnoseEwsUrlAsync(string ewsUrl)
    {
        if (string.IsNullOrEmpty(ewsUrl))
            return "URL не указан.";

        try
        {
            using var client = new System.Net.Http.HttpClient();
            client.Timeout = TimeSpan.FromSeconds(30);

            // Добавляем заголовки, которые ожидает EWS
            client.DefaultRequestHeaders.Add("User-Agent", "CheckMail/1.0");
            client.DefaultRequestHeaders.Add("Accept", "text/xml");

            var response = await client.GetAsync(ewsUrl);
            var content = await response.Content.ReadAsStringAsync();

            var result = new System.Text.StringBuilder();
            result.AppendLine($"URL: {ewsUrl}");
            result.AppendLine($"Статус: {(int)response.StatusCode} {response.StatusCode}");
            result.AppendLine($"Content-Type: {response.Content.Headers.ContentType}");
            result.AppendLine($"Длина ответа: {content.Length} символов");
            result.AppendLine();
            result.AppendLine("Первые 2000 символов ответа:");
            result.AppendLine(content.Length > 2000 ? content.Substring(0, 2000) : content);

            return result.ToString();
        }
        catch (Exception ex)
        {
            return $"Ошибка при запросе к {ewsUrl}:\n{ex}";
        }
    }

    /// <summary>Полное сканирование всех папок в поисках новых писем.</summary>
    public async Task CheckForNewMailAsync(Settings settings)
    {
        try
        {
            var service = CreateService(settings);
            var newItems = new List<NewMailInfo>();
            // Список свойств, которые нам нужны у каждого письма
            var props = new PropertySet(BasePropertySet.IdOnly,
                ItemSchema.Subject,
                ItemSchema.DateTimeReceived,
                EmailMessageSchema.InternetMessageId,
                EmailMessageSchema.Sender,
                EmailMessageSchema.IsRead);

            // Собираем все папки почтового ящика (рекурсивно, включая вложенные)
            var folderRoot = Folder.Bind(service, WellKnownFolderName.Root);
            var allFolders = new List<Folder> { folderRoot };

            if (settings.CheckAllFolders)
            {
                CollectSubfolders(folderRoot, allFolders);
            }

            // Для каждой папки ищем письма (только непрочитанные)
            const int pageSize = 200;
            foreach (var folder in allFolders)
            {
                var view = new ItemView(pageSize)
                {
                    PropertySet = props,
                    Traversal = ItemTraversal.Shallow
                };

                // Новыми считаем непрочитанные письма (IsRead = false)
                var filter = new SearchFilter.IsEqualTo(EmailMessageSchema.IsRead, false);

                FindItemsResults<Item> results;
                try
                {
                    results = service.FindItems(folder.Id, filter, view);
                }
                catch
                {
                    // Папку не удалось прочитать — пропускаем (нет прав на служебные папки)
                    continue;
                }

                foreach (var item in results.Items)
                {
                    // Нас интересуют только письма
                    if (item is not EmailMessage message) continue;

                    var messageId = GetMessageId(message);
                    if (string.IsNullOrEmpty(messageId)) continue;

                    // Пропускаем, если уже видели
                    if (_seen.Contains(messageId)) continue;
                    _seen.Add(messageId);

                    newItems.Add(new NewMailInfo
                    {
                        MessageId = messageId,
                        FolderName = folder.DisplayName,
                        Subject = message.Subject ?? "(без темы)",
                        Sender = GetSender(message),
                        ReceivedAt = message.DateTimeReceived
                    });

                    if (newItems.Count >= 50) break; // не спамить уведомлениями за один проход
                }

                if (newItems.Count >= 50) break;
            }

            // Ограничиваем кэш виденных, чтобы не рос бесконечно
            TrimSeenCache();

            // Сообщаем о найденных письмах
            foreach (var n in newItems)
            {
                NewMailFound?.Invoke(n);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Mail check failed: {ex}");
            throw; // пробрасываем, чтобы таймер мог показать ошибку пользователю
        }
    }

    /// <summary>Рекурсивный сбор всех подпапок.</summary>
    private static void CollectSubfolders(Folder parent, List<Folder> all)
    {
        try
        {
            var view = new FolderView(1000) { Traversal = FolderTraversal.Deep };
            var folders = parent.FindFolders(view);
            foreach (var f in folders)
            {
                all.Add(f);
                CollectSubfolders(f, all);
            }
        }
        catch
        {
            // доступ к вложенным папкам может быть ограничен — пропускаем
        }
    }

    private static string GetMessageId(EmailMessage item)
    {
        try
        {
            var v = item.InternetMessageId;
            if (!string.IsNullOrEmpty(v)) return v;
        }
        catch { }

        // Запасной вариант — идентификатор элемента Exchange (нестабилен, но лучше чем ничего)
        return item.Id?.UniqueId ?? Guid.NewGuid().ToString();
    }

    private static string GetSender(EmailMessage item)
    {
        try
        {
            if (item.Sender is EmailAddress addr && !string.IsNullOrEmpty(addr.Name))
                return addr.Name;
            return item.Sender?.Address ?? "";
        }
        catch
        {
            return "";
        }
    }

    private void TrimSeenCache()
    {
        // Держим не более 5000 записей, чтобы память не росла бесконечно
        if (_seen.Count > 5000)
        {
            var excess = _seen.Count - 4000;
            _seen.RemoveWhere(_ => excess-- > 0);
        }
    }
}

internal static class HashExtensions
{
    public static void AddRange<T>(this HashSet<T> set, IEnumerable<T> items)
    {
        foreach (var i in items) set.Add(i);
    }
}
