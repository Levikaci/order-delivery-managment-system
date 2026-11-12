# Order Delivery Management System

Клиент-серверная система управления доставкой заказов: сервер на ASP.NET Core 9 и PostgreSQL, клиентское приложение на WPF. Клиент оформляет заказ, оператор назначает курьера и контролирует выполнение, курьер ведёт доставку по статусам. Все изменения сохраняются в истории.

## Содержание
- [Возможности](#что-умеет)
- [Архитектура](#как-устроено)
- [База данных](#база-данных)
- [Зависимости](#зависимости)
- [Запуск](#запуск)
- [Интерфейс](#интерфейс)
- [API](#api)

## Что умеет
Вход по логину и паролю, три роли:

| Роль | Логин / пароль (демо) | Что может |
|---|---|---|
| Клиент | `ivan` / `ivan`, `maria` / `maria` | создаёт заказ из товаров (количество, итог), выбирает или сохраняет адрес, редактирует и отменяет заказ до передачи курьеру, подтверждает получение |
| Оператор | `operator` / `operator` | видит все заказы, назначает курьера (вручную или автоматически), меняет статусы, ведёт справочники. Заказы **не создаёт** |
| Курьер | `alexey`, `dmitry`, `olga` (пароль = логин) | свой маршрутный лист, статусы «принял / в пути / доставил / не удалось» (с причиной), переключатель онлайн/офлайн |

- Заказ и доставка имеют **раздельные статусы**. Доставка: `Created → Assigned → Accepted → InTransit → Delivered`, а также `Failed` (обязательна причина) и `Cancelled`. Неудачную доставку оператор может повторить (`retry`): она возвращается в `Created`, предыдущая попытка остаётся в истории. Заказ: `Created, Confirmed, Preparing, InDelivery, Delivered, Cancelled, DeliveryFailed` — выводится из статуса доставки: `Failed → DeliveryFailed` (доставка не удалась), `Cancelled → Cancelled` (заказ отменён). Подробнее — [ADR-006](docs/decisions/ADR-006-order-and-delivery-statuses.md).
- Состояния курьера: `Available, Busy, Offline, Blocked`. Занятого, офлайн и заблокированного назначить нельзя; после финального статуса курьер освобождается.
- `POST /api/orders` создаёт заказ + доставку и сразу подбирает свободного курьера с наименьшим числом доставок за последние 24 часа (при равенстве — по имени). Если свободных нет — доставка остаётся `Created`, оператор назначит позже.
- Фиксируются время начала и завершения доставки, история статусов доставки и заказа (с автором действия) и журнал изменений заказа: состав, адрес, комментарий (было → стало, кто и когда).
- Создание заказа атомарно; сбой автоназначения не отменяет заказ. Необязательный `requestId` в `POST /api/orders` делает повтор запроса идемпотентным.
- Деньги хранятся в копейках, наружу отдаются в рублях (`decimal`).
- Недопустимые переходы, повторное создание доставки и т. п. API отклоняет единым форматом ошибки `{success:false, errorCode, error, message}`.

Статусы доставки:

![Статусы](docs/img/statuses.png)

## Как устроено
- `src/ApiHost` — ASP.NET Core 9 minimal API, хранилище на PostgreSQL (`Store.cs`), миграции схемы (`Db/`), авторизация (`Auth.cs`).
- `src/Contracts` — DTO и перечисления; `OrderModule`, `DeliveryModule`, `CourierModule`, `CustomerModule` — бизнес-правила каждого модуля (`*Rules.cs`), общение через контракты.
- `src/WpfClient` — клиентское приложение WPF (вход, рабочие места по ролям).
- `tests/` — 167 тестов (правила модулей, сценарии, миграции, HTTP-API и роли на реальном PostgreSQL).
- `docs/` — этапы курсового, ADR, архитектура, тесты.

![Архитектура](docs/img/architecture.png)

## База данных
PostgreSQL 16. Строка подключения — `ConnectionStrings:Default` (по умолчанию `Host=localhost;Port=5432;Database=delivery;Username=delivery;Password=delivery`; в Docker — переменная `ConnectionStrings__Default`). Схема создаётся и обновляется **версионированными миграциями** при старте (`src/ApiHost/Db/Migrations.cs`, учёт — таблица `schema_migrations`). Каждая миграция выполняется в транзакции; таблицы и данные автоматически не удаляются. Если схема неизвестна, создана более новой версией или миграция упала, приложение не стартует и пишет причину. На пустой базе по умолчанию добавляются демо-данные (см. «Боевой запуск»). В Docker данные лежат в volume `pgdata`.

![Схема БД (PostgreSQL)](docs/img/db-schema.png)

Текстовое описание схемы — в [docs/architecture.md](docs/architecture.md#схема-бд-миграции-00010004). Основные таблицы: `customers`, `customer_addresses`, `couriers`, `users` (пароли PBKDF2), `products` (`price_kopecks`), `orders`, `order_items`, `deliveries`, `status_history`, `order_history`, `order_changes`, `sessions`, `schema_migrations`.

### Обновление версии
Новая версия приложения при старте сама применяет недостающие миграции; уже применённые не повторяются, данные сохраняются. Перед обновлением рабочей базы сделайте резервную копию:
```bash
docker compose exec db pg_dump -U delivery -d delivery > backup.sql
```
Подробнее — [docs/architecture.md](docs/architecture.md#обновление-установки).

## Зависимости
- **.NET 9 SDK / ASP.NET Core 9** — сервер и API (minimal API).
- **PostgreSQL 16 + Npgsql** — хранение данных.
- **xUnit** (+ Test.Sdk, coverlet) — тесты.
- **WPF (.NET 9, Windows)** — клиентское приложение; сторонних пакетов не использует.
- **Docker** — по желанию, для запуска без .NET.

Связи между модулями и полная таблица — в [docs/dependencies.md](docs/dependencies.md).

## Запуск

### Docker
```bash
docker compose up --build
```
Поднимает PostgreSQL (`db`) и API (`app`) на http://localhost:8080. Пароль БД можно задать переменной `POSTGRES_PASSWORD`.

### Боевой запуск (без демо-данных)
Демо-аккаунты имеют предсказуемые пароли (`пароль = логин`), поэтому для рабочей установки их отключают: `Seed:Demo=false` (переменная `Seed__Demo=false`) и пароль первого оператора `Seed:OperatorPassword` (переменная `Seed__OperatorPassword`, не короче 8 символов). На пустой базе создаётся только пользователь `operator` с этим паролем, без клиентов, курьеров и товаров; если пароль не задан, сервер не стартует. В Docker: `SEED_DEMO=false OPERATOR_PASSWORD=... docker compose up -d`. Любой пользователь может сменить пароль через `POST /api/auth/password`.

### Локально
Требуется [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) и PostgreSQL (проще всего `docker compose up -d db`).
```bash
dotnet run --project src/ApiHost --no-launch-profile --urls http://localhost:8080
```
Либо `run.bat` (Windows) / `./run.sh` (Linux, macOS). В Visual Studio: Multiple startup projects — `ApiHost` (сервер) и `WpfClient` (клиент).

Приложение доступно на http://localhost:8080, проверка работоспособности — `GET /api/health`.

Схема развёртывания:

![Развёртывание](docs/img/deploy.png)

### WPF-клиент
Сначала запустите сервер, затем (Windows):
```bash
dotnet run --project src/WpfClient
```
или `run-client.bat`. В окне входа адрес сервера по умолчанию `http://localhost:8080`; если сервер запущен в Docker, он тот же.

### Тесты
Нужен PostgreSQL с правом `CREATE DATABASE` (например, `docker run -d -p 5432:5432 -e POSTGRES_PASSWORD=postgres postgres:16`); адрес — переменная `TEST_PG_ADMIN` (по умолчанию `postgres:postgres@localhost:5432`). Каждый тест работает в собственной временной базе.
```bash
dotnet restore OrderDeliveryManagementSystem.sln
dotnet build OrderDeliveryManagementSystem.sln
dotnet test OrderDeliveryManagementSystem.sln
```

## Интерфейс
Клиентское приложение — WPF (`src/WpfClient`, .NET 9, Windows), тёмная тема. Общается с сервером по REST API с Bearer-токеном. Окно входа принимает адрес сервера, логин и пароль; дальше интерфейс зависит от роли:

- **Оператор**: диспетчерская (все заказы, поиск, назначение курьера вручную или автоматически, смена статуса, подтверждение получения) и справочники клиентов, курьеров и товаров.
- **Клиент**: «Мои заказы», создание заказа из товаров с количеством, выбор или сохранение адреса, изменение и отмена заказа до принятия курьером, подтверждение получения.
- **Курьер**: маршрутный лист, кнопки «Принять», «В пути», «Доставлено», «Не удалось» (с причиной), переход на линию и обратно.

Скриншоты настоящего WPF-клиента (запущен с API на PostgreSQL):

![Вход](docs/img/wpf-login.png)

![Диспетчерская: заказы, статусы, курьеры](docs/img/wpf-dispatcher.png)

Неудачная доставка отображается как «Не удалась» (`DeliveryFailed`), а не как отмена заказа:

![Неудачная доставка](docs/img/wpf-delivery-failed.png)

Журнал изменений заказа (было → стало, кто и когда):

![Журнал изменений заказа](docs/img/wpf-order-changes.png)

Список обновляется автоматически каждые 8 секунд, справа показана карточка заказа с составом, статусом заказа, временем, историей статусов и журналом изменений.

## API
Все endpoints, кроме `health` и `login`, требуют `Authorization: Bearer <token>` и проверяют роль.

| Метод | Endpoint | Что делает |
|---|---|---|
| POST | `/api/auth/login`, `/api/auth/logout` | вход / выход (сессии хранятся в PostgreSQL и переживают перезапуск, 12 часов) |
| POST | `/api/auth/password` | смена своего пароля (`oldPassword`, `newPassword` ≥ 8 символов); остальные сессии пользователя завершаются |
| GET | `/api/auth/me`, `/api/state` | текущий пользователь; данные для UI (фильтруются по роли) |
| GET | `/api/health` | проверка живости |
| POST | `/api/orders` | создать заказ (+ доставка, автоназначение); необязательный `requestId` — идемпотентность |
| GET / PUT | `/api/orders/{id}` | получить / изменить заказ (пока Created/Assigned); не переданный `comment` не меняется |
| GET | `/api/orders/{id}/changes` | журнал изменений заказа |
| POST | `/api/deliveries` | создать доставку для заказа (повтор → 409 `DELIVERY_ALREADY_EXISTS`) |
| GET | `/api/deliveries?q=&status=&courierId=&customerId=` | поиск и фильтр |
| GET | `/api/deliveries/{id}` | доставка с историей |
| POST | `/api/deliveries/{id}/assign` | назначить курьера (пустой `courierId` — авто) |
| PATCH | `/api/deliveries/{id}/status` | сменить статус (`status`, `comment`; для `Failed` причина в `comment` обязательна) |
| POST | `/api/deliveries/{id}/confirm` | подтверждение получения (клиент — своего заказа, оператор) |
| POST | `/api/deliveries/{id}/retry` | оператор: повторить неудачную доставку (`Failed` → `Created`, курьер подбирается заново, история сохраняется) |
| GET/POST/PUT/DELETE | `/api/customers`, `/api/couriers`, `/api/products` | справочники |
| GET/POST | `/api/customers/{id}/addresses` | адреса клиента |
| GET/PUT/DELETE | `/api/addresses/{id}` | адрес |
| GET | `/api/couriers/available` | свободные курьеры |
| GET | `/api/couriers/{id}/deliveries` | доставки курьера |
| PATCH | `/api/couriers/{id}/state` | состояние курьера |

Подробнее — в [docs/contracts.md](docs/contracts.md); соответствие ТЗ — в [docs/tz-traceability.md](docs/tz-traceability.md).

Ошибки сервера (`STORAGE_ERROR`, `INTERNAL_ERROR`) и некорректный JSON (`INVALID_REQUEST`) тоже приходят в едином формате ошибок.

## Версии и история изменений
Версия — `Directory.Build.props` (сейчас 2.1.1, семантическое версионирование), список изменений — [CHANGELOG.md](CHANGELOG.md). Сообщения коммитов — [Conventional Commits](https://www.conventionalcommits.org/ru/v1.0.0/) (`feat`, `fix`, `refactor`, `test`, `docs`, `build`, `chore`).
