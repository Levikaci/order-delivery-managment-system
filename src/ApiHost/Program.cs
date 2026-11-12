using System.Text.Json.Serialization;
using ApiHost;

var builder = WebApplication.CreateBuilder(args);
// Порт по умолчанию 8080 (как в клиенте, Docker и README), если адрес не задан --urls / ASPNETCORE_URLS / launchSettings.
if (string.IsNullOrEmpty(builder.Configuration["urls"]) && string.IsNullOrEmpty(builder.Configuration["http_ports"]))
    builder.WebHost.UseUrls("http://localhost:8080");
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSingleton<Store>();
builder.Services.AddSingleton<AuthService>();
builder.Services.AddSingleton<LoginThrottle>();
builder.Services.AddSingleton<RegistrationThrottle>();
// повторное автоназначение курьеров доставкам, оставшимся в статусе Created (сбой БД, нет свободных курьеров)
builder.Services.AddHostedService<AssignmentRetryService>();

var app = builder.Build();
// Вне Development сервер не стартует со стандартными паролями, демо-пользователями и без строки подключения.
StartupChecks.EnsureValid(app.Configuration, app.Environment);
app.Services.GetRequiredService<Store>(); // применить миграции при старте: при ошибке приложение не запускается с повреждённой схемой

// Единый формат ошибок (ErrorResponse из ТЗ): { success:false, errorCode, message }.
var messages = new Dictionary<string, string>
{
    ["UNAUTHORIZED"] = "Требуется вход в систему",
    ["FORBIDDEN"] = "Недостаточно прав для этого действия",
    ["INVALID_CREDENTIALS"] = "Неверный логин или пароль",
    ["INVALID_REQUEST"] = "Некорректный запрос",
    ["CUSTOMER_NOT_FOUND"] = "Клиент не найден",
    ["COURIER_NOT_FOUND"] = "Курьер не найден",
    ["ORDER_NOT_FOUND"] = "Заказ не найден",
    ["DELIVERY_NOT_FOUND"] = "Доставка не найдена",
    ["ADDRESS_NOT_FOUND"] = "Адрес не найден",
    ["PRODUCT_NOT_FOUND"] = "Товар не найден",
    ["INVALID_ADDRESS"] = "Укажите город, улицу и дом",
    ["INVALID_NAME"] = "Укажите имя",
    ["INVALID_PRODUCT"] = "Укажите название и цену товара",
    ["EMPTY_ORDER"] = "Добавьте в заказ хотя бы один товар",
    ["INVALID_QUANTITY"] = "Количество должно быть от 1 до 99",
    ["LOGIN_TAKEN"] = "Такой логин уже занят",
    ["CUSTOMER_HAS_ORDERS"] = "У клиента есть заказы, удалить нельзя",
    ["COURIER_HAS_ORDERS"] = "У курьера есть доставки, удалить нельзя",
    ["COURIER_NOT_AVAILABLE"] = "Нет доступных курьеров",
    ["COURIER_BUSY"] = "Курьер сейчас занят доставкой",
    ["INVALID_COURIER_STATE"] = "Состояние «Занят» устанавливается системой при назначении",
    ["DELIVERY_ALREADY_ASSIGNED"] = "Курьер уже назначен",
    ["DELIVERY_ALREADY_COMPLETED"] = "Доставка уже завершена",
    ["DELIVERY_ALREADY_EXISTS"] = "Доставка для заказа уже существует",
    ["INVALID_STATUS_TRANSITION"] = "Недопустимый переход статуса",
    ["UNAUTHORIZED_STATUS_CHANGE"] = "Вы не можете изменить этот статус (отмена возможна только до передачи в доставку)",
    ["FAILURE_REASON_REQUIRED"] = "Укажите причину, по которой доставка не выполнена",
    ["ORDER_NOT_EDITABLE"] = "Заказ нельзя изменить после принятия курьером",
    ["WEAK_PASSWORD"] = "Пароль должен быть не короче 8 символов, не совпадать с логином, старым паролем и стандартными значениями",
    ["TOO_MANY_ATTEMPTS"] = "Слишком много неудачных попыток входа. Повторите позже",
    ["TOO_MANY_REGISTRATIONS"] = "Слишком много попыток регистрации. Повторите позже",
    ["REGISTRATION_DISABLED"] = "Самостоятельная регистрация отключена. Обратитесь к оператору",
    ["INVALID_LOGIN"] = "Логин: от 3 до 32 символов — латинские буквы, цифры, точка, дефис или подчёркивание",
    ["DELIVERY_NOT_FAILED"] = "Повторить можно только неудачную доставку",
    ["IDEMPOTENCY_CONFLICT"] = "Этот requestId уже использован для заказа с другими данными. Для нового заказа нужен новый requestId",
    ["STORAGE_ERROR"] = "Временная ошибка хранилища данных. Повторите запрос позже",
    ["INTERNAL_ERROR"] = "Внутренняя ошибка сервера",
};

