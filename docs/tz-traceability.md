# Соответствие ТЗ

Актуально для v2.0.0. Тесты — классы в `tests/` (см. [testing.md](testing.md)). Первоначальные документы этапов не менялись.

| Требование ТЗ | Реализация | Подтверждающие тесты |
|---|---|---|
| Серверная часть на ASP.NET Core, клиентское приложение | `src/ApiHost` (REST API), `src/WpfClient` (WPF) | `ApiScenarioTests` (сервер целиком); клиент проверяется сборкой |
| Модули Order, Delivery, Courier, Customer | `src/OrderModule`, `DeliveryModule`, `CourierModule`, `CustomerModule` (`*Rules.cs`, `Money`), DTO и enum в `src/Contracts`; сценарии — `ApiHost/Store.cs` ([ADR-007](decisions/ADR-007-reliability-and-change-log.md)) | `RulesTests`, `MoneyTests` |
| Создание заказа с товарами и адресом | `POST /api/orders`, `Store.CreateOrder`, таблицы `orders`, `order_items`, `products` | `Scenario1_*`, `CreateOrder_Validation`, `OrderValidation_ReturnsSpecificCodes` |
| Сохранённые адреса клиента | `customer_addresses`, `/api/customers/{id}/addresses` | `Addresses_Crud_AndUseInOrder`, `SaveAddress_StoresInlineAddress` |
| Редактирование заказа | `PUT /api/orders/{id}` (Created/Assigned) | `UpdateOrder_AllowedBeforeAccept_ThenLocked`, `UpdateOrder_WithoutComment_KeepsComment_*` |
| Отмена заказа | отмена клиентом до передачи курьеру → доставка `Cancelled`, заказ `Cancelled` | `Cancel_SetsOrderCancelled_*`, `Client_CanCancelOnlyOwnOrder_BeforeHandover`, `Cancel_OverHttp_*` |
| Создание доставки | `POST /api/deliveries` (повтор — 409 `DELIVERY_ALREADY_EXISTS`) | `CreateDelivery_IsIdempotentByOrderId`, `RepeatedOperations_AreHarmless` |
| Назначение курьера: ручное и автоматическое | `POST /api/deliveries/{id}/assign`; авто при создании заказа | `Scenario1_*`, `Assign_Errors`, `ParallelAssign_SameDelivery_OnlyOneWins` |
| Нет свободного курьера (сценарий 2) | заказ сохраняется, доставка `Created`, назначение повторяется | `Scenario2_*`, `NoFreeCourier_Assign_Is409`, `CreateOrder_AutoAssignFails_*`, `ParallelOrders_OneFreeCourier_*` |
| Состояния курьера | Available, Busy, Offline, Blocked; освобождение по финальному статусу | `CourierStates_Rules`, `CourierState_CourierChangesOnlyOwn` |
| Статусы доставки и допустимые переходы | `DeliveryRules` | `DeliveryTransitions`, `Status_InvalidTransition_Completed_AndIdempotent`, `StatusRights_ByRole_OverHttp` |
| Статусы заказа | выводятся из доставки, `Failed → DeliveryFailed`, `Cancelled → Cancelled` ([ADR-006](decisions/ADR-006-order-and-delivery-statuses.md)) | `OrderStatusFollowsDelivery`, `FailedDelivery_*`, `FailedDelivery_OverHttp_*` |
| Причина неудачи | `Failed` требует причину (`FAILURE_REASON_REQUIRED`) | `Failed_RequiresReason_*`, `FailedWithoutReason_Is400` |
| Время начала и завершения | `started_at` (InTransit), `finished_at` (финальный статус) | `Scenario3_FullCycle_*` |
| История статусов (ADR-003) | `status_history`, `order_history`; с автором (`actor_id`) | `Scenario3_FullCycle_*`, `StatusHistory_RecordsActorId` |
| Журнал изменений заказа *(расширение ТЗ, ADR-007)* | `order_changes`, `GET /api/orders/{id}/changes`, секция в WPF | `ChangeLogTests`, `OrderEdit_IsVisibleInChangesEndpoint` |
| Единый формат ошибок | `{success, errorCode, error, message}`, включая 500/400 | `ForbiddenRoles_Get403_*`, `MalformedJson_*`, `DatabaseFailure_Returns500_*` |
| Роли и права | вход по логину/паролю, Bearer, проверка роли на каждом endpoint; оператор заказы не создаёт | `ApiRoleTests` (матрица 401/403, изоляция клиентов) |
| Деньги | `bigint` в копейках, `OrderModule.Money` ([ADR-005](decisions/ADR-005-money-in-kopecks.md)) | `MoneyTests`, `Money_RoundTripsExactly_OverHttp` |
| Хранение данных | PostgreSQL, версионированные миграции ([ADR-004](decisions/ADR-004-postgresql-and-migrations.md)) | `MigrationTests`, `Store_Restart_KeepsData*`, `DataSurvivesApplicationRestart` |
| Обновление существующей установки | `tools/SqliteImport` | `SqliteImportTests` |
| Идемпотентность и устойчивость к сбоям | `requestId`, транзакции, `FOR UPDATE` | `ReliabilityTests`, `PostOrders_WithSameRequestId_IsIdempotent` |
| Запуск | `docker compose up --build` или `dotnet run` | CI (`.github/workflows/ci.yml`) |
| Тесты | 171 автоматический тест | `dotnet test OrderDeliveryManagementSystem.sln` |

## Допустимые отклонения от ТЗ

- СУБД: ТЗ её не фиксирует; выбран PostgreSQL ([ADR-004](decisions/ADR-004-postgresql-and-migrations.md)).
- `IDeliveryService` и in-memory сервисы из первоначальных протоколов удалены как неиспользуемые ([ADR-007](decisions/ADR-007-reliability-and-change-log.md)).
- Журнал изменений заказа и `requestId` — расширения ТЗ с обоснованием в ADR-007; оба необязательны для клиентов API.
- Повторная доставка после `Failed` в рамках заказа не предусмотрена (ADR-006).
