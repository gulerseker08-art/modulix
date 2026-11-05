using Modulix.Core.Contracts.Sectors;

namespace Modulix.Core.Sectors;

/// <summary>
/// Firma ilişki rolü kataloğunun öğesi (ADR-0029 K9): müşteri, tedarikçi, taşıyıcı, acente... Platform geneli kayıttır (RLS dışı);
/// platform yöneticisi yönetir, silinmez, pasife alınır. Bir firma aynı anda birden çok role sahip olabilir. Rol, firmanın
/// <i>ticari ilişkisini</i> söyler; aldığı hizmet (hizmet ihtiyacı) ve müşterinin sektörü ayrı alanlardır.
/// </summary>
public sealed class RelationshipRole
{
    private RelationshipRole()
    {
        // EF Core için
    }

    public RelationshipRole(string code, string name)
    {
        if (!ActivityTreeRules.IsValidCode(code))
        {
            throw new ArgumentException($"Geçersiz rol kodu: '{code}'.", nameof(code));
        }

        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > ActivityTreeRules.NameMaxLength)
        {
            throw new ArgumentException("Rol adı boş olamaz ve en çok 100 karakter olabilir.", nameof(name));
        }

        Id = Guid.CreateVersion7();
        Code = code;
        Name = name.Trim();
        IsActive = true;
    }

    public Guid Id { get; private set; }

    public string Code { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public bool IsActive { get; private set; }

    public void SetActive(bool active) => IsActive = active;
}

public sealed record RelationshipRoleView(Guid Id, string Code, string Name, bool IsActive);

/// <summary>İlişki rolü kataloğu yönetimi (platform tarafı); yazmalar platform denetim kaydına aynı işlemde yazılır.</summary>
public interface IRelationshipRoles
{
    Task<IReadOnlyList<RelationshipRoleView>> ListAsync(CancellationToken cancellationToken);

    Task<RelationshipRoleView> CreateAsync(Guid actingUserId, string code, string name, CancellationToken cancellationToken);

    Task<RelationshipRoleView> SetActiveAsync(Guid actingUserId, string code, bool active, CancellationToken cancellationToken);
}

/// <summary>
/// Firma kaydındaki kod listeleri (ilişki rolleri, müşteri sektörü, hizmet ihtiyacı) için saf kurallar: kodlar küçük harf, tekrarsız
/// ve sıralı saklanır (tek sütunda virgülle). Katalogda var olma denetimi veritabanı gerektirir ve depodadır.
/// </summary>
public static class PartyRelationshipRules
{
    public const int MaxItems = ActivityCodeList.MaxItems;

    /// <summary>Sütun uzunluğu: en çok <see cref="MaxItems"/> kod, kod başına en çok 50 karakter ve ayraç.</summary>
    public const int StoredMaxLength = ActivityCodeList.StoredMaxLength;

    public static IReadOnlyList<string> Split(string? stored) => ActivityCodeList.Split(stored);

    /// <summary>Tekrarları atar, sıralar; geçersiz kod ya da fazla öğe varsa hata iletisi döner.</summary>
    public static (IReadOnlyList<string> Codes, string? Error) Normalize(IEnumerable<string?> codes) => ActivityCodeList.Normalize(codes);

    public static string? Join(IReadOnlyCollection<string> codes) => ActivityCodeList.Join(codes);
}
