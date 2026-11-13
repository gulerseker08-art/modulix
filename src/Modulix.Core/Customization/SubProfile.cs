using System.Text.Json;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Modules;

namespace Modulix.Core.Customization;

public enum SubProfileStatus
{
    /// <summary>Hazırlanıyor: seçenekleri firmaya uygulanmamıştır.</summary>
    Draft = 0,

    /// <summary>Etkin: seçenekleri firmaya uygulanır; sonradan eklenen seçenek hemen uygulanır.</summary>
    Active = 1,

    /// <summary>Pasif: uygulanan alanlar ve liste değerleri firmada kalır (ADR-0041 K5); yeni seçenek uygulanmaz.</summary>
    Inactive = 2,
}

public enum SubProfileOptionKind
{
    /// <summary>Bir kayıt türüne özel alan ekler.</summary>
    Field = 0,

    /// <summary>Bir seçim listesine değer ekler.</summary>
    ListValue = 1,
}

/// <summary>
/// Firma alt profili (ADR-0041): sektör profilinin altında firmanın kendi tanımladığı alan ve liste değeri paketi. Tek seviyedir (alt profilin
/// altında alt profil yoktur); bir faaliyet koduna bağlanabilir ama bağlanması zorunlu değildir. Silinmez, pasife alınır. Tenant verisidir;
/// Row-Level Security ile korunur.
/// </summary>
public sealed class SubProfile : ITenantEntity, ICompanyShared
{
    public const int CodeMaxLength = 50;
    public const int NameMaxLength = 100;
    public const int DescriptionMaxLength = 500;

    private SubProfile()
    {
        // EF Core için
    }

    public SubProfile(string code, string name, string? description, string? activityCode)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > CodeMaxLength || !code.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '_'))
        {
            throw new ArgumentException("Kod küçük harf, rakam, '-' ve '_' içerebilir (en çok 50 karakter).", "code");
        }

        Id = Guid.CreateVersion7();
        Code = code;
        Status = SubProfileStatus.Draft;
        Revision = 1;
        CreatedAt = UpdatedAt = DateTimeOffset.UtcNow;
        Edit(name, description, activityCode);
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; set; }

    /// <summary>Firma içinde benzersiz ve değişmez; ad değişse de kod kalır.</summary>
    public string Code { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    /// <summary>Bağlı faaliyet düğümünün kodu (platform kataloğu); boş olabilir. Katalogdaki düğüm kalkarsa alt profil çalışmaya devam eder.</summary>
    public string? ActivityCode { get; private set; }

    public SubProfileStatus Status { get; private set; }

    /// <summary>Eşzamanlı düzenlemeyi koruyan iyimser kilit.</summary>
    public long Revision { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>İlk etkinleştirme zamanı.</summary>
    public DateTimeOffset? ActivatedAt { get; private set; }

    public void Edit(string name, string? description, string? activityCode)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > NameMaxLength)
        {
            throw new ArgumentException($"Ad zorunludur ve en çok {NameMaxLength} karakter olabilir.", "name");
        }

        if (description is { Length: > DescriptionMaxLength })
        {
            throw new ArgumentException($"Açıklama en çok {DescriptionMaxLength} karakter olabilir.", "description");
        }

        Name = name.Trim();
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        ActivityCode = string.IsNullOrWhiteSpace(activityCode) ? null : activityCode.Trim();
        Touch();
    }

    public void SetStatus(SubProfileStatus status)
    {
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentException("Geçersiz durum.", "status");
        }

        Status = status;
        if (status == SubProfileStatus.Active)
        {
            ActivatedAt ??= DateTimeOffset.UtcNow;
        }

        Touch();
    }

    /// <summary>Seçenek eklenip çıkarılınca da sürüm artar: ekranı açık tutan başka kullanıcının eski görünümüyle yazması reddedilir.</summary>
    public void Touch()
    {
        Revision++;
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}

/// <summary>
/// Alt profilin bir seçeneği: bir kayda alan ya da bir listeye değer ekler (kural seçeneği sonraki dilimdedir). Uygulandıktan sonra
/// değiştirilemez ve kaldırılamaz: firmanın alanı artık kendisinindir, düzeltme Özel alanlar ve Listeler sayfasındandır.
/// </summary>
public sealed class SubProfileOption : ITenantEntity, ICompanyShared
{
    public const int TargetMaxLength = 100;
    public const int LabelMaxLength = 100;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private SubProfileOption()
    {
        // EF Core için
    }

    public SubProfileOption(Guid subProfileId, int sortOrder)
    {
        Id = Guid.CreateVersion7();
        SubProfileId = subProfileId;
        SortOrder = sortOrder;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; set; }

    public Guid SubProfileId { get; private set; }

    public SubProfileOptionKind Kind { get; set; }

    public int SortOrder { get; set; }

    /// <summary>Alan için kayıt türü anahtarı (<c>crm.opportunity</c>), liste değeri için liste anahtarı.</summary>
    public string TargetKey { get; set; } = string.Empty;

    /// <summary>Alan adı ya da liste değerinin adı.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Yalnızca alan seçeneğinde.</summary>
    public CustomFieldType? FieldType { get; set; }

    public bool Required { get; set; }

    public bool ShowInList { get; set; }

