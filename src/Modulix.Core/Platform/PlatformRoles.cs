namespace Modulix.Core.Platform;

/// <summary>
/// Platform ekibinin rolleri (ADR-0028 Dilim 5). Bayrak birleşimidir: bir kişi birden fazla rol taşıyabilir.
/// <see cref="Owner"/> bütün rolleri kapsar ve platform ekibini yönetir.
/// </summary>
[Flags]
public enum PlatformRole
{
    None = 0,

    /// <summary>Salt okunur izleme; olay yeniden deneme/atlama ve iki adımlı doğrulama sıfırlama.</summary>
    Support = 1,

    /// <summary>Firma aç/kapat/askıya al, modül hakkı, yönetici daveti, sektör profili.</summary>
    Operations = 2,

    /// <summary>Plan, abonelik, limit istisnası ve modül hakkı koşulları.</summary>
    Finance = 4,

    /// <summary>Hepsi ve platform ekibi yönetimi. Yalnızca komut satırıyla atanır.</summary>
    Owner = 8,
}

/// <summary>Platform hesabının rolleri ve iki adımlı doğrulama durumu (her istekte veritabanından okunur).</summary>
public sealed record PlatformAccess(PlatformRole Roles, bool TwoFactorEnabled);

public static class PlatformRoles
{
    /// <summary>Arayüzden atanabilen roller (Owner hariç).</summary>
    public const PlatformRole Assignable = PlatformRole.Support | PlatformRole.Operations | PlatformRole.Finance;

    /// <summary>
    /// Etkin rol: platform yöneticisi bayrağı olup rol kaydı boşsa kişi Owner sayılır; bu, rol modelinden önce atanmış hesapların
    /// yetkisini korur. Bayrak yoksa rol yoktur.
    /// </summary>
    public static PlatformRole Effective(bool isPlatformAdministrator, PlatformRole stored) =>
        !isPlatformAdministrator ? PlatformRole.None : stored == PlatformRole.None ? PlatformRole.Owner : stored;

    /// <summary>Gerekli rollerden en az biri varsa ya da kişi Owner ise <c>true</c>.</summary>
    public static bool Allows(PlatformRole effective, PlatformRole anyOf) =>
        effective.HasFlag(PlatformRole.Owner) || (effective & anyOf) != PlatformRole.None;

    public static string NameOf(PlatformRole role) => role switch
    {
        PlatformRole.Support => "Destek",
        PlatformRole.Operations => "Operasyon",
        PlatformRole.Finance => "Finans",
        PlatformRole.Owner => "Platform sahibi",
        _ => role.ToString(),
    };

    /// <summary>Bayrak birleşimini tek tek rollere ayırır (Owner varsa yalnızca Owner).</summary>
    public static IReadOnlyList<PlatformRole> Split(PlatformRole roles) =>
        roles.HasFlag(PlatformRole.Owner)
            ? [PlatformRole.Owner]
            : [.. new[] { PlatformRole.Support, PlatformRole.Operations, PlatformRole.Finance }.Where(r => roles.HasFlag(r))];
}

/// <summary>Platform ekibindeki bir hesap.</summary>
public sealed record PlatformTeamMember(Guid UserId, string DisplayName, string Email, PlatformRole Roles, bool TwoFactorEnabled);

/// <summary>
/// Platform ekibi yönetimi (ADR-0028 Dilim 5). Hesap hiç silinmez: ekipten çıkarmak yalnızca platform erişimini kapatır. Owner
/// ataması komut satırında kalır; burada yalnızca <see cref="PlatformRoles.Assignable"/> roller verilir ve Owner'ın rolü değiştirilemez.
/// Her değişiklik platform denetim kaydına yazılır. Hatalar: <see cref="UserAdministrationException"/> (bulunamadı, çakışma),
/// geçersiz rol için <see cref="ArgumentException"/> (ParamName <c>roles</c>).
/// </summary>
public interface IPlatformTeam
{
    Task<IReadOnlyList<PlatformTeamMember>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Kayıtlı bir hesabı e-postasıyla ekibe ekler ya da rollerini değiştirir. Hesap yoksa NotFound (hesap burada açılmaz).</summary>
    Task<PlatformTeamMember> SetRolesByEmailAsync(string email, PlatformRole roles, Guid actingUserId, CancellationToken cancellationToken);

    /// <summary>
    /// Ekip üyesinin rollerini değiştirir. <paramref name="roles"/> boşsa üyenin platform erişimi kapanır (hesap ve verisi kalır).
    /// Kendi rolünü ya da Owner'ın rolünü değiştirmek çakışmadır.
    /// </summary>
    Task<PlatformTeamMember> SetRolesAsync(Guid userId, PlatformRole roles, Guid actingUserId, CancellationToken cancellationToken);
}
