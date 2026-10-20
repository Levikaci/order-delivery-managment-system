using Contracts.Delivery;
using DeliveryModule;
using OrderModule;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddLogging();
builder.Services.AddSingleton<IDeliveryService, DeliveryService>();
builder.Services.AddSingleton<OrderService, OrderService>();

var app = builder.Build();

app.MapPost("/api/orders", async (
    OrderService orderService,
    Guid customerId,
    string address) =>
{
    var orderId = Guid.NewGuid();

    var created = await orderService.CreateOrderAsync(
        orderId,
        customerId,
        address);

    if (!created)
    {
        return Results.BadRequest(new
        {
            error = "DELIVERY_CREATION_FAILED"
        });
    }

    return Results.Ok(new
    {
        orderId,
        message = "Заказ успешно создан"
    });
});


app.MapGet("/", () => "Order Delivery Management System");

app.Run();