IResult Err(string code, int? statusOverride = null)
{
    var status = statusOverride ?? code switch
    {
        "UNAUTHORIZED" or "INVALID_CREDENTIALS" => 401,
        "TOO_MANY_ATTEMPTS" or "TOO_MANY_REGISTRATIONS" => 429,
        "FORBIDDEN" or "UNAUTHORIZED_STATUS_CHANGE" or "REGISTRATION_DISABLED" => 403,
        "STORAGE_ERROR" or "INTERNAL_ERROR" => 500,
        _ when code.EndsWith("NOT_FOUND") => 404,
        _ when code.EndsWith("HAS_ORDERS") || code.StartsWith("DELIVERY_ALREADY") || code is "COURIER_NOT_AVAILABLE" or "COURIER_BUSY"
            or "INVALID_STATUS_TRANSITION" or "DELIVERY_NOT_FAILED" or "ORDER_NOT_EDITABLE" or "LOGIN_TAKEN" or "IDEMPOTENCY_CONFLICT" => 409,
        _ => 400
    };
    return Results.Json(new { success = false, errorCode = code, error = code, message = messages.GetValueOrDefault(code, code) }, statusCode: status);
}

// Любое необработанное исключение — в едином формате ошибок, без трассировки в ответе.
app.UseExceptionHandler(h => h.Run(async ctx =>
{
    var ex = ctx.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
    var code = ex switch
    {
        BadHttpRequestException or System.Text.Json.JsonException => "INVALID_REQUEST",
        _ when Store.IsStorageError(ex) => "STORAGE_ERROR", // в том числе сетевые ошибки при недоступной БД
        _ => "INTERNAL_ERROR"
    };
    if (code != "INVALID_REQUEST") ctx.RequestServices.GetRequiredService<ILogger<Program>>().LogError(ex, "Необработанная ошибка запроса {Path}", ctx.Request.Path);
    // база временно недоступна (нет соединения, таймаут) — 503 и Retry-After: запрос можно повторить;
    // ошибка данных или SQL — 500, повтор того же запроса не поможет
    if (code == "STORAGE_ERROR" && Store.IsTransient(ex))
    {
        ctx.Response.Headers.RetryAfter = "5";
        await Err(code, 503).ExecuteAsync(ctx);
        return;
    }
    await Err(code).ExecuteAsync(ctx);
}));

// Фильтр доступа: пустой список ролей = любой вошедший пользователь.
RouteHandlerBuilder Roles(RouteHandlerBuilder b, params string[] roles) => b.AddEndpointFilter(async (ctx, next) =>
{
    var s = ctx.HttpContext.RequestServices.GetRequiredService<AuthService>().Get(ctx.HttpContext.Request);
    if (s is null) return Err("UNAUTHORIZED");
    if (roles.Length > 0 && !roles.Contains(s.Role)) return Err("FORBIDDEN");
    ctx.HttpContext.Items["me"] = s;
    return await next(ctx);
});
Session Me(HttpContext h) => (Session)h.Items["me"]!;
bool Own(Session me, Guid customerId) => me.Role == "operator" || me.RefId == customerId;

