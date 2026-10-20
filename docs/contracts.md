# API, контракты и DTO

Базовый путь API:

```text
/api
```

## Основные endpoints

| Метод | Endpoint | Назначение |
|---|---|---|
| POST | `/api/orders` | Создание заказа |
| GET | `/api/orders/{id}` | Получение заказа |
| POST | `/api/deliveries` | Создание доставки |
| GET | `/api/deliveries/{id}` | Получение доставки |
| POST | `/api/deliveries/{id}/assign` | Назначение курьера |
| PATCH | `/api/deliveries/{id}/status` | Изменение статуса |
| GET | `/api/couriers/available` | Свободные курьеры |
| GET | `/api/customers/{id}` | Данные клиента |

## CreateOrderRequest

```json
{
  "customerId": "8f3f7b9c-5a51-4df7-8c0d-5e9f1d4c2a10",
  "items": [
    {
      "productId": "1d4a1a31-3e4e-4c93-a9af-2b4d4dcb7c21",
      "quantity": 2
    }
  ],
  "deliveryAddress": {
    "city": "Волгоград",
    "street": "Мира",
    "house": "15",
    "apartment": "24"
  }
}
```

## CreateOrderResponse

```json
{
  "orderId": "a6f3f77e-3d61-4d3f-a0a2-0f58f5d6a4b1",
  "status": "Created",
  "deliveryId": "c8b4d8de-1d2a-4e2f-9f8c-9a3d2f8f1b32"
}
```

## DeliveryDto

```json
{
  "deliveryId": "c8b4d8de-1d2a-4e2f-9f8c-9a3d2f8f1b32",
  "orderId": "a6f3f77e-3d61-4d3f-a0a2-0f58f5d6a4b1",
  "courierId": "5d2d1c44-0e76-4b8c-bc2a-91f5e6b5a712",
  "status": "Assigned",
  "address": "г. Волгоград, ул. Мира, д. 15, кв. 24"
}
```

## ErrorResponse

```json
{
  "success": false,
  "errorCode": "COURIER_NOT_AVAILABLE",
  "message": "Нет доступных курьеров"
}
```

# Протокол 1: создание доставки

1. Инициатор: `OrderModule`.
2. Получатель: `DeliveryModule`.
3. Назначение: создание доставки после оформления заказа.
4. Транспорт: внутренний вызов через интерфейс; внешний вариант HTTP/JSON.
5. Точка входа: `POST /api/deliveries`, внутренний `IDeliveryService.CreateAsync(...)`.
6. Запрос: `CreateDeliveryRequest`.
7. Успешный ответ: `DeliveryDto`.
8. Ошибки: `ORDER_NOT_FOUND`, `CUSTOMER_NOT_FOUND`, `INVALID_ADDRESS`, `DELIVERY_ALREADY_EXISTS`.
9. Таймаут: целевой 3 секунды.
10. Повтор: только после проверки результата; `orderId` используется для идемпотентности.
11. Аутентификация: внешний API требует авторизацию.
12. Версия: `DeliveryContract v1`.

Пример:

```text
OrderModule
   |
   | CreateAsync(CreateDeliveryRequest)
   v
DeliveryModule
   |
   | проверка + создание
   v
DeliveryDto
```

# Протокол 2: назначение курьера

1. Инициатор: `DeliveryModule`.
2. Получатель: `CourierModule`.
3. Назначение: назначение свободного курьера.
4. Транспорт: внутренний вызов через интерфейс.
5. Точка входа: `POST /api/deliveries/{deliveryId}/assign`.
6. Запрос: `{ "courierId": "..." }`.
7. Ответ: `{ "deliveryId": "...", "courierId": "...", "status": "Assigned" }`.
8. Ошибки: `COURIER_NOT_FOUND`, `COURIER_NOT_AVAILABLE`, `DELIVERY_NOT_FOUND`, `DELIVERY_ALREADY_ASSIGNED`.
9. Таймаут: целевой 3 секунды.
10. Повтор: только после проверки состояния доставки и курьера.
11. Аутентификация: оператор или сервисный компонент с правами.
12. Версия: `CourierAssignmentContract v1`.

# Протокол 3: изменение статуса

1. Инициатор: `ApiHost` или `CourierModule`.
2. Получатель: `DeliveryModule`.
3. Назначение: изменение статуса с проверкой допустимого перехода.
4. Транспорт: HTTP/JSON или внутренний вызов.
5. Точка входа: `PATCH /api/deliveries/{deliveryId}/status`.
6. Запрос: `{ "status": "InTransit" }`.
7. Ответ: предыдущий и новый статус.
8. Ошибки: `DELIVERY_NOT_FOUND`, `INVALID_STATUS_TRANSITION`, `DELIVERY_ALREADY_COMPLETED`, `UNAUTHORIZED_STATUS_CHANGE`.
9. Таймаут: до 3 секунд.
10. Повтор: идемпотентная обработка уже примененного перехода.
11. Аутентификация: авторизованный пользователь или назначенный курьер.
12. Версия: `DeliveryStatusContract v1`.
