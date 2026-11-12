using System.Net;
using ApiHost;
using Xunit;

namespace Integration.Tests;

/// <summary>Проверка входных данных API: перечисления, длины полей, логины, поиск.</summary>
public class ValidationTests : ApiTests
{
    [Theory]
    [InlineData("""{"status":99}""")]
    [InlineData("""{"status":1}""")]
    [InlineData("""{"status":"Unknown"}""")]
    public async Task DeliveryStatus_AsNumberOrUnknownName_Is400(string body)
    {
        var op = await LoginAsync("operator");
        var (pizza, _, customer) = await DemoIds(op);
        var d = await CreateOrderAs(await LoginAsync("ivan"), customer, pizza);
        var r = await op.PatchAsync($"/api/deliveries/{d["deliveryId"]}/status", Json(body));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("INVALID_REQUEST", await Code(r));
    }

    [Fact]
    public async Task CourierState_AsNumber_Is400()
    {
        var op = await LoginAsync("operator");
        var courier = (await Body(await op.GetAsync("/api/couriers"))).AsArray()[0]!["id"]!.ToString();
        var r = await op.PatchAsync($"/api/couriers/{courier}/state", Json("""{"state":42}"""));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task DeliveriesFilter_WithNumericStatus_Is400()
    {
        var op = await LoginAsync("operator");
        Assert.Equal(HttpStatusCode.BadRequest, (await op.GetAsync("/api/deliveries?status=99")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await op.GetAsync("/api/deliveries?status=Created")).StatusCode);
    }

    [Theory]
    [InlineData("""{"name":"Курьер","login":"a b","password":"Strong-pass-42"}""", "INVALID_LOGIN")]
    [InlineData("""{"name":"Курьер","login":"кириллица","password":"Strong-pass-42"}""", "INVALID_LOGIN")]
    [InlineData("""{"name":"Курьер","login":"ab","password":"Strong-pass-42"}""", "INVALID_LOGIN")]
    [InlineData("""{"name":"Курьер","phone":"позвоните мне","login":"ok.login"}""", "INVALID_PHONE")]
    [InlineData("""{"name":"Курьер","phone":"+7 900 000-00-00 0000000000000000000","login":"ok.login"}""", "INVALID_PHONE")]
    public async Task OperatorCreatedCourier_IsValidatedLikeRegistration(string body, string code)
    {
        var op = await LoginAsync("operator");
        var r = await op.PostAsync("/api/couriers", Json(body));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal(code, await Code(r));
    }

    [Fact]
    public async Task TooLongFields_AreRejected()
    {
        var op = await LoginAsync("operator");
        var (pizza, _, customer) = await DemoIds(op);
        var longText = new string('я', 501);
        var order = await (await LoginAsync("ivan")).PostAsync("/api/orders", Json($$"""{"customerId":"{{customer}}","items":[{"productId":"{{pizza}}","quantity":1}],"deliveryAddress":{"city":"В","street":"М","house":"1"},"comment":"{{longText}}"}"""));
        Assert.Equal("INVALID_COMMENT", await Code(order));
        var street = new string('у', 101);
        var addr = await op.PostAsync($"/api/customers/{customer}/addresses", Json($$"""{"city":"В","street":"{{street}}","house":"1"}"""));
        Assert.Equal("INVALID_ADDRESS", await Code(addr));
        var product = await op.PostAsync("/api/products", Json($$"""{"name":"{{street}}","price":10}"""));
        Assert.Equal("INVALID_PRODUCT", await Code(product));
        var rename = await op.PutAsync($"/api/customers/{customer}", Json("""{"name":"Иван","phone":"12345678901234567890123456789012345"}"""));
        Assert.Equal("INVALID_PHONE", await Code(rename));
    }

    [Fact]
    public async Task Search_TreatsPercentAndUnderscoreLiterally()
    {
        var op = await LoginAsync("operator");
        var (pizza, _, customer) = await DemoIds(op);
        await CreateOrderAs(await LoginAsync("ivan"), customer, pizza);
        Assert.NotEmpty((await Body(await op.GetAsync("/api/deliveries?q=Мира"))).AsArray());
        Assert.Empty((await Body(await op.GetAsync("/api/deliveries?q=%25"))).AsArray());     // «%» — не «любые символы»
        Assert.Empty((await Body(await op.GetAsync("/api/deliveries?q=_"))).AsArray());
    }

    [Theory]
    [InlineData("50%", "50\\%")]
    [InlineData("a_b", "a\\_b")]
    [InlineData("c:\\x", "c:\\\\x")]
    public void EscapeLike_EscapesSpecialCharacters(string input, string expected) => Assert.Equal(expected, Store.EscapeLike(input));
}
