using System.Globalization;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Customization;
using Modulix.Core.Contracts.Persistence;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;

namespace Modulix.Modules.Contracts.Records;

/// <summary>Kayıt depolarının ihtiyaç duyduğu servisler: veri, tenant üyeleri, seçim listeleri, isteği yapan kullanıcı ve iş akışı kapısı.</summary>
internal sealed class ContractsStoreContext(
    IModuleData data, ITenantMembers members, IChoiceLists choiceLists, ITenantContextAccessor tenant, IRecordAccess access,
    ICurrentAccess permissions, ContractWorkflowGate gate, IRecordCommandContext? command = null)
{
    public IModuleData Data => data;

    public ITenantMembers Members => members;

    public IChoiceLists ChoiceLists => choiceLists;

    /// <summary>İsteği yapan kullanıcının izin ve veri kapsamı.</summary>
    public IRecordAccess Access => access;

    /// <summary>Kullanıcının izinleri (kayıt türünden bağımsız izin sorguları için).</summary>
    public ICurrentAccess Permissions => permissions;

    /// <summary>İş akışı kapısı: durum ve toplam gibi sistem alanları yalnızca kapı açıkken yazılır.</summary>
    public ContractWorkflowGate Gate => gate;

    /// <summary>Çalışan yazma komutunun nedeni (yalnızca güvenilir iç çağıranlar verir, ör. kayıt devri).</summary>
    public string? CommandReason => command?.Reason;

    /// <summary>İsteği yapan kullanıcı; aktif tenant yoksa null.</summary>
    public Guid? CurrentUserId => tenant.Current?.UserId;
}

/// <summary>
/// Sözleşme iş akışı kapısı (istek ömürlü): durum, sürüm ve toplam alanları genel kayıt uçlarından yazılamaz; yalnızca iş akışı servisi
/// kapıyı açarak boru hattı üzerinden yazar. Kapı açıkken yapılan güncelleme tarihçeye <see cref="EventKind"/> satırı bırakır.
/// </summary>
internal sealed class ContractWorkflowGate
{
    public IReadOnlyList<string>? Permissions { get; private set; }

    public bool IsOpen => Permissions is not null;

    public string? EventKind { get; private set; }

    public string? Reason { get; private set; }

    /// <summary>
    /// Geçişle aynı işlemde çalışan ek yazma (sürüm dondurma, sürüm durumu). Sözleşmenin sürüm denetimine ve audit'ine bağlı yazılır;
    /// eşzamanlı çakışmada hiçbiri yazılmaz.
    /// </summary>
    public Func<ContractsStoreContext, Domain.Contract, CancellationToken, Task>? Effect { get; private set; }

    /// <summary>Şablon geçişiyle (yayımlama) aynı işlemde çalışan ek yazma (sürüm satırı). Şablon kaydının sürüm denetimine bağlıdır.</summary>
    public Func<ContractsStoreContext, Domain.ContractTemplate, CancellationToken, Task>? TemplateEffect { get; private set; }

    /// <summary>Taahhüt geçişiyle (gerçekleşme tamamlama, ihlal, muafiyet) aynı işlemde çalışan ek yazma. Taahhüt kaydının sürüm denetimine bağlıdır.</summary>
    public Func<ContractsStoreContext, Domain.ContractCommitment, CancellationToken, Task>? CommitmentEffect { get; private set; }

    public IDisposable Open(
        IReadOnlyList<string> permissions, string eventKind, string? reason = null, Func<ContractsStoreContext, Domain.Contract, CancellationToken, Task>? effect = null,
        Func<ContractsStoreContext, Domain.ContractTemplate, CancellationToken, Task>? templateEffect = null,
        Func<ContractsStoreContext, Domain.ContractCommitment, CancellationToken, Task>? commitmentEffect = null)
    {
        CommitmentEffect = commitmentEffect;
        Permissions = permissions;
        EventKind = eventKind;
        Reason = reason;
        Effect = effect;
        TemplateEffect = templateEffect;
        return new Closer(this);
    }

    private sealed class Closer(ContractWorkflowGate gate) : IDisposable
    {
        public void Dispose()
        {
            gate.Permissions = null;
            gate.EventKind = null;
            gate.Reason = null;
            gate.Effect = null;
            gate.TemplateEffect = null;
            gate.CommitmentEffect = null;
        }
    }
}

/// <summary>Liste filtresi: değeri okuyup sorguya uygular. Değer geçersizse null döner (doğrulama hatası olur).</summary>
internal delegate IQueryable<TEntity>? RecordFilter<TEntity>(IQueryable<TEntity> source, string value, ContractsStoreContext context);

/// <summary>Filtre değerlerini okuma yardımcıları.</summary>
internal static class FilterValue
{
    public static Guid? Id(string value) => Guid.TryParse(value, out var id) && id != Guid.Empty ? id : null;

    /// <summary>"me" isteği yapan kullanıcıdır; aksi halde kullanıcı kimliği beklenir.</summary>
    public static Guid? User(string value, ContractsStoreContext context) =>
        string.Equals(value, "me", StringComparison.OrdinalIgnoreCase) ? context.CurrentUserId : Id(value);

    public static bool? Bool(string value) => bool.TryParse(value, out var flag) ? flag : null;

    public static DateOnly? Date(string value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
}
