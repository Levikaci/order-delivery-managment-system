using System.Data.Common;
using System.Net;
using ApiHost;
using Contracts;
using DeliveryModule;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace Integration.Tests;

/// <summary>Восстановление автоматического назначения курьеров после сбоев и при появлении свободных курьеров.</summary>
public class AssignmentRecoveryTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly Store _s;
    private readonly Guid _ivan;
    private readonly Product _pizza;
    private static readonly AddressDto Addr = new("Волгоград", "Мира", "15", "24");
    private static readonly Session Op = new("operator", null, "Оператор");

    public AssignmentRecoveryTests()
    {
        _s = _db.OpenStore();
        _ivan = _s.Customers().First(c => c.Name.StartsWith("Иван")).Id;
        _pizza = _s.Products().First(p => p.Name.Contains("Пицца"));
    }
    public void Dispose() { _s.Dispose(); _db.Dispose(); }

    private Delivery NewOrder() => _s.CreateOrder(new(_ivan, new() { new(_pizza.Id, 1) }, Addr)).d!;
    private void AllCouriers(CourierState st) { foreach (var c in _s.Couriers()) Assert.Null(_s.SetCourierState(c.Id, st, true)); }
    private long Rows(string sql) => _db.Scalar<long>("SELECT COUNT(*) FROM " + sql);
    private void Trap(string table) => _db.Sql($@"
CREATE OR REPLACE FUNCTION boom() RETURNS trigger AS $$ BEGIN RAISE EXCEPTION 'injected failure'; END $$ LANGUAGE plpgsql;
CREATE TRIGGER trap BEFORE UPDATE ON {table} FOR EACH ROW EXECUTE FUNCTION boom();");
    private void Untrap(string table) => _db.Sql($"DROP TRIGGER trap ON {table}");
    private int AssignedHistory(Guid deliveryId) => _s.Delivery(deliveryId)!.History.Count(h => h.Status == DeliveryStatus.Assigned);

    [Fact]
    public void TemporaryDbFailure_ThenRecovery_DeliveryGetsAssigned()
    {
        Trap("couriers");
        var (d, assigned, e) = _s.CreateOrder(new(_ivan, new() { new(_pizza.Id, 1) }, Addr));
        Assert.Null(e);
        Assert.False(assigned);                                                   // заказ сохранён, курьера нет
        Assert.ThrowsAny<DbException>(() => _s.AssignPendingDeliveries(10));      // сбой ещё не устранён
        Assert.Equal(DeliveryStatus.Created, _s.Delivery(d!.DeliveryId)!.Status);

        Untrap("couriers");                                                       // база снова в порядке
        Assert.Equal(1, _s.AssignPendingDeliveries(10).Assigned);
        var x = _s.Delivery(d.DeliveryId)!;
        Assert.Equal(DeliveryStatus.Assigned, x.Status);
        Assert.Equal(OrderStatus.Confirmed, x.OrderStatus);
        Assert.Contains(x.History, h => h.Comment!.Contains("повторное автоназначение"));
    }

    [Fact]
    public void BrokenDelivery_DoesNotBlockOthers()
    {
        AllCouriers(CourierState.Offline);
        var broken = NewOrder();                                                  // самая старая ожидающая доставка
        var ok = NewOrder();
        _db.Sql($@"
CREATE OR REPLACE FUNCTION boom() RETURNS trigger AS $$ BEGIN RAISE EXCEPTION 'injected failure'; END $$ LANGUAGE plpgsql;
CREATE TRIGGER trap BEFORE UPDATE ON deliveries FOR EACH ROW WHEN (OLD.id = '{broken.DeliveryId}') EXECUTE FUNCTION boom();");
        AllCouriers(CourierState.Available);

        Assert.Equal(1, _s.AssignPendingDeliveries(10).Assigned);                 // ошибка одной доставки не останавливает очередь
        Assert.Equal(DeliveryStatus.Assigned, _s.Delivery(ok.DeliveryId)!.Status);
        Assert.Equal(DeliveryStatus.Created, _s.Delivery(broken.DeliveryId)!.Status);

        Untrap("deliveries");
        Assert.Equal(1, _s.AssignPendingDeliveries(10).Assigned);
        Assert.Equal(DeliveryStatus.Assigned, _s.Delivery(broken.DeliveryId)!.Status);
    }

    [Fact]
    public void NoFreeCouriers_DeliveryWaits_ThenCourierAppears()
    {
        AllCouriers(CourierState.Offline);
        var d = NewOrder();
        var r = _s.AssignPendingDeliveries(10);
        Assert.Equal(0, r.Assigned);
        Assert.True(r.NoFreeCouriers);
        Assert.Equal(DeliveryStatus.Created, _s.Delivery(d.DeliveryId)!.Status);

        var olga = _s.Couriers().First(c => c.Name.StartsWith("Ольга")).Id;
        Assert.Null(_s.SetCourierState(olga, CourierState.Available, true));      // курьер вышел на линию
        Assert.Equal(1, _s.AssignPendingDeliveries(10).Assigned);
        Assert.Equal(olga, _s.Delivery(d.DeliveryId)!.CourierId);
    }

    [Fact]
    public void PendingDeliveries_AreAssignedOldestFirst_OneCourierEach()
    {
        AllCouriers(CourierState.Offline);
        var ds = Enumerable.Range(0, 5).Select(_ => NewOrder()).ToList();
        AllCouriers(CourierState.Available);
        Assert.Equal(3, _s.AssignPendingDeliveries(10).Assigned);                // три курьера — три доставки
        Assert.All(ds.Take(3), d => Assert.Equal(DeliveryStatus.Assigned, _s.Delivery(d.DeliveryId)!.Status));
        Assert.All(ds.Skip(3), d => Assert.Equal(DeliveryStatus.Created, _s.Delivery(d.DeliveryId)!.Status));
        Assert.Equal(3, _db.Scalar<long>("SELECT COUNT(DISTINCT courier_id) FROM deliveries WHERE courier_id IS NOT NULL"));
    }

    [Fact]
    public void BatchSize_LimitsOnePass()
    {
        AllCouriers(CourierState.Offline);
        NewOrder(); NewOrder();
        AllCouriers(CourierState.Available);
        Assert.Equal(1, _s.AssignPendingDeliveries(1).Assigned);
        Assert.Equal(1, _s.AssignPendingDeliveries(1).Assigned);
        Assert.Equal(0, _s.AssignPendingDeliveries(1).Assigned);
    }

    [Fact]
    public void FinishedAndCancelledDeliveries_AreNotTouched()
    {
        var delivered = NewOrder();
        foreach (var st in new[] { DeliveryStatus.Accepted, DeliveryStatus.InTransit, DeliveryStatus.Delivered })
            Assert.Null(_s.ChangeStatus(delivered.DeliveryId, st, null, Op));
        AllCouriers(CourierState.Offline);
        var cancelled = NewOrder();
        Assert.Null(_s.ChangeStatus(cancelled.DeliveryId, DeliveryStatus.Cancelled, null, Op));
        AllCouriers(CourierState.Available);

        Assert.Equal(0, _s.AssignPendingDeliveries(10).Assigned);
        Assert.Equal(DeliveryStatus.Delivered, _s.Delivery(delivered.DeliveryId)!.Status);
        Assert.Equal(DeliveryStatus.Cancelled, _s.Delivery(cancelled.DeliveryId)!.Status);
        Assert.Null(_s.Delivery(cancelled.DeliveryId)!.CourierId);
    }

    [Fact]
    public void RepeatedPasses_DoNotDuplicateAssignments()
    {
        AllCouriers(CourierState.Offline);
        var d = NewOrder();
        AllCouriers(CourierState.Available);
        for (var i = 0; i < 5; i++) _s.AssignPendingDeliveries(10);
        Assert.Equal(1, AssignedHistory(d.DeliveryId));
        Assert.Equal(1, Rows("couriers WHERE state='Busy'"));
        Assert.Equal(1, Rows("deliveries"));
    }

    [Fact]
    public async Task SeveralWorkers_InParallel_KeepDataConsistent()
    {
        AllCouriers(CourierState.Offline);
        var ds = Enumerable.Range(0, 6).Select(_ => NewOrder()).ToList();
        AllCouriers(CourierState.Available);
        // отдельные Store — как несколько экземпляров API со своими пулами соединений
        var workers = Enumerable.Range(0, 4).Select(_ => _db.OpenStore()).ToList();
        try
        {
            var results = await Task.WhenAll(workers.Select(w => Task.Run(() => w.AssignPendingDeliveries(10))));
            Assert.Equal(3, results.Sum(r => r.Assigned));
        }
        finally { workers.ForEach(w => w.Dispose()); }
        Assert.Equal(3, Rows("deliveries WHERE status='Assigned'"));
        Assert.Equal(3, _db.Scalar<long>("SELECT COUNT(DISTINCT courier_id) FROM deliveries WHERE status='Assigned'"));
        Assert.All(ds, d => Assert.True(AssignedHistory(d.DeliveryId) <= 1));
        Assert.Equal(3, Rows("couriers WHERE state='Busy'"));
    }

    [Fact]
    public async Task ManualAssign_DuringBackgroundPass_OnlyOneAssignmentWins()
    {
        for (var round = 0; round < 5; round++)
        {
            AllCouriers(CourierState.Offline);
            var d = NewOrder();
            AllCouriers(CourierState.Available);
            var dmitry = _s.Couriers().First(c => c.Name.StartsWith("Дмитрий")).Id;
            using var worker = _db.OpenStore();
            var manual = Task.Run(() => _s.Assign(d.DeliveryId, dmitry, Op));
            var background = Task.Run(() => worker.AssignPendingDeliveries(10));
            await Task.WhenAll(manual, background);
            var manualResult = await manual;
            Assert.True(manualResult is null || manualResult == "DELIVERY_ALREADY_ASSIGNED", manualResult);
            Assert.Equal(1, AssignedHistory(d.DeliveryId));
            Assert.Equal(DeliveryStatus.Assigned, _s.Delivery(d.DeliveryId)!.Status);
            Assert.Equal(1, Rows("couriers WHERE state='Busy'"));
            Assert.Null(_s.ChangeStatus(d.DeliveryId, DeliveryStatus.Cancelled, null, Op)); // освободить курьера для следующего раунда
        }
    }

    [Fact]
    public void ManualAssign_StillWorks_AndBackgroundSkipsAssigned()
    {
        AllCouriers(CourierState.Offline);
        var d = NewOrder();
        var olga = _s.Couriers().First(c => c.Name.StartsWith("Ольга")).Id;
        Assert.Null(_s.SetCourierState(olga, CourierState.Available, true));
        Assert.Null(_s.Assign(d.DeliveryId, olga, Op));
        Assert.Equal(0, _s.AssignPendingDeliveries(10).Assigned);
        Assert.Equal(olga, _s.Delivery(d.DeliveryId)!.CourierId);
    }

    [Fact]
    public void CourierWithActiveDelivery_IsNotAssignedAgain_EvenIfMarkedAvailable()
    {
        var first = NewOrder();
        var cid = first.CourierId!.Value;
        _db.Sql($"UPDATE couriers SET state='Available' WHERE id='{cid}'");            // рассогласованный флаг состояния
        foreach (var c in _s.Couriers().Where(c => c.Id != cid)) _s.SetCourierState(c.Id, CourierState.Offline, true);
        var second = NewOrder();
        Assert.Equal(DeliveryStatus.Created, second.Status);                            // автоподбор его не берёт
        Assert.Equal("COURIER_BUSY", _s.Assign(second.DeliveryId, cid, Op));           // и вручную тоже нельзя
        Assert.Equal(0, _s.AssignPendingDeliveries(10).Assigned);
    }

    [Fact]
    public void Database_RejectsSecondActiveDeliveryForCourier()
    {
        var first = NewOrder();
        foreach (var c in _s.Couriers().Where(c => c.Id != first.CourierId)) _s.SetCourierState(c.Id, CourierState.Offline, true);
        var second = NewOrder();
        var ex = Assert.Throws<PostgresException>(() =>
            _db.Sql($"UPDATE deliveries SET status='Assigned', courier_id='{first.CourierId}' WHERE id='{second.DeliveryId}'"));
        Assert.Equal("ux_deliveries_active_courier", ex.ConstraintName);
    }

    [Fact]
    public void Retry_WithoutCouriers_IsPickedUpLater()
    {
        var d = NewOrder();
        foreach (var st in new[] { DeliveryStatus.Accepted, DeliveryStatus.InTransit })
            Assert.Null(_s.ChangeStatus(d.DeliveryId, st, null, Op));
        Assert.Null(_s.ChangeStatus(d.DeliveryId, DeliveryStatus.Failed, "Клиента нет дома", Op));
        AllCouriers(CourierState.Offline);
        Assert.Null(_s.Retry(d.DeliveryId, Op));
        Assert.Equal(DeliveryStatus.Created, _s.Delivery(d.DeliveryId)!.Status);
        AllCouriers(CourierState.Available);
        Assert.Equal(1, _s.AssignPendingDeliveries(10).Assigned);
        Assert.Equal(DeliveryStatus.Assigned, _s.Delivery(d.DeliveryId)!.Status);
    }

    [Fact]
    public void Rules_AwaitsAssignment()
    {
        Assert.True(DeliveryRules.AwaitsAssignment(DeliveryStatus.Created, false));
        Assert.False(DeliveryRules.AwaitsAssignment(DeliveryStatus.Created, true));
        foreach (var s in Enum.GetValues<DeliveryStatus>().Where(s => s != DeliveryStatus.Created))
            Assert.False(DeliveryRules.AwaitsAssignment(s, false));
    }
}

/// <summary>Поведение фоновой службы: паузы при сбоях, восстановление, остановка.</summary>
public class AssignmentRetryServiceTests
{
    private static readonly AssignmentOptions Fast = new() { RetryIntervalSeconds = 1, MaxBackoffSeconds = 8, BatchSize = 5 };

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(4, 8)]
    [InlineData(10, 8)]
    [InlineData(1000, 8)]
    public void Backoff_GrowsAndIsCapped(int failures, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), AssignmentRetryService.NextDelay(failures, Fast));

    [Fact]
    public void TransientErrors_IncludeNetworkFailures()
    {
        Assert.True(Store.IsTransient(new NpgsqlException("connection refused", new System.Net.Sockets.SocketException(10061))));
        Assert.True(Store.IsTransient(new System.Net.Sockets.SocketException(11001)));             // имя хоста БД не разрешается
        Assert.True(Store.IsTransient(new InvalidOperationException("x", new TimeoutException())));
        Assert.True(Store.IsTransient(new PostgresException("terminating connection", "FATAL", "FATAL", "57P01"))); // перезапуск сервера БД
        Assert.False(Store.IsTransient(new InvalidOperationException("bug")));
        Assert.False(Store.IsTransient(null));
    }

    [Fact]
    public void DataErrors_AreStorageErrors_ButNotTransient()
    {
        var fk = new PostgresException("violates foreign key", "ERROR", "ERROR", "23503");
        var trigger = new PostgresException("injected", "ERROR", "ERROR", "P0001");
        Assert.True(Store.IsStorageError(fk));
        Assert.True(Store.IsStorageError(trigger));
        Assert.False(Store.IsTransient(fk));                                   // повтор того же запроса не поможет
        Assert.False(Store.IsTransient(trigger));
        Assert.True(Store.IsStorageError(new NpgsqlException("connection refused")));
        Assert.False(Store.IsStorageError(new InvalidOperationException("bug")));
    }

    [Fact]
    public async Task NetworkFailure_IsRetriedLikeDbError()
    {
        var calls = 0;
        var svc = new AssignmentRetryService(_ =>
        {
            if (Interlocked.Increment(ref calls) == 1) throw new System.Net.Sockets.SocketException(11001);
            return new(0, false);
        }, new AssignmentOptions { RetryIntervalSeconds = 1, MaxBackoffSeconds = 1 }, NullLogger.Instance);
        await svc.StartAsync(CancellationToken.None);
        await WaitUntil(() => Volatile.Read(ref calls) >= 2, TimeSpan.FromSeconds(10));
        await svc.StopAsync(CancellationToken.None);
        Assert.True(svc.ExecuteTask!.IsCompletedSuccessfully);
    }

    [Fact]
    public void Options_AreReadFromConfiguration_AndClamped()
    {
        var cfg = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Assignment:RetryIntervalSeconds"] = "0", ["Assignment:MaxBackoffSeconds"] = "60", ["Assignment:BatchSize"] = "100000", ["Assignment:Enabled"] = "false"
        }).Build();
        var o = AssignmentOptions.From(cfg);
        Assert.Equal(1, o.RetryIntervalSeconds);
        Assert.Equal(60, o.MaxBackoffSeconds);
        Assert.Equal(500, o.BatchSize);
        Assert.False(o.Enabled);
    }

    [Fact]
    public async Task DbOutage_DoesNotStopService_AndRetriesWithBackoff()
    {
        var calls = new List<DateTime>();
        var fail = true;
        var svc = new AssignmentRetryService(_ =>
        {
            lock (calls) calls.Add(DateTime.UtcNow);
            if (fail) throw new NpgsqlException("connection refused");
            return new(1, false);
        }, new AssignmentOptions { RetryIntervalSeconds = 1, MaxBackoffSeconds = 2 }, NullLogger.Instance);

        await svc.StartAsync(CancellationToken.None);
        await WaitUntil(() => svc.Iterations >= 2, TimeSpan.FromSeconds(10));
        fail = false;                                                             // база восстановлена
        var before = svc.Iterations;
        await WaitUntil(() => svc.Iterations >= before + 1, TimeSpan.FromSeconds(10));
        await svc.StopAsync(CancellationToken.None);

        lock (calls)
        {
            Assert.True(calls.Count >= 3);
            Assert.True(calls[1] - calls[0] >= TimeSpan.FromMilliseconds(900));    // не «молотит» базу без паузы
        }
    }

    [Fact]
    public async Task Stop_CompletesPromptly()
    {
        var svc = new AssignmentRetryService(_ => new(0, false), new AssignmentOptions { RetryIntervalSeconds = 3600 }, NullLogger.Instance);
        await svc.StartAsync(CancellationToken.None);
        await WaitUntil(() => svc.Iterations >= 1, TimeSpan.FromSeconds(5));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await svc.StopAsync(CancellationToken.None);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), sw.Elapsed.ToString());
        Assert.True(svc.ExecuteTask!.IsCompleted);
    }

    [Fact]
    public async Task Disabled_DoesNothing()
    {
        var called = false;
        var svc = new AssignmentRetryService(_ => { called = true; return new(0, false); }, new AssignmentOptions { Enabled = false }, NullLogger.Instance);
        await svc.StartAsync(CancellationToken.None);
        await Task.Delay(300);
        await svc.StopAsync(CancellationToken.None);
        Assert.False(called);
    }

    internal static async Task WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > timeout) throw new TimeoutException("Условие не выполнено за " + timeout);
            await Task.Delay(50);
        }
    }
}