var api = app.MapGroup("/api");
api.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// --- вход ---
api.MapPost("/auth/login", (HttpContext h, Store s, AuthService a, LoginThrottle t, LoginRequest r) =>
{
    var ip = h.Connection.RemoteIpAddress?.ToString();
    if (t.IsLocked(r?.Login, ip)) return Err("TOO_MANY_ATTEMPTS");
    if (s.Authenticate(r?.Login, r?.Password) is not { } me)
    {
        t.Failed(r?.Login, ip);
        return Err("INVALID_CREDENTIALS");
    }
    t.Succeeded(r?.Login, ip);
    return Results.Ok(new { token = a.Start(me), role = me.Role, refId = me.RefId, name = me.Name });
});
// Регистрация клиента без участия оператора; курьеров и операторов по-прежнему создаёт только оператор.
api.MapPost("/auth/register", (HttpContext h, Store s, AuthService a, RegistrationThrottle t, IConfiguration cfg, RegisterRequest r) =>
{
    if (!cfg.GetValue("Security:AllowRegistration", true)) return Err("REGISTRATION_DISABLED");
    if (!t.TryAcquire(h.Connection.RemoteIpAddress?.ToString())) return Err("TOO_MANY_REGISTRATIONS");
    var (me, e) = s.RegisterClient(r);
    if (me is null) return Err(e ?? "INVALID_REQUEST");
    return Results.Ok(new { token = a.Start(me), role = me.Role, refId = me.RefId, name = me.Name, login = r.Login.Trim() });
});
Roles(api.MapPost("/auth/logout", (HttpContext h, AuthService a) => { a.End(AuthService.Token(h.Request)!); return Results.Ok(); }));
Roles(api.MapPost("/auth/password", (HttpContext h, Store s, ChangePasswordRequest r) =>
    s.ChangePassword(Me(h), r?.OldPassword, r?.NewPassword, AuthService.Token(h.Request) is { } t ? AuthService.HashToken(t) : null) is { } e ? Err(e) : Results.Ok()));
Roles(api.MapGet("/auth/me", (HttpContext h) => Results.Ok(Me(h))));

Roles(api.MapGet("/state", (HttpContext h, Store s) => Results.Ok(s.Snapshot(Me(h)))));

// --- клиенты и адреса ---
Roles(api.MapGet("/customers", (Store s) => Results.Ok(s.Customers())), "operator");
Roles(api.MapPost("/customers", (Store s, NewPerson p) => { var (c, e) = s.NewCustomer(p); return c is null ? Err(e!) : Results.Ok(c); }), "operator");
Roles(api.MapGet("/customers/{id:guid}", (HttpContext h, Store s, Guid id) =>
    !Own(Me(h), id) ? Err("FORBIDDEN") : s.Customer(id) is { } c ? Results.Ok(c) : Err("CUSTOMER_NOT_FOUND")), "operator", "client");
Roles(api.MapPut("/customers/{id:guid}", (Store s, Guid id, NewPerson p) => s.UpdateCustomer(id, p) is { } e ? Err(e) : Results.Ok()), "operator");
Roles(api.MapDelete("/customers/{id:guid}", (Store s, Guid id) => s.DeleteCustomer(id) is { } e ? Err(e) : Results.Ok()), "operator");

Roles(api.MapGet("/customers/{id:guid}/addresses", (HttpContext h, Store s, Guid id) =>
    !Own(Me(h), id) ? Err("FORBIDDEN") : Results.Ok(s.Addresses(id))), "operator", "client");
Roles(api.MapPost("/customers/{id:guid}/addresses", (HttpContext h, Store s, Guid id, AddressRequest r) =>
{
    if (!Own(Me(h), id)) return Err("FORBIDDEN");
    var (a, e) = s.AddAddress(id, r);
    return a is null ? Err(e!) : Results.Ok(a);
}), "operator", "client");
Roles(api.MapGet("/addresses/{id:guid}", (HttpContext h, Store s, Guid id) =>
    s.Address(id) is not { } a ? Err("ADDRESS_NOT_FOUND") : !Own(Me(h), a.CustomerId) ? Err("FORBIDDEN") : Results.Ok(a)), "operator", "client");
