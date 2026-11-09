using Modulix.Core.Contracts.Records;

namespace Modulix.Core.Contracts.Approvals;

/// <summary>
/// Onaya gönderilebilen kayıt türü (ADR-0027 Dilim 4c). Modül <c>IModule.ApprovalTargets</c> ile beyan eder; firma bir onay sürecini bu
/// türe bağlayınca talep o kayıt için açılır. Alan adları kaydın teknik alanlarıdır (<see cref="RecordData.Values"/>). Tutar ve para birimi
/// talepte kullanıcıdan alınmaz, kayıttan okunur; kayıtta bu alanlar değişirse bekleyen talep geçersizleşir (Ç6a.2).
/// <see cref="LinkTemplate"/> arayüz yoludur, <c>{id}</c> kaydın kimliğiyle değiştirilir.
/// </summary>
public sealed record ApprovalTargetDefinition(string EntityKey, string DisplayName, string LabelField, string LinkTemplate)
{
    public string? AmountField { get; init; }

    public string? CurrencyField { get; init; }

    /// <summary>
    /// Onaya bağlanabilen modül işlemleri (Ç6a.3, ör. fırsatı kazanıldı olarak kapatma). Firma bir onay sürecinde bunlardan seçtiklerini
    /// "onaysız yapılamaz" yapar; modül işlemi yapmadan önce <see cref="IApprovalGate"/>'e sorar. Anahtarlar kayıt türü içinde tekildir.
    /// </summary>
    public IReadOnlyList<ApprovalCommandDefinition> Commands { get; init; } = [];

    /// <summary>Talebi geçersiz kılan alanlar: tutar ve para birimi.</summary>
    public IEnumerable<string> GuardedFields => new[] { AmountField, CurrencyField }.OfType<string>();
}

/// <summary>Bir kaydın son onay talebinin özeti. <see cref="Status"/>: Pending, Approved, Rejected, Withdrawn, Invalidated.</summary>
public sealed record RecordApprovalStatus(
    Guid RequestId, string Process, string Status, DateTimeOffset SubmittedAt, DateTimeOffset? CompletedAt, string? CurrentStep);

/// <summary>
/// Modüllerin kayıt onay durumunu sorduğu kapı (Ç6a.3): onay, modülün kesinleştirme komutuna girdi olabilir ama modülün kendi
/// doğrulamasının yerine geçmez. Aktif tenant'ta çalışır; kayıt yetkisini çağıran modül denetler.
/// </summary>
public interface IApprovalStatusReader
{
    /// <summary>Kaydın en son açılan onay talebi; hiç talep yoksa null.</summary>
    Task<RecordApprovalStatus?> LatestAsync(RecordRef record, CancellationToken cancellationToken);
}

/// <summary>Onaya bağlanabilen modül işlemi: kalıcı anahtar (ör. "win") ve arayüz adı (ör. "Kazanıldı olarak kapatma").</summary>
public sealed record ApprovalCommandDefinition(string Key, string DisplayName);

/// <summary>
/// Onay kapısı (ADR-0027 Ç6a.3): modül, onaya bağlanabilen bir işlemi yapmadan önce sorar. Firma o işlemi bir onay sürecine bağlamamışsa
/// işlem serbesttir. Bağlamışsa kaydın o süreçlerdeki son talebi onaylanmış olmalıdır. Onaydan sonra tutar ya da para birimi değişirse onay
/// geçersizleşir. Kapı modülün kendi doğrulamasının yerine geçmez; aktif tenant'ta çalışır, kayıt yetkisini çağıran modül denetler.
/// </summary>
public interface IApprovalGate
{
    /// <summary>İşlem engelliyse kullanıcıya gösterilecek neden; serbestse <c>null</c>.</summary>
    Task<string?> BlockReasonAsync(RecordRef record, string command, CancellationToken cancellationToken);
}
