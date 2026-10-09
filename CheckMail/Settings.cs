using System.IO;
using System.Text.Json;

namespace CheckMail;

/// <summary>Протокол доступа к почтовому серверу.</summary>
public enum MailProtocol
{
    Imap,
    Ews
}

/// <summary>Настройки приложения, хранятся в JSON-файле рядом с exe.</summary>
public class Settings
{
    public string EwsUrl { get; set; } = string.Empty;   // например https://mail.cit.gov.ru/EWS/Exchange.asmx
    public string ImapServer { get; set; } = string.Empty; // например mail.cit.gov.ru
    public int ImapPort { get; set; } = 993;               // IMAP over SSL
    public bool ImapUseSsl { get; set; } = true;           // использовать SSL/TLS для IMAP
    // В закрытой сети CRL/OCSP-серверы часто недоступны, и проверка отзыва
    // сертификата падает с ошибкой SSL. По умолчанию отключаем её.
    public bool ImapSkipRevocationCheck { get; set; } = true;
    // Папки, которые не нужно проверять (через запятую). По умолчанию
    // исключается служебная папка «Conflicts» — технические дубли писем.
    public string ExcludedFolders { get; set; } = "Conflicts";
    public string Email { get; set; } = string.Empty;      // почтовый ящик пользователя
    public string Username { get; set; } = string.Empty;   // логин для аутентификации
    public string Password { get; set; } = string.Empty;   // пароль для аутентификации
    public string Domain { get; set; } = string.Empty;     // домен (опционально)
    public bool UseDefaultCredentials { get; set; } = false; // использовать учётку текущего пользователя Windows (только EWS)
    public MailProtocol Protocol { get; set; } = MailProtocol.Imap; // протокол проверки почты
    public int CheckIntervalSeconds { get; set; } = 300;   // по умолчанию каждые 5 минут
    public bool CheckAllFolders { get; set; } = true;      // проверять все папки ящика (EWS)
    public bool RunAtStartup { get; set; } = false;        // автозапуск вместе с Windows

    private static string ConfigPath =>
        Path.Combine(AppContext.BaseDirectory, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var s = JsonSerializer.Deserialize<Settings>(json);
                if (s != null) return s;
            }
        }
        catch
        {
            // при ошибке чтения возвращаем настройки по умолчанию
        }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigPath, json);
        }
        catch
        {
            // не критично — молча игнорируем
        }
    }
}
