namespace Modulix.Core.Organization;

/// <summary>Bir işletici şirketin şubesi (ADR-0008): idari birimdir, kapsam sınırı değildir.</summary>
public sealed record BranchSummary(Guid Id, Guid CompanyId, string Name);

/// <summary>
/// İşletici şirketlerin şubeleri (ADR-0008, adım 5). Şube idari bir birimdir; hiçbir kaydı kapsam olarak süzmez. Yapılandırma
/// verisidir; bu servisten yazılır ve değişiklik aynı işlemde denetim kaydına geçer. Aktif tenant yoksa yazma işlemleri
/// <see cref="InvalidOperationException"/> fırlatır. Aynı ad için <see cref="CompanyConflictException"/>, geçersiz ad için
/// <see cref="ArgumentException"/> fırlatır.
/// </summary>
public interface IBranchAdministration
{
    /// <summary>Şirketin şubeleri, ada göre sıralı. Şirket bu tenant'ta değilse <c>null</c>.</summary>
    Task<IReadOnlyList<BranchSummary>?> ListAsync(Guid companyId, CancellationToken cancellationToken);

    /// <summary>Şirkete yeni şube açar; adı şirket içinde tekil olmalıdır. Şirket bu tenant'ta değilse <c>null</c>.</summary>
    Task<BranchSummary?> CreateAsync(Guid companyId, string name, CancellationToken cancellationToken);

    /// <summary>Şubeyi yeniden adlandırır. Şirket veya şube bulunamazsa <c>null</c>.</summary>
    Task<BranchSummary?> RenameAsync(Guid companyId, Guid branchId, string name, CancellationToken cancellationToken);
}
