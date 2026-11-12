using System.Data.Common;

namespace ApiHost;

/// <summary>Настройки фонового назначения курьеров (секция Assignment).</summary>
public sealed record AssignmentOptions
{
    /// <summary>Включено ли фоновое назначение. Ручное назначение оператором работает независимо от настройки.</summary>
    public bool Enabled { get; init; } = true;
    /// <summary>Интервал опроса доставок, ожидающих курьера, в секундах.</summary>
    public int RetryIntervalSeconds { get; init; } = 15;
    /// <summary>Верхняя граница паузы между попытками при недоступной базе, в секундах.</summary>
    public int MaxBackoffSeconds { get; init; } = 300;
    /// <summary>Сколько доставок назначается за один проход.</summary>
    public int BatchSize { get; init; } = 20;

    public static AssignmentOptions From(IConfiguration cfg)
    {
        var o = cfg.GetSection("Assignment").Get<AssignmentOptions>() ?? new AssignmentOptions();
        return o with
        {
            RetryIntervalSeconds = Math.Clamp(o.RetryIntervalSeconds, 1, 3600),
            MaxBackoffSeconds = Math.Clamp(o.MaxBackoffSeconds, Math.Clamp(o.RetryIntervalSeconds, 1, 3600), 24 * 3600),
            BatchSize = Math.Clamp(o.BatchSize, 1, 500),
        };
    }
}

/// <summary>
/// Повторное автоматическое назначение курьеров. Если при создании заказа или повторе доставки назначить курьера
/// не удалось (нет свободных курьеров или временный сбой БД), доставка остаётся в статусе Created; служба
/// периодически опрашивает PostgreSQL и назначает таким доставкам курьеров тем же алгоритмом балансировки.
/// При ошибках БД пауза между попытками растёт экспоненциально до MaxBackoffSeconds, чтобы не нагружать
/// недоступную базу. Несколько экземпляров API работают одновременно: доставки и курьеры блокируются
/// FOR UPDATE SKIP LOCKED. При остановке приложения текущий проход завершается и служба выходит.
/// </summary>
public sealed class AssignmentRetryService : BackgroundService
{
    private readonly Func<CancellationToken, Store.PendingAssignmentResult> _runOnce;
    private readonly AssignmentOptions _o;
    private readonly ILogger _log;

    public AssignmentRetryService(Store store, IConfiguration cfg, ILogger<AssignmentRetryService> log)
        : this(ct => store.AssignPendingDeliveries(AssignmentOptions.From(cfg).BatchSize, ct), AssignmentOptions.From(cfg), log) { }

    internal AssignmentRetryService(Func<CancellationToken, Store.PendingAssignmentResult> runOnce, AssignmentOptions options, ILogger log)
    {
        _runOnce = runOnce;
        _o = options;
        _log = log;
    }

    /// <summary>Число завершённых проходов (успешных и неудачных) — для диагностики и тестов.</summary>
    public int Iterations => _iterations;
    private int _iterations;

    /// <summary>Пауза после failures подряд неудачных проходов: интервал × 2^(failures-1), не больше MaxBackoffSeconds.</summary>
    public static TimeSpan NextDelay(int failures, AssignmentOptions o)
    {
        if (failures <= 0) return TimeSpan.FromSeconds(o.RetryIntervalSeconds);
        var seconds = o.RetryIntervalSeconds * Math.Pow(2, Math.Min(failures - 1, 20));
        return TimeSpan.FromSeconds(Math.Min(seconds, o.MaxBackoffSeconds));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_o.Enabled)
        {
            _log.LogInformation("Фоновое назначение курьеров отключено (Assignment:Enabled=false)");
            return;
        }
        _log.LogInformation("Фоновое назначение курьеров запущено: интервал {IntervalSeconds} с, пауза при сбоях до {MaxBackoffSeconds} с",
            _o.RetryIntervalSeconds, _o.MaxBackoffSeconds);
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan delay;
            try
            {
                var r = await Task.Run(() => _runOnce(stoppingToken), stoppingToken);
                if (failures > 0) _log.LogInformation("Фоновое назначение курьеров восстановлено после {Failures} неудачных попыток", failures);
                failures = 0;
                if (r.Assigned > 0) _log.LogInformation("Фоновое назначение: назначено доставок {Assigned}", r.Assigned);
                // пачка заполнена целиком — ожидающие доставки, скорее всего, ещё есть: следующий проход без полной паузы
                delay = r.Assigned >= _o.BatchSize && !r.NoFreeCouriers ? TimeSpan.FromMilliseconds(200) : NextDelay(0, _o);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                failures++;
                delay = NextDelay(failures, _o);
                if (ex is DbException or TimeoutException)
                    _log.LogWarning(ex, "Фоновое назначение курьеров: временная ошибка БД (попытка {Failures}), следующая через {DelaySeconds} с",
                        failures, (int)delay.TotalSeconds);
                else
                    _log.LogError(ex, "Фоновое назначение курьеров: непредвиденная ошибка (попытка {Failures}), следующая через {DelaySeconds} с",
                        failures, (int)delay.TotalSeconds);
            }
            Interlocked.Increment(ref _iterations);
            // небольшой разброс, чтобы экземпляры API не обращались к базе синхронно
            var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, (int)Math.Min(1000, delay.TotalMilliseconds / 10 + 1)));
            try { await Task.Delay(delay + jitter, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
        _log.LogInformation("Фоновое назначение курьеров остановлено");
    }
}
