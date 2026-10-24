namespace Modulix.Shared.Audit;

// Denetim kaydı (Y5) sözleşmeleri: /api/audit. Hepsi core.audit.read ister; kayıtlar yalnızca okunur.

public enum AuditOperation
{
    Created = 1,
    Updated = 2,
    Deleted = 3,
    Exported = 4,
    SignedIn = 5,
    SignInFailed = 6,
    LockedOut = 7,
    PasswordReset = 8,
    InvitationAccepted = 9,
    PasswordChanged = 10,
    PasswordResetRequested = 11,
    SessionsClosed = 12,
}

public enum AuditSource
{
    Api = 1,
    BackgroundJob = 2,
    Import = 3,
    AiTool = 4,
}

/// <summary>Değişen alan; değerler gösterime hazırdır. Değerler gizliyse ikisi de boştur.</summary>
public sealed record AuditFieldChangeDto(string Field, string Label, string? Old, string? New);

/// <summary>
/// Denetim kaydının bir satırı. <see cref="ValuesHidden"/>: okurun bu kayıt türünü görüntüleme izni yok; değişen
/// alanların adları görünür, değerleri ve kaydın adı gizlidir.
/// </summary>
public sealed record AuditEntryDto(
    Guid Id,
    DateTimeOffset OccurredAt,
    Guid UserId,
    string UserName,
    string EntityKey,
    string EntityName,
    Guid RecordId,
    string? RecordTitle,
    AuditOperation Action,
    AuditSource Source,
    bool ValuesHidden,
    IReadOnlyList<AuditFieldChangeDto> Changes);

public sealed record AuditPageResponse(IReadOnlyList<AuditEntryDto> Items, int Page, int PageSize, int TotalCount);

public sealed record AuditActorDto(Guid UserId, string Name);

public sealed record AuditEntityTypeDto(string Key, string Name);

/// <summary>Filtre seçenekleri: kayıtta geçen kişiler ve kayıt türleri.</summary>
public sealed record AuditFiltersResponse(IReadOnlyList<AuditActorDto> Users, IReadOnlyList<AuditEntityTypeDto> EntityTypes);

public static class AuditText
{
    /// <summary>Değeri gizli alanlarda gösterilen metin.</summary>
    public const string Hidden = "gizli";

    public static string Of(AuditOperation action) => action switch
    {
        AuditOperation.Created => "Oluşturdu",
        AuditOperation.Updated => "Değiştirdi",
        AuditOperation.Deleted => "Sildi",
        AuditOperation.Exported => "Dışa aktardı",
        AuditOperation.SignedIn => "Giriş yaptı",
        AuditOperation.SignInFailed => "Giriş başarısız",
        AuditOperation.LockedOut => "Hesap kilitlendi",
        AuditOperation.PasswordReset => "Şifresini sıfırladı",
        AuditOperation.InvitationAccepted => "Daveti kabul etti",
        AuditOperation.PasswordChanged => "Şifresini değiştirdi",
        AuditOperation.PasswordResetRequested => "Şifre sıfırlama bağlantısı istedi",
        AuditOperation.SessionsClosed => "Oturum kapattı",
        _ => action.ToString(),
    };

    public static string Of(AuditSource source) => source switch
    {
        AuditSource.Api => "Arayüz",
        AuditSource.BackgroundJob => "Arka plan işi",
        AuditSource.Import => "İçe aktarma",
        AuditSource.AiTool => "AI",
        _ => source.ToString(),
    };
}
