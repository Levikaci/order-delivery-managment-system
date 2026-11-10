namespace ApiHost.Db;

/// <summary>Список миграций. Применённые миграции не редактируются: любое изменение схемы — новая версия.</summary>
public static class Migrations
{
    public static readonly IReadOnlyList<Migration> All = new List<Migration>
    {
        new(1, "initial_schema", @"
CREATE TABLE customers(id uuid PRIMARY KEY, name text NOT NULL, phone text NOT NULL DEFAULT '');
CREATE TABLE customer_addresses(
  id uuid PRIMARY KEY, customer_id uuid NOT NULL REFERENCES customers(id) ON DELETE CASCADE,
  label text NOT NULL DEFAULT '', city text NOT NULL, street text NOT NULL, house text NOT NULL, apartment text NULL);
CREATE TABLE couriers(
  id uuid PRIMARY KEY, name text NOT NULL, phone text NOT NULL DEFAULT '',
  state text NOT NULL DEFAULT 'Available' CHECK (state IN ('Available','Busy','Offline','Blocked')));
CREATE TABLE products(
  id uuid PRIMARY KEY, name text NOT NULL, price_kopecks bigint NOT NULL CHECK (price_kopecks >= 0));
CREATE TABLE users(
  id uuid PRIMARY KEY, login text NOT NULL, salt bytea NOT NULL, hash bytea NOT NULL,
  role text NOT NULL CHECK (role IN ('client','operator','courier')), ref_id uuid NULL);
CREATE UNIQUE INDEX ux_users_login ON users(lower(login));
CREATE TABLE orders(
  id uuid PRIMARY KEY, customer_id uuid NOT NULL REFERENCES customers(id),
  address text NOT NULL, comment text NULL, status text NOT NULL, created_at timestamptz NOT NULL);
CREATE TABLE order_items(
  id bigserial PRIMARY KEY, order_id uuid NOT NULL REFERENCES orders(id) ON DELETE CASCADE,
  product_id uuid NOT NULL, name text NOT NULL,
  price_kopecks bigint NOT NULL CHECK (price_kopecks >= 0), quantity int NOT NULL CHECK (quantity BETWEEN 1 AND 99));
CREATE TABLE deliveries(
  id uuid PRIMARY KEY, order_id uuid NOT NULL UNIQUE REFERENCES orders(id),
  courier_id uuid NULL REFERENCES couriers(id), status text NOT NULL, created_at timestamptz NOT NULL,
  started_at timestamptz NULL, finished_at timestamptz NULL, failure_reason text NULL, confirmed_at timestamptz NULL);
CREATE TABLE status_history(
  id bigserial PRIMARY KEY, delivery_id uuid NOT NULL REFERENCES deliveries(id),
  status text NOT NULL, at timestamptz NOT NULL, comment text NULL);
CREATE TABLE order_history(
  id bigserial PRIMARY KEY, order_id uuid NOT NULL REFERENCES orders(id),
  status text NOT NULL, at timestamptz NOT NULL, comment text NULL);
CREATE INDEX ix_deliveries_status ON deliveries(status);
CREATE INDEX ix_deliveries_courier ON deliveries(courier_id);
CREATE INDEX ix_orders_customer ON orders(customer_id);
CREATE INDEX ix_history_delivery ON status_history(delivery_id);
CREATE INDEX ix_ohistory_order ON order_history(order_id);"),

        new(2, "order_request_id", @"
-- идемпотентное создание заказа: повтор запроса с тем же requestId возвращает уже созданный заказ
ALTER TABLE orders ADD COLUMN request_id uuid NULL;
CREATE UNIQUE INDEX ux_orders_request ON orders(customer_id, request_id) WHERE request_id IS NOT NULL;"),

        new(3, "order_changes_and_actors", @"
-- журнал изменений заказа: что, когда, кем и как изменено (users.id без FK, чтобы история переживала удаление учётной записи)
CREATE TABLE order_changes(
  id bigserial PRIMARY KEY, order_id uuid NOT NULL REFERENCES orders(id),
  at timestamptz NOT NULL, actor_id uuid NULL, actor_name text NULL,
  field text NOT NULL CHECK (field IN ('Items','Address','Comment')),
  old_value text NULL, new_value text NULL);
CREATE INDEX ix_order_changes_order ON order_changes(order_id);
ALTER TABLE status_history ADD COLUMN actor_id uuid NULL;
ALTER TABLE order_history ADD COLUMN actor_id uuid NULL;"),
    };
}
