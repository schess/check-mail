using System.Collections.Concurrent;
using System.Threading.Tasks;
using MailKit;
using MailKit.Net.Imap;
using Microsoft.Exchange.WebServices.Data;
using Task = System.Threading.Tasks.Task;

namespace CheckMail;

/// <summary>
/// Восстанавливает кириллицу в тексте, который пришёл в неверной кодировке.
/// Некоторые старые письма хранят тему/отправителя как latin1-байты кириллицы
/// (вместо UTF-8) — из-за этого в уведомлении видны «кракозябры».
/// Возвращает исходную строку, если исправление не требуется.
/// </summary>
internal static class TextFixer
{
    private static readonly string[] FallbackEncodings = { "koi8-r", "windows-1251", "utf-8" };

    static TextFixer()
    {
        try
        {
            // Нужны устаревшие кодовые страницы (KOI8-R, Windows-1251).
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        }
        catch
        {
            // если не зарегистрировались — останемся только с UTF-8
        }
    }

    /// <summary>Доля кириллических букв в строке (0..1).</summary>
    private static double CyrillicRatio(string s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        int letters = 0, cyr = 0;
        foreach (var ch in s)
        {
            if (!char.IsLetter(ch)) continue;
            letters++;
            if (ch >= 'Ѐ' && ch <= 'ӿ') cyr++;
        }
        return letters == 0 ? 0 : (double)cyr / letters;
    }

