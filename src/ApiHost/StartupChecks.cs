using Npgsql;

namespace ApiHost;

/// <summary>
/// Проверка конфигурации при запуске. В окружении Development допускаются учебные значения
/// (демо-данные, пароль БД из appsettings.Development.json). В остальных окружениях (Production, Staging и т. п.)
/// сервер не стартует с небезопасными параметрами по умолчанию: строка подключения и пароли берутся
/// из переменных окружения или другого внешнего хранилища, демо-пользователи запрещены.
/// </summary>
public static class StartupChecks
{
    public const int MinDbPasswordLength = 12;

    /// <summary>Пароли, которые встречаются в примерах, compose-файлах и документации, — в рабочем окружении недопустимы.</summary>
    private static readonly HashSet<string> KnownWeak = new(StringComparer.OrdinalIgnoreCase)
    {
        "delivery", "postgres", "password", "operator", "admin", "root", "123456", "12345678", "qwerty", "changeme", "change-me", "secret",
    };

    /// <summary>Создавать ли демо-данные, если Seed:Demo не задан явно: только при локальной разработке.</summary>
    public static bool DemoByDefault(IHostEnvironment env) => env.IsDevelopment();

    /// <summary>Возвращает список проблем конфигурации; пустой список — можно запускаться.</summary>
    public static List<string> Validate(IConfiguration cfg, IHostEnvironment env)
    {
        var problems = new List<string>();
        var cs = cfg.GetConnectionString("Default");
        if (string.IsNullOrWhiteSpace(cs))
        {
            problems.Add("Не задана строка подключения ConnectionStrings:Default (переменная окружения ConnectionStrings__Default).");
            return problems;
        }

        NpgsqlConnectionStringBuilder b;
        try { b = new NpgsqlConnectionStringBuilder(cs); }
        catch (ArgumentException ex)
        {
            problems.Add("Некорректная строка подключения ConnectionStrings:Default: " + ex.Message);
            return problems;
        }

        if (env.IsDevelopment()) return problems; // учебный запуск: стандартные пароли и демо-данные допустимы

        if (string.IsNullOrEmpty(b.Password))
            problems.Add("В строке подключения к PostgreSQL не задан пароль.");
        else if (IsWeak(b.Password, MinDbPasswordLength, b.Username))
            problems.Add($"Пароль PostgreSQL слишком простой или стандартный: нужен уникальный пароль не короче {MinDbPasswordLength} символов.");

        if (cfg.GetValue("Seed:Demo", false))
            problems.Add($"Seed:Demo=true запрещён в окружении {env.EnvironmentName}: демо-пользователи имеют пароли, равные логинам.");

        var op = cfg["Seed:OperatorPassword"];
        if (!string.IsNullOrEmpty(op) && IsWeak(op, Store.MinPasswordLength, "operator"))
            problems.Add($"Seed:OperatorPassword слишком простой: нужен пароль не короче {Store.MinPasswordLength} символов, не совпадающий с логином и стандартными значениями.");

        return problems;
    }

    /// <summary>Бросает исключение с перечнем всех проблем, чтобы администратор исправил их за один раз.</summary>
    public static void EnsureValid(IConfiguration cfg, IHostEnvironment env)
    {
        var problems = Validate(cfg, env);
        if (problems.Count > 0)
            throw new InvalidOperationException($"Небезопасная или неполная конфигурация ({env.EnvironmentName}):" + Environment.NewLine
                + string.Join(Environment.NewLine, problems.Select(p => " - " + p)));
    }

    internal static bool IsWeak(string password, int minLength, string? login = null) =>
        password.Length < minLength || KnownWeak.Contains(password)
        || (login is not null && string.Equals(password, login, StringComparison.OrdinalIgnoreCase))
        || password.Distinct().Count() < 4;
}
