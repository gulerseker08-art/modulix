namespace Modulix.Modules.Contracts.Lifecycle;

/// <summary>Sağlayıcıya iletilen imzacı (yalnızca imza için gerekenler).</summary>
public sealed record ProviderSignatory(int Position, string Name, string? Email, string? Capacity, bool IsRequired);

/// <summary>Sağlayıcıya iletilen istek: Modulix'in referansı, belge adı ve özeti, imzacılar ve son tarih. Sözleşme tutarları ve iç alanlar sağlayıcıya gitmez.</summary>
public sealed record ProviderSignatureRequest(
    string Reference, string DocumentName, string DocumentSha256, IReadOnlyList<ProviderSignatory> Signatories, DateTimeOffset ExpiresAt, bool Sequential);

public sealed record ProviderRequestResult(string ProviderReference);

/// <summary>
/// Harici imza sağlayıcısı (değiştirilebilir). Gerçek bir sağlayıcı eklemek bu arayüzü uygulamak, ana bilgisayarda (API) o sağlayıcının webhook
/// doğrulayıcısını bağlamak ve ayarda sağlayıcı adını seçilebilir kılmaktır; sözleşme modülü sağlayıcıya özgü bir şey bilmez. Sağlayıcı olayları
/// <see cref="IContractSignatures.ApplyProviderEventAsync"/> ile uygulanır; olayın Modulix'teki karşılığı sağlayıcıdan bağımsızdır.
/// </summary>
public interface IContractSignatureProvider
{
    /// <summary>Ayarda ve istekte saklanan küçük harfli ad (ör. "simulation").</summary>
    string Name { get; }

    /// <summary>Görünen ad.</summary>
    string DisplayName { get; }

    /// <summary>Test simülasyonu mu? Doğruysa imza hiçbir hukuki değer taşımaz ve ekranlarda böyle gösterilir.</summary>
    bool IsSimulation { get; }

    Task<ProviderRequestResult> CreateRequestAsync(ProviderSignatureRequest request, CancellationToken cancellationToken);

    Task CancelRequestAsync(string providerReference, CancellationToken cancellationToken);
}

/// <summary>Kayıtlı sağlayıcılar (adla bulunur).</summary>
public interface IContractSignatureProviders
{
    IReadOnlyList<IContractSignatureProvider> All { get; }

    IContractSignatureProvider? Find(string? name);
}

internal sealed class ContractSignatureProviders(IEnumerable<IContractSignatureProvider> providers) : IContractSignatureProviders
{
    private readonly IReadOnlyList<IContractSignatureProvider> all = [.. providers];

    public IReadOnlyList<IContractSignatureProvider> All => all;

    public IContractSignatureProvider? Find(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : all.FirstOrDefault(p => string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Test simülasyonu: ağ çağrısı yapmaz, kimseye e-posta ya da imza isteği göndermez; referans üretir ve olayları Modulix içinden (simülasyon uç noktası ya da
/// imzalı webhook) tetiklenir. <b>Gerçek bir entegrasyon değildir</b>; ürettiği imzalar <see cref="SignatureAssurance.SimulatedElectronic"/> sayılır.
/// </summary>
public sealed class SimulationSignatureProvider : IContractSignatureProvider
{
    public const string ProviderName = "simulation";

    public string Name => ProviderName;

    public string DisplayName => "Simülasyon (test)";

    public bool IsSimulation => true;

    public Task<ProviderRequestResult> CreateRequestAsync(ProviderSignatureRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new ProviderRequestResult(request.Reference));

    public Task CancelRequestAsync(string providerReference, CancellationToken cancellationToken) => Task.CompletedTask;
}
