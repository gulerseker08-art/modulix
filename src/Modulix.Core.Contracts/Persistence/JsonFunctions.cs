namespace Modulix.Core.Contracts.Persistence;

/// <summary>
/// Sorgularda JSON kolonundan değer okuma (Y9a, özel alan filtreleri). Yalnızca LINQ sorgusunda kullanılır; veritabanı
/// karşılığına çevrilir (SQL Server: <c>JSON_VALUE</c>). Doğrudan çağrılırsa hata verir.
/// </summary>
public static class JsonFunctions
{
    /// <param name="json">JSON metni (kolon).</param>
    /// <param name="path">JSON yolu, ör. <c>$.arac_tipi</c>.</param>
    public static string? Value(string? json, string path) =>
        throw new NotSupportedException("JsonFunctions.Value yalnızca veritabanı sorgusunda kullanılabilir.");
}
