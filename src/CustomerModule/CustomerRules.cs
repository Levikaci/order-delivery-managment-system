namespace CustomerModule;

/// <summary>Правила данных клиента и адреса доставки.</summary>
public static class CustomerRules
{
    public static string? ValidateName(string? name) =>
        string.IsNullOrWhiteSpace(name) || name.Trim().Length > 100 ? "INVALID_NAME" : null;

    public static string? ValidateAddress(string? city, string? street, string? house) =>
        string.IsNullOrWhiteSpace(city) || string.IsNullOrWhiteSpace(street) || string.IsNullOrWhiteSpace(house)
            ? "INVALID_ADDRESS" : null;

    public static string FormatAddress(string city, string street, string house, string? apartment) =>
        $"г. {city.Trim()}, ул. {street.Trim()}, д. {house.Trim()}" +
        (string.IsNullOrWhiteSpace(apartment) ? "" : $", кв. {apartment.Trim()}");
}
