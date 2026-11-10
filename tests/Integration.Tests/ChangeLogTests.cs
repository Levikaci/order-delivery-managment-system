using ApiHost;
using Contracts;
using Xunit;

namespace Integration.Tests;

/// <summary>Журнал изменений заказа: состав, адрес, комментарий; автор и время.</summary>
public class ChangeLogTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly Store _s;
    private readonly Guid _ivan;
    private readonly Product _pizza, _juice;
    private readonly Session _client, _op;

    public ChangeLogTests()
    {
        _s = _db.OpenStore();
        _ivan = _s.Customers().First(c => c.Name.StartsWith("Иван")).Id;
        _pizza = _s.Products().First(p => p.Name.Contains("Пицца"));
        _juice = _s.Products().First(p => p.Name.Contains("Сок"));
        _client = _s.Authenticate("ivan", "ivan")!;
        _op = _s.Authenticate("operator", "operator")!;
    }
    public void Dispose() { _s.Dispose(); _db.Dispose(); }

    private Delivery Create() => _s.CreateOrder(new(_ivan, new() { new(_pizza.Id, 2) }, new("Волгоград", "Мира", "15", "24"), null, "без лука"), _client).d!;

    [Fact]
    public void Authenticate_ReturnsUserId() => Assert.NotNull(_client.UserId);

    [Fact]
    public void ItemsChange_IsLoggedWithOldAndNewValues_Author_AndTime()
    {
        var d = Create();
        var before = DateTime.UtcNow.AddSeconds(-2);
        Assert.Null(_s.UpdateOrder(d.OrderId, new(new() { new(_pizza.Id, 3), new(_juice.Id, 1) }), _client));
        var ch = Assert.Single(_s.OrderChanges(d.OrderId));
        Assert.Equal("Items", ch.Field);
        Assert.Equal("Пицца Маргарита ×2", ch.OldValue);
        Assert.Equal("Пицца Маргарита ×3, Сок апельсиновый ×1", ch.NewValue);
        Assert.Equal(_client.UserId, ch.ActorId);
        Assert.Equal(_client.Name, ch.ActorName);
        Assert.InRange(ch.At, before, DateTime.UtcNow.AddSeconds(2));
    }

    [Fact]
    public void AddressAndCommentChanges_AreLoggedSeparately()
    {
        var d = Create();
        Assert.Null(_s.UpdateOrder(d.OrderId, new(null, new("Волгоград", "Чуйкова", "3", null), null, "позвонить"), _op));
        var changes = _s.OrderChanges(d.OrderId);
        Assert.Equal(new[] { "Address", "Comment" }, changes.Select(c => c.Field));
        Assert.Equal("г. Волгоград, ул. Мира, д. 15, кв. 24", changes[0].OldValue);
        Assert.Equal("г. Волгоград, ул. Чуйкова, д. 3", changes[0].NewValue);
        Assert.Equal(("без лука", "позвонить"), (changes[1].OldValue, changes[1].NewValue));
        Assert.All(changes, c => Assert.Equal(_op.UserId, c.ActorId));
    }

    [Fact]
    public void UpdateWithoutRealChange_WritesNothing_AndKeepsHistoryClean()
    {
        var d = Create();
        var historyBefore = _s.Order(d.OrderId)!.OrderHistory.Count;
        Assert.Null(_s.UpdateOrder(d.OrderId, new(new() { new(_pizza.Id, 2) }, null, null, "без лука"), _client));
        Assert.Empty(_s.OrderChanges(d.OrderId));
        Assert.Equal(historyBefore, _s.Order(d.OrderId)!.OrderHistory.Count);
    }

    [Fact]
    public void ClearingComment_IsLogged_WithNullNewValue()
    {
        var d = Create();
        Assert.Null(_s.UpdateOrder(d.OrderId, new(null, null, null, ""), _client));
        var ch = Assert.Single(_s.OrderChanges(d.OrderId));
        Assert.Equal("без лука", ch.OldValue);
        Assert.Null(ch.NewValue);
    }

    [Fact]
    public void Changes_SurviveRestart_AndAreVisibleInOrderPayload()
    {
        var d = Create();
        _s.UpdateOrder(d.OrderId, new(null, null, null, "новый"), _client);
        _s.Dispose();
        using var s2 = _db.OpenStore();
        Assert.Single(s2.Order(d.OrderId)!.Changes);
    }

    [Fact]
    public void RejectedUpdate_LogsNothing()
    {
        var d = Create();
        var cid = d.CourierId!.Value;
        _s.ChangeStatus(d.DeliveryId, DeliveryStatus.Accepted, null, new("courier", cid, "K"));
        Assert.Equal("ORDER_NOT_EDITABLE", _s.UpdateOrder(d.OrderId, new(null, null, null, "поздно"), _client));
        Assert.Empty(_s.OrderChanges(d.OrderId));
    }

    [Fact]
    public void StatusHistory_RecordsActorId()
    {
        var d = Create();
        Assert.Null(_s.ChangeStatus(d.DeliveryId, DeliveryStatus.Cancelled, null, _client));
        var x = _s.Delivery(d.DeliveryId)!;
        Assert.Equal(_client.UserId, x.History.Last().ActorId);
        Assert.Equal(_client.UserId, x.OrderHistory.Last().ActorId);
        Assert.Equal(_client.UserId, x.History.First().ActorId); // «Доставка создана» — тоже автор заказа
    }
}
