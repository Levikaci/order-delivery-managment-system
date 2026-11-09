# Соответствие ТЗ

| Требование ТЗ | Реализация |
|---|---|
| Веб-приложение на ASP.NET Core | `src/ApiHost`, minimal API + `wwwroot/index.html` |
| Модули Order, Delivery, Courier, Customer | `src/OrderModule`, `DeliveryModule`, `CourierModule`, `CustomerModule` (`*Rules.cs`), общие DTO и enum в `src/Contracts` |
| Создание заказа с товарами и адресом | `POST /api/orders`, таблицы `orders`, `order_items`, `products` |
| Сохранённые адреса клиента | `customer_addresses`, `/api/customers/{id}/addresses` |
| Редактирование и отмена заказа | `PUT /api/orders/{id}` (Created/Assigned), отмена клиентом до передачи курьеру |
| Создание доставки | `POST /api/deliveries` (повтор — 409 `DELIVERY_ALREADY_EXISTS`) |
| Назначение курьера, автоподбор | `POST /api/deliveries/{id}/assign`; авто при пустом `courierId` и при создании заказа |
| Нет свободного курьера | доставка остаётся `Created`, операцию можно повторить |
| Состояния курьера | Available, Busy, Offline, Blocked; освобождение по финальному статусу |
| Статусы доставки | Created, Assigned, Accepted, InTransit, Delivered, Failed, Cancelled; `DeliveryRules` |
| Статусы заказа | Created, Confirmed, Preparing, InDelivery, Delivered, Cancelled; выводятся из статуса доставки |
| Причина неудачи | `Failed` требует `reason` (`FAILURE_REASON_REQUIRED`) |
| Время начала и завершения | `started_at` (InTransit), `finished_at` (финальный статус) |
| История статусов | `status_history`, `order_history` |
| Единый формат ошибок | `{success, errorCode, error, message}` |
| Роли и права | вход по логину/паролю, Bearer-токен, проверка роли на каждом endpoint; оператор заказы не создаёт |
| Хранение данных | SQLite, схема v2 |
| Запуск | `docker compose up --build` или `dotnet run` |
| Тесты | 46 автоматических тестов |
