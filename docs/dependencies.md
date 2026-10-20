# Зависимости

## Внутренние

| Источник | Получатель | Назначение |
|---|---|---|
| `ApiHost` | `OrderModule` | Работа с заказами |
| `ApiHost` | `DeliveryModule` | Работа с доставками |
| `ApiHost` | `CourierModule` | Работа с курьерами |
| `ApiHost` | `CustomerModule` | Работа с клиентами |
| `OrderModule` | `CustomerModule` | Проверка клиента и адреса |
| `OrderModule` | `DeliveryModule` | Создание доставки |
| `DeliveryModule` | `CourierModule` | Поиск и назначение курьера |

## Внешние

Планируются:

- .NET SDK 9;
- ASP.NET Core;
- СУБД;
- Git;
- Swagger UI или Postman для проверки API.

На текущем этапе внешние платежные, картографические, SMS и другие сервисы не используются.
