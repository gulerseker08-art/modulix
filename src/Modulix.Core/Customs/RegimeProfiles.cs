using Modulix.Core.Contracts.Customs;

namespace Modulix.Core.Customs;

/// <summary>Profil sürümünün durumu: taslak → yayında → yerini yenisine bıraktı (geçmiş olarak kalır).</summary>
public enum RegimeProfileVersionState
{
    Draft = 0,
    Published = 1,
    Superseded = 2,
}

/// <summary>
/// Rejim profili (ADR-0032 K16): ülke/rejim/antrepo tipine göre değişen kuralların sürümlü paketi. Platform geneli kayıttır (RLS dışı, <c>TenantId</c>
/// yok); yalnızca platform yönetir. Profilin içeriği <see cref="RegimeProfileVersion"/>'dadır. Profil silinmez, pasife alınır
/// (tenant seçimleri ona başvurur).
/// </summary>
public sealed class RegimeProfile
{
    public const int CodeMaxLength = 50;
    public const int NameMaxLength = 100;

    private RegimeProfile()
    {
        // EF Core için
    }

    public RegimeProfile(string code, string name)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > CodeMaxLength || !code.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-'))
        {
            throw new ArgumentException($"Geçersiz profil kodu: '{code}' (küçük harf, rakam ve '-'; en çok {CodeMaxLength} karakter).", nameof(code));
        }

        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > NameMaxLength)
        {
            throw new ArgumentException($"Profil adı boş olamaz ve en çok {NameMaxLength} karakter olabilir.", nameof(name));
        }

        Id = Guid.CreateVersion7();
        Code = code;
        Name = name.Trim();
        IsActive = true;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; }

    public string Code { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public bool IsActive { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public void SetActive(bool active) => IsActive = active;
}

/// <summary>
/// Profilin bir sürümü. Profil başına en çok bir taslak ve bir yayın vardır. Yayınlanan içerik değişmez; kayıtlar işlendikleri sürüme bağlı
/// kalır (K16). <see cref="IsVerified"/> kuralın uzman tarafından doğrulandığını söyler; doğrulanmamış yayın arayüzde "Doğrulanmadı" etiketi taşır.
/// </summary>
public sealed class RegimeProfileVersion
{
    public const int NoteMaxLength = 500;

    private RegimeProfileVersion()
    {
        // EF Core için
    }

    public RegimeProfileVersion(Guid profileId, int number, string contentJson, Guid createdByUserId)
    {
        if (number < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(number));
        }

        Id = Guid.CreateVersion7();
        ProfileId = profileId;
        Number = number;
        ContentJson = contentJson;
        State = RegimeProfileVersionState.Draft;
        CreatedByUserId = createdByUserId;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; }

    public Guid ProfileId { get; private set; }

    /// <summary>Profil içinde artan sürüm numarası (1'den).</summary>
    public int Number { get; private set; }

    public RegimeProfileVersionState State { get; private set; }

    /// <summary>İçerik (<see cref="RegimeProfileContent"/>) JSON olarak.</summary>
    public string ContentJson { get; private set; } = string.Empty;

    public bool IsVerified { get; private set; }

    public string? VerificationNote { get; private set; }

    public Guid CreatedByUserId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? PublishedAt { get; private set; }

    public Guid? PublishedByUserId { get; private set; }

    /// <summary>Taslağın içeriğini değiştirir (yayınlanmış sürüm değiştirilemez).</summary>
    public void ReplaceContent(string contentJson)
    {
        if (State != RegimeProfileVersionState.Draft)
        {
            throw new InvalidOperationException("Yalnızca taslak sürümün içeriği değiştirilebilir.");
        }

        ContentJson = contentJson;
    }

    public void Publish(bool verified, string? note, Guid publishedByUserId)
    {
        if (State != RegimeProfileVersionState.Draft)
        {
            throw new InvalidOperationException("Yalnızca taslak sürüm yayınlanabilir.");
        }

        State = RegimeProfileVersionState.Published;
        IsVerified = verified;
        VerificationNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        PublishedAt = DateTimeOffset.UtcNow;
        PublishedByUserId = publishedByUserId;
    }

    public void Supersede()
    {
        if (State != RegimeProfileVersionState.Published)
        {
            throw new InvalidOperationException("Yalnızca yayındaki sürümün yerine yenisi geçebilir.");
        }

        State = RegimeProfileVersionState.Superseded;
    }
}

public sealed record RegimeProfileSummary(Guid Id, string Code, string Name, bool IsActive, int? PublishedNumber, bool PublishedVerified, bool HasDraft);

public sealed record RegimeProfileVersionView(
    Guid Id, int Number, RegimeProfileVersionState State, bool IsVerified, string? VerificationNote, DateTimeOffset CreatedAt, DateTimeOffset? PublishedAt,
    RegimeProfileContent Content);

public sealed record RegimeProfileDetail(RegimeProfileSummary Summary, IReadOnlyList<RegimeProfileVersionView> Versions);

/// <summary>Profil bulunamadı (404).</summary>
public sealed class RegimeProfileNotFoundException(string message) : Exception(message);

/// <summary>Kod çakışması, taslak yokken yayın gibi veri durumundan doğan hata (409).</summary>
public sealed class RegimeProfileConflictException(string message) : Exception(message);

/// <summary>
/// Platform yöneticisinin rejim profili yönetimi. Akış: profil oluştur → taslak içeriği kaydet (doğrulanır) → yayınla (taslak yayına çıkar,
/// önceki yayın "yerini bıraktı" olur). Her yazma platform denetim kaydına aynı işlemde yazılır.
/// </summary>
public interface IRegimeProfileAdmin
{
    Task<IReadOnlyList<RegimeProfileSummary>> ListAsync(CancellationToken cancellationToken);

    Task<RegimeProfileDetail> GetAsync(Guid profileId, CancellationToken cancellationToken);

    Task<RegimeProfileSummary> CreateAsync(Guid actingUserId, string code, string name, CancellationToken cancellationToken);

    /// <summary>Taslağı oluşturur ya da içeriğini değiştirir. İçerik geçersizse <see cref="ArgumentException"/> (hata iletileri birleşik) fırlatır.</summary>
    Task<RegimeProfileVersionView> SaveDraftAsync(Guid actingUserId, Guid profileId, RegimeProfileContent content, CancellationToken cancellationToken);

    Task DiscardDraftAsync(Guid actingUserId, Guid profileId, CancellationToken cancellationToken);

    /// <param name="verified">Kuralın uzman tarafından doğrulandığı. Doğrulanmamış yayın "Doğrulanmadı" etiketiyle yayınlanır.</param>
    Task<RegimeProfileVersionView> PublishAsync(Guid actingUserId, Guid profileId, bool verified, string? note, CancellationToken cancellationToken);

    Task<RegimeProfileSummary> SetActiveAsync(Guid actingUserId, Guid profileId, bool active, CancellationToken cancellationToken);
}
