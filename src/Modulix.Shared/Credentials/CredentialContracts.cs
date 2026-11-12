namespace Modulix.Shared.Credentials;

/// <summary>
/// Süreli belge (ADR-0038 K3) anahtarları (arayüz için); sunucudaki <c>CredentialEntities</c> ile aynıdır (birim testle denetlenir). Belgeler genel
/// kayıt uçlarından (<c>/api/records/core.credential</c>) okunur ve yazılır; erişim sahip kaydın (araç, sürücü) erişimidir.
/// </summary>
public static class CredentialAccess
{
    public const string EntityKey = "core.credential";

    public const string TypeList = "core.credential-type";

    /// <summary>Belge türü kategorileri: sahip kayıt türünün kategorisi.</summary>
    public const string Vehicle = "vehicle";
    public const string Driver = "driver";

    public const string Active = "active";
    public const string Revoked = "revoked";

    /// <summary>Bitişine bu kadar gün kalan belge "Bitiyor" rozetiyle gösterilir (uyarı kuralının eşiği firmanın kuralındadır).</summary>
    public const int ExpiringDays = 30;

    public static IReadOnlyList<(string Key, string Label)> Statuses { get; } =
    [
        (Active, "Geçerli"),
        (Revoked, "İptal edildi"),
    ];
}
