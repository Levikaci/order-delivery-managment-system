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

| Что | Версия | Зачем |
|---|---|---|
| .NET SDK / ASP.NET Core | 9 | сборка и запуск сервера, minimal API |
| Microsoft.Data.Sqlite | 9.0.4 | доступ к SQLite (файл БД, без отдельного сервера) |
| Microsoft.Extensions.Logging | 10.0.12 | логирование в модулях |
| xUnit, Microsoft.NET.Test.Sdk, coverlet | 2.9.2 / 17.12.0 / 6.0.2 | автотесты и покрытие |
| Inter, JetBrains Mono | локально, OFL | шрифты интерфейса (`wwwroot/fonts`) |
| Docker (по желанию) | любая | запуск без установки .NET |
| Git | любая | контроль версий |

Платёжные, картографические, SMS и другие внешние сервисы не используются. Интерфейс без фреймворков и CDN, всё работает офлайн.