Roles(api.MapPut("/addresses/{id:guid}", (HttpContext h, Store s, Guid id, AddressRequest r) =>
    s.Address(id) is not { } a ? Err("ADDRESS_NOT_FOUND") : !Own(Me(h), a.CustomerId) ? Err("FORBIDDEN")
    : s.UpdateAddress(id, r) is { } e ? Err(e) : Results.Ok()), "operator", "client");
Roles(api.MapDelete("/addresses/{id:guid}", (HttpContext h, Store s, Guid id) =>
    s.Address(id) is not { } a ? Err("ADDRESS_NOT_FOUND") : !Own(Me(h), a.CustomerId) ? Err("FORBIDDEN")
    : s.DeleteAddress(id) is { } e ? Err(e) : Results.Ok()), "operator", "client");

// --- курьеры ---
Roles(api.MapGet("/couriers", (Store s) => Results.Ok(s.Couriers())), "operator");
Roles(api.MapGet("/couriers/available", (Store s) => Results.Ok(s.Couriers(true))), "operator");
Roles(api.MapPost("/couriers", (Store s, NewPerson p) => { var (c, e) = s.NewCourier(p); return c is null ? Err(e!) : Results.Ok(c); }), "operator");
Roles(api.MapPut("/couriers/{id:guid}", (Store s, Guid id, NewPerson p) => s.UpdateCourier(id, p) is { } e ? Err(e) : Results.Ok()), "operator");
Roles(api.MapDelete("/couriers/{id:guid}", (Store s, Guid id) => s.DeleteCourier(id) is { } e ? Err(e) : Results.Ok()), "operator");
Roles(api.MapPatch("/couriers/{id:guid}/state", (HttpContext h, Store s, Guid id, CourierStateRequest r) =>
{
    var me = Me(h);
    if (me.Role == "courier" && me.RefId != id) return Err("FORBIDDEN");
    return s.SetCourierState(id, r.State, me.Role == "operator") is { } e ? Err(e) : Results.Ok();
}), "operator", "courier");
Roles(api.MapGet("/couriers/{id:guid}/deliveries", (HttpContext h, Store s, Guid id) =>
    Me(h).Role == "courier" && Me(h).RefId != id ? Err("FORBIDDEN") : Results.Ok(s.Deliveries(courierId: id))), "operator", "courier");

// --- товары ---
Roles(api.MapGet("/products", (Store s) => Results.Ok(s.Products())));
Roles(api.MapPost("/products", (Store s, ProductRequest r) => { var (p, e) = s.NewProduct(r); return p is null ? Err(e!) : Results.Ok(p); }), "operator");
Roles(api.MapPut("/products/{id:guid}", (Store s, Guid id, ProductRequest r) => s.UpdateProduct(id, r) is { } e ? Err(e) : Results.Ok()), "operator");
Roles(api.MapDelete("/products/{id:guid}", (Store s, Guid id) => s.DeleteProduct(id) is { } e ? Err(e) : Results.Ok()), "operator");

// --- заказы ---
// Создаёт заказ и доставку (Протокол 1) и пытается назначить свободного курьера (сценарий 1).
// Если курьеров нет — заказ сохраняется, доставка остаётся в статусе Created (сценарий 2).
Roles(api.MapPost("/orders", (HttpContext h, Store s, CreateOrderRequest r) =>
{
    var me = Me(h);
    if (r is null) return Err("INVALID_REQUEST");
    if (me.RefId != r.CustomerId) return Err("FORBIDDEN");
    var (d, assigned, e) = s.CreateOrder(r, me);
    return d is null ? Err(e!) : Results.Ok(new
    {
        orderId = d.OrderId,
        status = d.Status,
        orderStatus = d.OrderStatus,
        deliveryId = d.DeliveryId,
        courierId = d.CourierId,
        courierAssigned = assigned,
        message = assigned ? "Заказ создан, курьер назначен"
            : "Заказ создан, свободного курьера пока нет: он будет назначен автоматически. Оператор может назначить вручную: POST /api/deliveries/" + d.DeliveryId + "/assign"
    });
}), "client");
Roles(api.MapGet("/orders/{id:guid}", (HttpContext h, Store s, Guid id) =>
    s.Order(id) is not { } d ? Err("ORDER_NOT_FOUND") : !Own(Me(h), d.CustomerId) && !(Me(h).Role == "courier" && d.CourierId == Me(h).RefId) ? Err("FORBIDDEN") : Results.Ok(d)));