    /// <summary>Seçim tipli alanın seçenek adları (JSON dizisi).</summary>
    public string ChoicesJson { get; private set; } = "[]";

    /// <summary>Yalnızca kategorili listenin değerinde.</summary>
    public string? Category { get; set; }

    /// <summary>Firmaya uygulandığı zaman (aynı adlı alan/değer zaten varsa o gün). Boşsa uygulanmamıştır.</summary>
    public DateTimeOffset? AppliedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public IReadOnlyList<string> Choices
    {
        get => JsonSerializer.Deserialize<List<string>>(ChoicesJson, Json) ?? [];
        set => ChoicesJson = JsonSerializer.Serialize(value, Json);
    }

    public bool IsApplied => AppliedAt is not null;

    public void MarkApplied() => AppliedAt ??= DateTimeOffset.UtcNow;
}

/// <summary>Alt profil girdisi. <see cref="Code"/> yalnızca oluştururken kullanılır; boşsa addan üretilir.</summary>
public sealed record SubProfileInput(string Name, string? Description, string? ActivityCode, string? Code = null);

public sealed record SubProfileOptionInput(
    SubProfileOptionKind Kind,
    string TargetKey,
    string Label,
    CustomFieldType? FieldType = null,
    bool Required = false,
    bool ShowInList = false,
    IReadOnlyList<string>? Choices = null,
    string? Category = null);

public sealed record SubProfileOptionView(
    Guid Id,
    SubProfileOptionKind Kind,
    string TargetKey,
    string Label,
    CustomFieldType? FieldType,
    bool Required,
    bool ShowInList,
    IReadOnlyList<string> Choices,
    string? Category,
    DateTimeOffset? AppliedAt,
    int SortOrder);

/// <summary>Listede <see cref="Options"/> boştur (yalnızca <see cref="OptionCount"/>); tek alt profil okunurken dolar.</summary>
public sealed record SubProfileView(
    Guid Id,
    string Code,
    string Name,
    string? Description,
    string? ActivityCode,
    string? ActivityName,
    SubProfileStatus Status,
    long Revision,
    int OptionCount,
    int PendingCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ActivatedAt,
    IReadOnlyList<SubProfileOptionView> Options);

/// <summary>Bilinmeyen alt profil ya da seçenek (404).</summary>
public sealed class SubProfileNotFoundException(string message) : Exception(message);

/// <summary>Eşzamanlı düzenleme ya da durumu izin vermeyen işlem (409).</summary>
public sealed class SubProfileConflictException(string message) : Exception(message);

/// <summary>
/// Firma alt profilleri (ADR-0041), aktif tenant için. Yazma işlemleri <c>core.customization.manage</c> ister (API denetler). Alanlar ve liste
/// değerleri mevcut özel alan ve liste servisleriyle eklenir (doğrulama ve denetim onlarındır); alt profilin kendisi <c>core.sub-profile</c>
/// olarak denetime yazılır. Geçersiz girdi <see cref="ArgumentException"/>.
/// </summary>
public interface ISubProfileService
{
    Task<IReadOnlyList<SubProfileView>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Alt profil yoksa null.</summary>
    Task<SubProfileView?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<SubProfileView> CreateAsync(SubProfileInput input, CancellationToken cancellationToken);

    /// <summary>Alt profil yoksa null. Kod değişmez. <paramref name="revision"/> uyuşmazsa <see cref="SubProfileConflictException"/>.</summary>
    Task<SubProfileView?> UpdateAsync(Guid id, SubProfileInput input, long revision, CancellationToken cancellationToken);

    /// <summary>
    /// Durumu değiştirir. Etkinleştirme uygulanmamış seçenekleri firmaya ekler (tek işlem, yalnızca ekler); kapalı modülün seçenekleri
    /// uygulanmadan kalır ve modül açılınca yeniden etkinleştirmeyle (<see cref="ApplyPendingAsync"/>) uygulanır. Alt profil yoksa null.
    /// </summary>
    Task<SubProfileView?> SetStatusAsync(Guid id, SubProfileStatus status, long revision, CancellationToken cancellationToken);

    /// <summary>Yazmadan, etkinleştirmenin ya da yeniden uygulamanın ekleyeceklerini gösterir. Alt profil yoksa null.</summary>
    Task<ProfilePlan?> PreviewApplyAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Etkin alt profilin uygulanmamış seçeneklerini yeniden uygular (ör. modül sonradan açıldı). Alt profil yoksa null.</summary>
    Task<SubProfileView?> ApplyPendingAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Seçenek ekler. Etkin alt profilde hemen uygulanır. Alt profil yoksa null.</summary>
    Task<SubProfileView?> AddOptionAsync(Guid id, SubProfileOptionInput input, CancellationToken cancellationToken);

    /// <summary>Yalnızca uygulanmamış seçenek değişir. Alt profil ya da seçenek yoksa null.</summary>
    Task<SubProfileView?> UpdateOptionAsync(Guid id, Guid optionId, SubProfileOptionInput input, CancellationToken cancellationToken);

    /// <summary>Yalnızca uygulanmamış seçenek kaldırılır. Alt profil ya da seçenek yoksa null.</summary>
    Task<SubProfileView?> RemoveOptionAsync(Guid id, Guid optionId, CancellationToken cancellationToken);
}
