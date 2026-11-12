# Архитектура

Используется модульная архитектура с отдельным API-слоем.

![Архитектура](img/architecture.png)

## Основной сценарий

![Основной сценарий](img/scenario.png)

## Принцип зависимостей

Внешний API зависит от контрактов модулей. Модули не должны обращаться к внутренним классам друг друга напрямую. Обмен выполняется через интерфейсы и DTO.

Диаграмма статусов доставки:

![Статусы](img/statuses.png)

Реализация: каждый модуль (`OrderModule`, `DeliveryModule`, `CourierModule`, `CustomerModule`) содержит свои бизнес-правила (`*Rules.cs`), а `ApiHost` отвечает за HTTP, авторизацию по ролям и хранение в PostgreSQL (`Store.cs`, транзакции на создание заказа и смену статуса). Это упрощение под курсовой: отдельных процессов и баз у модулей нет.


---

## Актуальное состояние реализации (v2.0.0)

Первоначальный текст выше сохранён как основа. Фактическая реализация отличается следующим (решения — в [ADR-004…007](decisions/)):

- **Хранилище — PostgreSQL**. Схема создаётся и обновляется версионированными миграциями (`src/ApiHost/Db`), данные при обновлении не удаляются.
- **Бизнес-логика** выполняется в `ApiHost.Store`, который применяет статические правила модулей (`OrderRules`, `DeliveryRules`, `CourierRules`, `CustomerRules`, `Money`). Отдельных сервисов `OrderService`/`DeliveryService` нет (удалены как неиспользуемые). Межмодульные «протоколы» из [contracts.md](contracts.md) реализованы как операции `Store` и HTTP API.
- **Консистентность:** операции выполняются транзакциями, строки блокируются `SELECT ... FOR UPDATE`, поэтому API можно запускать в нескольких экземплярах.
- **Деньги** — целые копейки (`bigint`), наружу рубли (`decimal`).
- **Статусы:** заказ выводится из доставки; `Failed → DeliveryFailed`, `Cancelled → Cancelled`.

### Схема БД (миграции 0001–0004)

```text
customers(id, name, phone)                      customer_addresses(id, customer_id, label, city, street, house, apartment)
couriers(id, name, phone, state)                products(id, name, price_kopecks)
users(id, login, salt, hash, role, ref_id)      orders(id, customer_id, address, comment, status, created_at, request_id)
order_items(id, order_id, product_id, name, price_kopecks, quantity)
deliveries(id, order_id, courier_id, status, created_at, started_at, finished_at, failure_reason, confirmed_at)
status_history(id, delivery_id, status, at, comment, actor_id)   order_history(id, order_id, status, at, comment, actor_id)
order_changes(id, order_id, at, actor_id, actor_name, field, old_value, new_value)
sessions(token_hash, user_id, role, ref_id, name, expires_at)
schema_migrations(version, name, applied_at)
```

Диаграмма схемы (PostgreSQL, миграции 0001–0004):

![Схема БД](img/db-schema.png)

### Обновление установки

1. Сделайте резервную копию базы: `docker compose exec db pg_dump -U delivery -d delivery > backup.sql` (без Docker — `pg_dump` с той же строкой подключения).
2. Обновите приложение (`git pull`, затем `docker compose up -d --build` или новая сборка `ApiHost`).
3. При старте приложение применяет только недостающие миграции, каждую в своей транзакции; уже применённые версии и данные не затрагиваются. Если миграция завершилась ошибкой, она откатывается, приложение не стартует и пишет причину в журнал — база остаётся в прежней версии.
4. Восстановление из копии при необходимости: `docker compose exec -T db psql -U delivery -d delivery < backup.sql` (в пустую базу).
