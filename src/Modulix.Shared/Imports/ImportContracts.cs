namespace Modulix.Shared.Imports;

// İçe aktarma günlüğü (ADR-0033, Dilim 1). Satır değeri içermez; yalnızca iş özeti ve satır hatası (satır no, sütun, kod, ileti).
public sealed record ImportRunDto(
    Guid Id,
    Guid UserId,
    string UserName,
    string EntityKey,
    string EntityName,
    string FileName,
    long FileSize,
    string Mode,
    string Policy,
    string Status,
    int TotalRows,
    int CreatedCount,
    int UpdatedCount,
    int SkippedCount,
    int FailedCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? Note);

public sealed record ImportRunPageDto(IReadOnlyList<ImportRunDto> Items, int Page, int PageSize, int TotalCount);

public sealed record ImportIssueDto(int RowNo, string? ColumnKey, string Code, string Severity, string Message);

public sealed record ImportIssuePageDto(IReadOnlyList<ImportIssueDto> Items, int Page, int PageSize, int TotalCount);

public sealed record ImportIssueCodeCountDto(string Code, int Count);

public sealed record ImportRunDetailDto(ImportRunDto Run, IReadOnlyList<ImportIssueCodeCountDto> IssueCodes, ImportIssuePageDto Issues);

/// <summary>Ekran metinleri: durum, mod, politika ve hata kodu adları (arayüzde İngilizce teknik ad görünmez).</summary>
public static class ImportText
{
    public static string Status(string status) => status switch
    {
        "Pending" => "Bekliyor",
        "Running" => "Çalışıyor",
        "Succeeded" => "Başarılı",
        "PartiallySucceeded" => "Kısmen başarılı",
        "Failed" => "Başarısız",
        "Cancelled" => "İptal edildi",
        "RolledBack" => "Geri alındı",
        _ => status,
    };

    /// <summary>Rozet tonu: <c>badge stage-{ton}</c> (tokenlardan gelen tonlar; diğer ekranlarla aynı sınıflar).</summary>
    public static string StatusBadge(string status) => status switch
    {
        "Succeeded" => "won",
        "PartiallySucceeded" => "onhold",
        "Failed" => "lost",
        "Cancelled" or "RolledBack" => "cancelled",
        _ => "open",
    };

    public static string SeverityBadge(string severity) => severity == "Error" ? "lost" : "onhold";

    public static string Mode(string mode) => mode switch
    {
        "DryRun" => "Kuru çalışma",
        "Apply" => "Uygulama",
        _ => mode,
    };

    public static string Policy(string policy) => policy switch
    {
        "SkipInvalidRows" => "Hatasız satırları al",
        "AllOrNothing" => "Hepsi ya da hiç",
        _ => policy,
    };

    public static string Severity(string severity) => severity switch
    {
        "Error" => "Hata",
        "Warning" => "Uyarı",
        _ => severity,
    };

    public static string Code(string code) => code switch
    {
        "required-missing" => "Zorunlu alan boş",
        "format-invalid" => "Biçim geçersiz",
        "lookup-unresolved" => "Liste değeri bulunamadı",
        "duplicate-exact" => "Aynı kayıt var",
        "duplicate-possible" => "Benzer kayıt olabilir",
        "custom-field-invalid" => "Özel alan geçersiz",
        "permission-denied" => "Yetki yok",
        "business-rule" => "İş kuralı",
        "file-invalid" => "Dosya geçersiz",
        "system-error" => "Sistem hatası",
        _ => code,
    };

    public static IReadOnlyList<string> Statuses { get; } =
        ["Pending", "Running", "Succeeded", "PartiallySucceeded", "Failed", "Cancelled", "RolledBack"];

    public static IReadOnlyList<string> Codes { get; } =
    [
        "required-missing", "format-invalid", "lookup-unresolved", "duplicate-exact", "duplicate-possible",
        "custom-field-invalid", "permission-denied", "business-rule", "file-invalid", "system-error",
    ];
}
