using Modulix.Core.Contracts.Records;
using Modulix.Modules.Logistics.Domain;

namespace Modulix.Modules.Logistics.Records;

/// <summary>
/// Tutar + para birimi alanlarının ortak kuralı (ADR-0038 K11, dilim 7a: navlun ve hakediş). İki alan birlikte anlamlıdır: tutar varsa para birimi
/// üç harfli kod olmalı, para birimi varsa tutar girilmelidir. Tutar 0 ile en çok 9.999.999.999.999.999,99 arasında, en çok iki ondalıklıdır.
/// </summary>
internal static class MoneyRules
{
    public static RecordField<T> Amount<T>(string name, Func<T, decimal?> get, Action<T, decimal?> set) =>
        Field.Decimal(name, get, set, 2, 0, LogisticsEntity.MaxAmount);

    public static RecordField<T> Currency<T>(string name, Func<T, string?> get, Action<T, string?> set) =>
        Field.Text(name, LogisticsEntity.CurrencyLength, get, set, upperCase: true);

    /// <summary>Tutar ve para biriminin birlikte tutarlı olup olmadığını denetler; hataları <paramref name="errors"/>'a ekler.</summary>
    public static void Validate(decimal? amount, string? currency, string amountField, string currencyField, List<RecordFieldError> errors)
    {
        if (amount is not null && currency is null)
        {
            errors.Add(new RecordFieldError(currencyField, "Tutar girildiğinde para birimi seçilmelidir."));
        }
        else if (amount is null && currency is not null)
        {
            errors.Add(new RecordFieldError(amountField, "Para birimi seçildiğinde tutar girilmelidir."));
        }
        else if (currency is not null && (currency.Length != LogisticsEntity.CurrencyLength || !currency.All(char.IsAsciiLetterUpper)))
        {
            errors.Add(new RecordFieldError(currencyField, "Üç harfli para birimi kodu girin (ör. TRY, EUR, USD)."));
        }
    }
}
