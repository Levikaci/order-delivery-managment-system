# ADR-006: Статусы заказа и доставки

**Статус:** принято (v2.0.0, issue #1)

## Проблема

Неудачная доставка (`Failed`) превращалась в статус заказа `Cancelled`, поэтому «отмена» и «не смогли доставить» были неразличимы.

## Решение

Добавлен `OrderStatus.DeliveryFailed`. Статус заказа выводится из статуса доставки (`DeliveryRules.ToOrderStatus`):

| Доставка | Заказ |
|---|---|
| Created | Created |
| Assigned | Confirmed |
| Accepted | Preparing |
| InTransit | InDelivery |
| Delivered | Delivered |
| Failed | **DeliveryFailed** |
| Cancelled | Cancelled |

Допустимые переходы доставки не менялись; `Delivered`, `Failed`, `Cancelled` — финальные. `Failed` требует причину. Повторная доставка после `Failed` в рамках того же заказа не предусмотрена ТЗ и не добавлялась: оформляется новый заказ.

## Последствия

WPF показывает статус заказа в карточке; импорт переводит прежние «Cancelled из-за Failed» в `DeliveryFailed`.