    internal static string FixMojibake(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;

        // Текст уже содержит нормальную кириллицу — ничего не трогаем.
        if (CyrillicRatio(value) > 0.2) return value;

        var bytes = System.Text.Encoding.Latin1.GetBytes(value);

        string best = value;
        double bestScore = 0;

        foreach (var name in FallbackEncodings)
        {
            string decoded;
            try
            {
                decoded = System.Text.Encoding.GetEncoding(name).GetString(bytes);
            }
            catch
            {
                continue;
            }

            var score = CyrillicRatio(decoded);

            // Бонус за частые русские буквы (у «мусорной» расшифровки
            // они встречаются заметно реже) — helps отличить KOI8-R от CP1251.
            int common = 0;
            foreach (var ch in decoded)
            {
                if ("абвгдеёжзийклмнопрстуфхцчшщъыьэюяАБВГДЕЁЖЗИЙКЛМНОПРСТУФХЦЧШЩЪЫЬЭЮЯ".IndexOf(ch) >= 0)
                    common++;
            }
            score += Math.Min(common, 40) / 100.0;

            if (score > bestScore)
            {
                bestScore = score;
                best = decoded;
            }

            if (score > 0.9) break;   // нашли уверенный вариант — можно остановиться
        }

        // Если ни одна кодировка не дала внятной кириллицы — оставляем как есть.
        return CyrillicRatio(best) > 0.5 ? best : value;
    }
}

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

    /// <summary>Момент последней успешной проверки (для информации/диагностики).</summary>
    private DateTime _lastArrivalBaseline = DateTime.MinValue;

    /// <summary>Всего непрочитанных писем в ящике (по всем папкам) на последней проверке.</summary>
    public int LastUnreadTotal { get; private set; }

    /// <summary>Список папок с непрочитанными письмами (например "Входящие (2), Руководство (1)").</summary>
    public string LastUnreadFolders { get; private set; } = string.Empty;

    /// <summary>Все непрочитанные письма по всем папкам на последней проверке (свежий список).</summary>
    public List<NewMailInfo> LastUnread { get; private set; } = new();

    /// <summary>
    /// Список непрочитанных изменился — можно обновить окно/счётчик в трее.
    /// Событие вызывается из фонового потока.
    /// </summary>
    public event Action<List<NewMailInfo>>? UnreadListChanged;

    public event Action<NewMailInfo>? NewMailFound;

    /// <summary>Подключение к серверу Exchange через EWS.</summary>
    public ExchangeService CreateService(Settings s)
    {
        var service = new ExchangeService(ExchangeVersion.Exchange2013_SP1);

        if (s.UseDefaultCredentials)
        {
            // Windows-аутентификация (Kerberos/NTLM) — как в Outlook.
            // PreAuthenticate=true заставляет EWS отправлять заголовок
            // Authorization заранее через CredentialCache.DefaultCredentials,
            // благодаря чему Negotiate/Kerberos работает надёжнее.
            service.UseDefaultCredentials = true;
            service.PreAuthenticate = true;
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

    /// <summary>
    /// Проверка почты: выбирает реализацию по настроенному протоколу (IMAP или EWS).
    /// Вся работа выполняется в фоновом потоке через Task.Run, чтобы синхронные
    /// сетевые вызовы не блокировали поток UI (иначе приложение «виснет»).
    /// </summary>
    public Task<int> CheckForNewMailAsync(Settings settings)
    {
        return Task.Run(() =>
        {
            return settings.Protocol == MailProtocol.Imap
                ? CheckForNewMailImap(settings)
                : CheckForNewMail(settings);
        });
    }

    /// <summary>
    /// Разбирает список исключаемых папок из настроек (через запятую или точку с
    /// запятой) в множество имён. По умолчанию исключается служебная папка
    /// «Conflicts» — это технические дубли писем от синхронизации, а не реальная
    /// почта, и о них не нужно сообщать.
    /// </summary>
    private static HashSet<string> ParseExcludedFolders(string? value)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(value))
            value = "Conflicts";

        foreach (var part in value.Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var name = part.Trim();
            if (name.Length > 0) set.Add(name);
        }

        if (set.Count == 0) set.Add("Conflicts");
        return set;
    }

    /// <summary>
    /// Попадает ли папка под исключение. Сравниваем как имя папки, так и любой
    /// сегмент полного пути — чтобы исключались и вложенные вроде
    /// «Sync Issues/Conflicts».
    /// </summary>
    private static bool IsExcludedFolder(IMailFolder folder, HashSet<string> excluded)
    {
        if (excluded.Count == 0) return false;

        if (!string.IsNullOrEmpty(folder.Name) && excluded.Contains(folder.Name))
            return true;

        var full = folder.FullName;
        if (string.IsNullOrEmpty(full)) return false;

        foreach (var segment in full.Split('/', '\\'))
        {
            var name = segment.Trim();
            if (name.Length > 0 && excluded.Contains(name))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Подключается к IMAP и перечисляет папки ящика (рекурсивно, включая
    /// вложенные), пропуская исключённые (вместе с их подпапками).
    /// </summary>
    private List<IMailFolder> ListAllFolders(ImapClient client, HashSet<string> excluded)
    {
        var result = new List<IMailFolder>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Walk(IMailFolder folder, int depth)
        {
            if (folder == null || depth > 10) return;
            var path = folder.FullName ?? folder.Name;
            if (!string.IsNullOrEmpty(path) && !seenPaths.Add(path)) return;

            // Исключённая папка: не добавляем её и не спускаемся в подпапки.
            if (IsExcludedFolder(folder, excluded)) return;

            result.Add(folder);

            IList<IMailFolder> children;
            try
            {
                children = folder.GetSubfolders(false, CancellationToken.None);
            }
            catch
            {
                return; // нет прав на подпапки — просто идём дальше
            }

            foreach (var child in children)
                Walk(child, depth + 1);
        }

        foreach (var ns in client.PersonalNamespaces)
        {
            try
            {
                Walk(client.GetFolder(ns), 0);
            }
            catch
            {
                // namespace недоступен — пропускаем
            }
        }

        return result;
    }

    /// <summary>
    /// Проверка почты через IMAP (MailKit): обходит ВСЕ папки ящика, в каждой
    /// находит непрочитанные письма (\\Seen не установлен) и сообщает о них.
    /// Выполняется в фоновом потоке.
    /// </summary>
    private int CheckForNewMailImap(Settings settings)
    {
        using var client = new MailKit.Net.Imap.ImapClient();
        if (settings.ImapSkipRevocationCheck) client.CheckCertificateRevocation = false;

        var host = string.IsNullOrEmpty(settings.ImapServer) ? "mail.cit.gov.ru" : settings.ImapServer;
        var port = settings.ImapPort > 0 ? settings.ImapPort : 993;
        var login = string.IsNullOrEmpty(settings.Username) ? settings.Email : settings.Username;

        if (string.IsNullOrEmpty(login) || string.IsNullOrEmpty(settings.Password))
            throw new InvalidOperationException("Не задан логин или пароль для проверки почты.");

        client.Connect(host, port, settings.ImapUseSsl
            ? MailKit.Security.SecureSocketOptions.SslOnConnect
            : MailKit.Security.SecureSocketOptions.StartTlsWhenAvailable);
        client.Authenticate(login, settings.Password);

        var excluded = ParseExcludedFolders(settings.ExcludedFolders);
        var folders = ListAllFolders(client, excluded);

        var newItems = new List<NewMailInfo>();
        var allUnread = new List<NewMailInfo>();
        var unreadFolders = new List<string>();
        int unreadTotal = 0;
        var request = new MailKit.FetchRequest(MailKit.MessageSummaryItems.UniqueId |
                                                MailKit.MessageSummaryItems.Envelope);

        foreach (var folder in folders)
        {
            try
            {
                if (!folder.CanOpen) continue;
                folder.Open(MailKit.FolderAccess.ReadOnly, CancellationToken.None);

                // ВАЖНО: после Open() счётчики Count/Unread по умолчанию НЕ
                // заполнены (MailKit не шлёт STATUS автоматически) и равны 0.
                // Запрашиваем STATUS явно — иначе счётчик непрочитанных
                // всегда был бы 0. Сам поиск NotSeen ниже всё равно остаётся
                // главным источником истины.
                folder.Status(MailKit.StatusItems.Unread | MailKit.StatusItems.Count,
                              CancellationToken.None);

                // Учитываем общее число непрочитанных в ящике — его показываем
                // в статусе, даже если письма уже сообщались ранее.
                if (folder.Unread > 0)
                {
                    unreadTotal += folder.Unread;
                    unreadFolders.Add($"{folder.Name} ({folder.Unread})");
                }

                // Поиск непрочитанных — основной механизм определения новых.
                var uids = folder.Search(MailKit.Search.SearchQuery.NotSeen, CancellationToken.None);
                if (uids == null || uids.Count == 0) continue;

                foreach (var item in folder.Fetch(uids, request))
                {
                    if (item?.Envelope == null) continue;

                    var messageId = item.Envelope.MessageId;
                    if (string.IsNullOrEmpty(messageId))
                    {
                        // без MessageId используем папку+дату как уникальный ключ
                        messageId = $"{folder.FullName}:{item.Envelope.Date?.DateTime:O}";
                    }
                    var from = item.Envelope.From?.Mailboxes?.FirstOrDefault();
                    var subject = TextFixer.FixMojibake(item.Envelope.Subject ?? "");
                    var senderRaw = !string.IsNullOrEmpty(from?.Name) ? from.Name : (from?.Address ?? "");
                    var info = new NewMailInfo
                    {
                        MessageId = messageId,
                        FolderName = string.IsNullOrEmpty(folder.Name) ? "Входящие" : folder.Name,
                        Subject = string.IsNullOrEmpty(subject) ? "(без темы)" : subject,
                        Sender = TextFixer.FixMojibake(senderRaw),
                        ReceivedAt = item.Envelope.Date?.LocalDateTime ?? DateTime.Now
                    };

                    // Полный список непрочитанных — для окна со списком и трея.
                    allUnread.Add(info);

                    // В уведомления попадают только те, о чём ещё не сообщали.
                    if (_seen.Add(messageId))
                    {
                        newItems.Add(info);
                    }
                }
            }
            catch
            {
                // Служебные папки (Спам, Удалённые, синхронизация) могут быть
                // недоступны или защищены — молча пропускаем.
            }
            finally
            {
                try { if (folder.IsOpen) folder.Close(false, CancellationToken.None); } catch { }
            }
        }

        _lastArrivalBaseline = DateTime.Now;
        TrimSeenCache();
        client.Disconnect(true);

        // Полный список непрочитанных по всем папкам — для постоянного окна
        // со списком и счётчика в трее (он не исчезает, в отличие от всплывающих
        // уведомлений, которые закрываются по таймауту).
        LastUnread = allUnread
            .OrderByDescending(m => m.ReceivedAt)
            .ToList();
        LastUnreadTotal = LastUnread.Count > 0 ? LastUnread.Count : unreadTotal;
        LastUnreadFolders = string.Join(", ", unreadFolders);
        UnreadListChanged?.Invoke(LastUnread);

        // Всплывающие окна — только для новых писем (их ещё не показывали).
        const int MaxIndividualNotifications = 3;

        int shown = 0;
        foreach (var n in newItems)
        {
            if (shown >= MaxIndividualNotifications) break;
            shown++;
            NewMailFound?.Invoke(n);
        }

        int rest = newItems.Count - shown;
        if (rest > 0)
        {
            NewMailFound?.Invoke(new NewMailInfo
            {
                MessageId = $"summary:{DateTime.Now:O}",
                FolderName = "Сводка",
                Subject = $"Новых непрочитанных писем ещё: {rest}",
                Sender = $"Всего непрочитанных в ящике: {LastUnreadTotal}. Папки: {LastUnreadFolders}",
                ReceivedAt = DateTime.Now
            });
        }

        return newItems.Count;
    }

    /// <summary>
    /// Проверяет подключение к IMAP-серверу: соединяется, аутентифицируется и
    /// открывает папку «Входящие» (ReadOnly). Бросает исключение при неудаче.
    /// </summary>
    public void TestImapConnection(Settings settings)
    {
        var host = string.IsNullOrEmpty(settings.ImapServer) ? "mail.cit.gov.ru" : settings.ImapServer;
        var port = settings.ImapPort > 0 ? settings.ImapPort : 993;
        var login = string.IsNullOrEmpty(settings.Username) ? settings.Email : settings.Username;

        if (string.IsNullOrEmpty(login) || string.IsNullOrEmpty(settings.Password))
            throw new InvalidOperationException("Не задан логин или пароль для проверки почты.");

        using var client = new MailKit.Net.Imap.ImapClient();
        if (settings.ImapSkipRevocationCheck) client.CheckCertificateRevocation = false;
        client.Connect(host, port, settings.ImapUseSsl
            ? MailKit.Security.SecureSocketOptions.SslOnConnect
            : MailKit.Security.SecureSocketOptions.StartTlsWhenAvailable);
        client.Authenticate(login, settings.Password);
        client.Inbox.Open(MailKit.FolderAccess.ReadOnly);
        client.Disconnect(true);
    }

    /// <summary>Синхронное ядро проверки (EWS) — выполняется в фоновом потоке.</summary>
    private int CheckForNewMail(Settings settings)
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

            return newItems.Count;
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
