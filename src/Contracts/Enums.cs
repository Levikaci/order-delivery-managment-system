namespace Contracts;

/// <summary>Статус доставки (DeliveryStatus из ТЗ).</summary>
public enum DeliveryStatus { Created, Assigned, Accepted, InTransit, Delivered, Failed, Cancelled }

/// <summary>Статус заказа (OrderStatus из ТЗ).</summary>
public enum OrderStatus { Created, Confirmed, Preparing, InDelivery, Delivered, Cancelled }

/// <summary>Состояние курьера (CourierModule из ТЗ).</summary>
public enum CourierState { Available, Busy, Offline, Blocked }
