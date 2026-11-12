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
| PostgreSQL | 16 | хранилище данных (docker-compose, CI) |
| Npgsql | 9.0.3 | доступ к PostgreSQL |
| Microsoft.AspNetCore.Mvc.Testing | 9.0.4 | HTTP-тесты приложения целиком |
| xUnit, Microsoft.NET.Test.Sdk, coverlet | 2.9.2 / 17.12.0 / 6.0.2 | автотесты и покрытие |
| Docker (по желанию) | любая | запуск без установки .NET |
| Git | любая | контроль версий |

Платёжные, картографические, SMS и другие внешние сервисы не используются. Клиент WPF входит в состав .NET SDK для Windows и не требует сторонних пакетов.