Roles(api.MapPut("/orders/{id:guid}", (HttpContext h, Store s, Guid id, UpdateOrderRequest r) =>
    s.Order(id) is not { } d ? Err("ORDER_NOT_FOUND") : !Own(Me(h), d.CustomerId) ? Err("FORBIDDEN")
    : s.UpdateOrder(id, r, Me(h)) is { } e ? Err(e) : Results.Ok()), "operator", "client");
Roles(api.MapGet("/orders/{id:guid}/changes", (HttpContext h, Store s, Guid id) =>
    s.Order(id) is not { } d ? Err("ORDER_NOT_FOUND") : !Own(Me(h), d.CustomerId) ? Err("FORBIDDEN") : Results.Ok(d.Changes)), "operator", "client");

// --- доставки ---
Roles(api.MapPost("/deliveries", (HttpContext h, Store s, CreateDeliveryBody b) =>
{
    var (d, e) = s.CreateDelivery(b.OrderId, Me(h));
    return e is null ? Results.Ok(new { deliveryId = d!.DeliveryId, orderId = d.OrderId, status = d.Status })
        : e == "DELIVERY_ALREADY_EXISTS" ? Results.Json(new { success = false, errorCode = e, error = e, message = messages[e], deliveryId = d!.DeliveryId }, statusCode: 409) : Err(e);
}), "operator");
Roles(api.MapGet("/deliveries", (HttpContext h, Store s, string? q, DeliveryStatus? status, Guid? courierId, Guid? customerId) =>
{
    var me = Me(h);
    if (me.Role == "client") customerId = me.RefId;
    if (me.Role == "courier") courierId = me.RefId;
    return Results.Ok(s.Deliveries(q, status, courierId, customerId));
}));
Roles(api.MapGet("/deliveries/{id:guid}", (HttpContext h, Store s, Guid id) =>
    s.Delivery(id) is not { } d ? Err("DELIVERY_NOT_FOUND")
    : !Own(Me(h), d.CustomerId) && !(Me(h).Role == "courier" && d.CourierId == Me(h).RefId) ? Err("FORBIDDEN") : Results.Ok(d)));
Roles(api.MapPost("/deliveries/{id:guid}/assign", (HttpContext h, Store s, Guid id, AssignRequest? r) =>
    s.Assign(id, r?.CourierId, Me(h)) is { } e ? Err(e) : Results.Ok(new { deliveryId = id, courierId = s.Delivery(id)!.CourierId, status = DeliveryStatus.Assigned })), "operator");
Roles(api.MapPatch("/deliveries/{id:guid}/status", (HttpContext h, Store s, Guid id, StatusRequest r) =>
{
    var before = s.Delivery(id)?.Status;
    return s.ChangeStatus(id, r.Status, r.Comment, Me(h)) is { } e ? Err(e) : Results.Ok(new { deliveryId = id, previousStatus = before, status = r.Status });
}));
Roles(api.MapPost("/deliveries/{id:guid}/retry", (HttpContext h, Store s, Guid id) =>
    s.Retry(id, Me(h)) is { } e ? Err(e) : Results.Ok(new { deliveryId = id, status = s.Delivery(id)!.Status })), "operator");
Roles(api.MapPost("/deliveries/{id:guid}/confirm", (HttpContext h, Store s, Guid id) =>
    s.ConfirmReceipt(id, Me(h)) is { } e ? Err(e) : Results.Ok()), "client", "operator");

app.Run();

public partial class Program { }
