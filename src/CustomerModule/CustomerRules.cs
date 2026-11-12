namespace CustomerModule;

/// <summary>Правила данных клиента и адреса доставки.</summary>
public static class CustomerRules
{
    public static string? ValidateName(string? name) =>
        string.IsNullOrWhiteSpace(name) || name.Trim().Length > 100 ? "INVALID_NAME" : null;

    public const int MaxAddressPartLength = 100;
    public const int MaxApartmentLength = 20;
    public const int MaxLabelLength = 50;
    public const int MaxPhoneLength = 32;
    public const int MaxCommentLength = 500;

    public static string? ValidateAddress(string? city, string? street, string? house, string? apartment = null, string? label = null) =>
        string.IsNullOrWhiteSpace(city) || string.IsNullOrWhiteSpace(street) || string.IsNullOrWhiteSpace(house)
        || TooLong(city, MaxAddressPartLength) || TooLong(street, MaxAddressPartLength) || TooLong(house, MaxAddressPartLength)
        || TooLong(apartment, MaxApartmentLength) || TooLong(label, MaxLabelLength)
            ? "INVALID_ADDRESS" : null;

    /// <summary>Телефон необязателен; если указан — до 32 символов: цифры, пробел, «+», «-», скобки.</summary>
    public static string? ValidatePhone(string? phone) =>
        string.IsNullOrWhiteSpace(phone) || phone.Trim().Length <= MaxPhoneLength
            && System.Text.RegularExpressions.Regex.IsMatch(phone.Trim(), @"^\+?[0-9 ()\-]{3,}$") && phone.Any(char.IsDigit)
            ? null : "INVALID_PHONE";

    /// <summary>Комментарий к заказу и причина неудачи доставки — не длиннее 500 символов.</summary>
    public static string? ValidateComment(string? comment) => TooLong(comment, MaxCommentLength) ? "INVALID_COMMENT" : null;

    private static bool TooLong(string? value, int max) => value is not null && value.Trim().Length > max;

    public static string FormatAddress(string city, string street, string house, string? apartment) =>
        $"г. {city.Trim()}, ул. {street.Trim()}, д. {house.Trim()}" +
        (string.IsNullOrWhiteSpace(apartment) ? "" : $", кв. {apartment.Trim()}");
}