/// <summary>Фоновое назначение внутри работающего API.</summary>
public class AssignmentRetryApiTests : ApiTests
{
    [Fact]
    public async Task CourierComingOnline_GetsWaitingOrder_WithoutOperator()
    {
        using var f = new ApiFactory(settings: new Dictionary<string, string> { ["Assignment:Enabled"] = "true", ["Assignment:RetryIntervalSeconds"] = "1" });
        var op = await LoginAsync("operator", f: f);
        var couriers = (await Body(await op.GetAsync("/api/couriers"))).AsArray();
        foreach (var c in couriers)
            Assert.Equal(HttpStatusCode.OK, (await op.PatchAsync($"/api/couriers/{c!["id"]}/state", Json("""{"state":"Offline"}"""))).StatusCode);

        var (pizza, _, ivanId) = await DemoIds(op);
        var ivan = await LoginAsync("ivan", f: f);
        var order = await Body(await ivan.PostAsync("/api/orders", Json(OrderJson(ivanId, pizza))));
        Assert.False((bool)order["courierAssigned"]!);
        Assert.Contains("автоматически", order["message"]!.ToString());
        var deliveryId = order["deliveryId"]!.ToString();

        await Task.Delay(1500);                                                     // курьеров нет — доставка ждёт
        Assert.Equal("Created", f.Db.Scalar<string>($"SELECT status FROM deliveries WHERE id='{deliveryId}'"));

        var olga = couriers.First(c => c!["name"]!.ToString().StartsWith("Ольга"))!["id"]!.ToString();
        Assert.Equal(HttpStatusCode.OK, (await op.PatchAsync($"/api/couriers/{olga}/state", Json("""{"state":"Available"}"""))).StatusCode);

        await AssignmentRetryServiceTests.WaitUntil(() =>
            f.Db.Scalar<string>($"SELECT status FROM deliveries WHERE id='{deliveryId}'") == "Assigned", TimeSpan.FromSeconds(15));
        Assert.Equal(olga, f.Db.Scalar<Guid>($"SELECT courier_id FROM deliveries WHERE id='{deliveryId}'").ToString());
        var d = await Body(await op.GetAsync($"/api/deliveries/{deliveryId}"));
        Assert.Equal("Confirmed", d["orderStatus"]!.ToString());
    }
}
