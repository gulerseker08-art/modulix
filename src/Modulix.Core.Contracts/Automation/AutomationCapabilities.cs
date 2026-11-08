using System.Text.Json.Nodes;
using Modulix.Core.Contracts.Records;

namespace Modulix.Core.Contracts.Automation;

// Modülün kural altyapısına beyan ettiği yetenekler ve aksiyon (araç) sözleşmesi (ADR-0027). Core sektöre özel hiçbir şey bilmez;
// modül neyi tetikleyebileceğini, hangi alanları okutabileceğini ve hangi aksiyonları sunduğunu burada bildirir. Beyan açılışta
// ModuleCatalog tarafından doğrulanır.

/// <summary>
/// Kural yazılabilen kayıt türü. <see cref="ReadPermission"/> boş değilse yalnızca bu izne sahip olanlar kural yazabilir.
/// <see cref="RecordSource"/>: tarama tabanlı tetikleyicileri (zamanlanmış, tarihe göre, hareketsizlik) destekleyen modül
/// <see cref="IAutomationRecordSource"/> uygulayan tipi verir; boşsa bu kayıt türüne yalnızca olay tetikleyicileri konabilir.
/// </summary>
public sealed record AutomationEntityDefinition(string EntityKey, string DisplayName, string? ReadPermission = null)
{
    public Type? RecordSource { get; init; }

    /// <summary>
    /// Kayıt seçicide (kuralı bir kayıt üzerinde denemek, elle çalıştırmak) kaydı tanıtan alanın adı (ör. "title"). Kayıt türünün beyan
    /// edilmiş bir <b>metin</b> alanı olmalıdır; boşsa seçici kaydı yalnızca kısa kimliğiyle gösterir. Kullanıcı alanı göremiyorsa
    /// (<see cref="AutomationFieldDefinition.ReadPermission"/>) katalog etiket alanını vermez.
    /// </summary>
    public string? LabelField { get; init; }
}

/// <summary>
/// Modülün tetikleyebildiği olay. <see cref="EventType"/> kayıt olayıysa (<c>RecordCreated</c>, <c>RecordUpdated</c>) kayıt boru hattının
/// olayıdır; modülün iş olayıysa modül önekli bir türdür (ör. "crm.opportunity.won"). <see cref="Key"/> modül önekli ve değişmezdir.
/// </summary>
public sealed record AutomationEventDefinition(string Key, string EntityKey, string EventType, string DisplayName);

public sealed record AutomationOption(string Key, string Label);

/// <summary>
/// Kuralda okunabilen alan. <see cref="Name"/> kayıt değerlerindeki yoldur (ör. "amount", özel alan için "custom.arac_tipi").
/// <see cref="Key"/> = kayıt türü + "." + ad ve değişmezdir. <see cref="ReadPermission"/>: bu izne sahip olmayan kimlik alanı
/// göremez; kural onda yanlış değerlenir ve değer açıklamada gösterilmez (hassas alanlar için aynı izin verilmelidir).
/// </summary>
public sealed record AutomationFieldDefinition(string EntityKey, string Name, string Label, AutomationFieldType Type)
{
    public string Key => EntityKey + "." + Name;

    public string? ReadPermission { get; init; }

    /// <summary>Seçim alanında izin verilen değerler; boşsa değer doğrulanmaz.</summary>
    public IReadOnlyList<AutomationOption> Options { get; init; } = [];

    /// <summary>Lookup alanının lookup anahtarı (ADR-0016).</summary>
    public string? LookupKey { get; init; }

    /// <summary>Para alanında para birimini taşıyan kardeş alanın adı (kayıt değerlerindeki yol, ör. "currency"); para alanı için zorunludur.</summary>
    public string? CurrencyField { get; init; }

    public string? Hint { get; init; }
}

/// <summary>
/// Modülün beyan ettiği ilişki (ör. fırsatın aktiviteleri). Çözümleyici <see cref="Resolver"/> modüle aittir ve sayımı tek sorguyla
/// yapar. <see cref="TargetFields"/>: koşulun <c>Where</c> bölümünde kullanılabilen hedef kayıt alanları (ikinci seviye ilişki yoktur).
/// </summary>
public sealed record AutomationRelationDefinition(
    string Key,
    string EntityKey,
    string TargetEntityKey,
    string Label,
    IReadOnlyList<AutomationFieldDefinition> TargetFields,
    Type Resolver)
{
    public string? ReadPermission { get; init; }
}

/// <summary>İlişki sayımı isteği; <see cref="Where"/> doğrulanmış, yalnızca hedef alanlarına bakan koşuldur.</summary>
public sealed record AutomationRelationQuery(string Relation, Guid SourceRecordId, ConditionNode? Where);

/// <summary>
/// Modülün ilişki çözümleyicisi; <see cref="AutomationRelationDefinition.Resolver"/> tipi bunu uygular ve modül
/// <c>ConfigureServices</c>'te scoped kaydeder. Yalnızca kendi tablolarını okur; tenant süzgeci ve RLS veri erişimiyle uygulanır.
/// </summary>
public interface IAutomationRelationResolver
{
    /// <summary>Kaynak kayda bağlı, koşula uyan kayıt sayısı. Tek sorgu; kayıt başına döngü yoktur.</summary>
    Task<int> CountAsync(AutomationRelationQuery query, CancellationToken cancellationToken);
}

/// <summary>Aksiyonun etkisinin sınıfı. Dry-run yalnızca <see cref="Write"/> aksiyonu çalıştırmaz, planlar.</summary>
public enum AutomationSideEffect
{
    /// <summary>Hiçbir şey yazmaz.</summary>
    Read = 1,

    /// <summary>Taslak üretir; kesinleşme ve dış etki yoktur.</summary>
    Draft = 2,

    /// <summary>Kayıt değiştirir ya da dış etki üretir.</summary>
    Write = 3,
}

/// <summary>Idempotency anahtarının kaynağı.</summary>
public enum AutomationIdempotency
{
    /// <summary>Motor türetir: tenant + tetikleyici/olay kimliği + kural sürümü + hedef kayıt + adım.</summary>
    Derived = 1,

    /// <summary>Çağıran verir (manuel çağrı, ileride ajan).</summary>
    CallerSupplied = 2,
}

public sealed record AutomationParameter(string Name, string Label, AutomationFieldType Type, bool Required = true)
{
    public string? Description { get; init; }

    public IReadOnlyList<AutomationOption> Options { get; init; } = [];

    public string? LookupKey { get; init; }

    /// <summary>Metin için en çok uzunluk.</summary>
    public int MaxLength { get; init; } = 500;

    /// <summary>Parametre kayıttan alan bağlamayla (<c>{"$field": "anahtar"}</c>) verilebilir mi? Varsayılan evet.</summary>
    public bool AllowFieldBinding { get; init; } = true;
}

/// <summary>
/// Aksiyon (araç) sözleşmesi: kural motorunun, elle çalıştırmanın ve ileride yapay zekâ ajanlarının çağırdığı tek tip kayıt.
/// Çağıran kimlik sözleşmenin dışındadır (<see cref="AutomationCallContext"/>). <see cref="Key"/> modül önekli ve değişmezdir
/// (ör. "crm.follow-up.create"). <see cref="RequiredPermissions"/> çağıran kimliğin, <see cref="RequiredModules"/> tenant'ın
/// sahip olması gerekenlerdir (lisans); ikisi de hem yayın hem çalışma anında denetlenir.
/// </summary>
public sealed record AutomationActionDefinition(
    string Key,
    string DisplayName,
    string Description,
    IReadOnlyList<AutomationParameter> Parameters,
    IReadOnlyList<string> RequiredPermissions,
    IReadOnlyList<string> RequiredModules,
    AutomationSideEffect SideEffect,
    bool SupportsDryRun,
    AutomationIdempotency Idempotency = AutomationIdempotency.Derived);

public sealed record AutomationActionRegistration(AutomationActionDefinition Definition, Type Handler)
{
    public static AutomationActionRegistration For<THandler>(AutomationActionDefinition definition)
        where THandler : class, IAutomationAction =>
        new(definition, typeof(THandler));
}

/// <summary>
/// Modülün sunduğu başlangıç kuralı şablonu (ADR-0027, Dilim 3b). Şablon koddadır ve tenant'a özgü değildir (tek imaj); firma
/// "kullan" dediğinde <b>taslak</b> kural olarak kopyalanır: taslak hiç çalışmaz, firma gözden geçirip kendisi yayınlar.
/// <see cref="Key"/> modül önekli ve tekildir (ör. "crm.lead-inactivity-follow-up"); <see cref="EntityKey"/> modülün kural yazılabilen
/// kayıt türüdür. Tanım katalogla birlikte açılışta kontrol edilir, tam doğrulama kullanan kimliğin yetkisiyle yapılır.
/// </summary>
public sealed record AutomationTemplateDefinition(
    string Key, string DisplayName, string Description, string EntityKey, AutomationDefinition Definition);

/// <summary>Modülün kural altyapısına beyan ettiği tüm yetenekler. Boş beyan geçerlidir.</summary>
public sealed record AutomationCapabilities
{
    public static AutomationCapabilities None { get; } = new();

    public IReadOnlyList<AutomationEntityDefinition> Entities { get; init; } = [];

    public IReadOnlyList<AutomationEventDefinition> Events { get; init; } = [];

    public IReadOnlyList<AutomationFieldDefinition> Fields { get; init; } = [];

    public IReadOnlyList<AutomationRelationDefinition> Relations { get; init; } = [];

    public IReadOnlyList<AutomationActionRegistration> Actions { get; init; } = [];

    /// <summary>Başlangıç şablonları; kullanılınca pasif (taslak) kural olur.</summary>
    public IReadOnlyList<AutomationTemplateDefinition> Templates { get; init; } = [];

    public bool IsEmpty => Entities.Count + Events.Count + Fields.Count + Relations.Count + Actions.Count + Templates.Count == 0;
}

/// <summary>Çağıran kimliğin türü. <see cref="Agent"/> ileride yapay zekâ içindir; bugün hiçbir çağrı yolu üretmez.</summary>
public enum AutomationCallerKind
{
    User = 1,
    Automation = 2,
    Agent = 3,
}

/// <summary>Aksiyonu çağıran kimlik ve izleme bilgisi; aksiyon sözleşmesinin parçası değil, bağlamdır.</summary>
public sealed record AutomationCallContext(
    AutomationCallerKind Kind,
    Guid UserId,
    string? CorrelationId = null,
    Guid? CausationId = null,
    int Depth = 0)
{
    public Guid? RuleVersionId { get; init; }
}

/// <summary>
/// Aksiyon isteği. <see cref="Parameters"/> şemaya göre doğrulanmış ve alan bağlamaları çözülmüştür. <see cref="IdempotencyKey"/>
/// aynı etkinin ikinci kez oluşmaması içindir; aksiyon bunu hedef sisteme geçirir. <see cref="DryRun"/> doluysa aksiyon hiçbir şey
/// yazmaz (yalnızca <see cref="AutomationActionDefinition.SupportsDryRun"/> olanlar çağrılır).
/// </summary>
public sealed record AutomationActionRequest(
    string Action,
    JsonObject Parameters,
    RecordRef? Target,
    string IdempotencyKey,
    bool DryRun = false);

public enum AutomationActionStatus
{
    Succeeded = 1,

    /// <summary>Etki zaten vardı ya da koşul gereği yapılmadı (hata değil).</summary>
    Skipped = 2,

    Failed = 3,

    /// <summary>Dry-run: yapılacak iş planlandı, yazılmadı.</summary>
    Planned = 4,
}

/// <summary>Aksiyon sonucu. <see cref="Summary"/> kullanıcıya gösterilir; kişisel veri taşımaz.</summary>
public sealed record AutomationActionResult(AutomationActionStatus Status, string Summary)
{
    public RecordRef? Effect { get; init; }

    /// <summary>Başarısızlıkta tekrar denemek sonucu değiştirebilir mi?</summary>
    public bool Retryable { get; init; }
}

/// <summary>
/// Aksiyon işleyicisi; modül <c>ConfigureServices</c>'te scoped kaydeder. Yalnızca Contracts'taki servisleri (kayıt boru hattı vb.)
/// kullanır, <c>DbContext</c>'e doğrudan yazmaz. Teslim en az bir kezdir: işleyici <see cref="AutomationActionRequest.IdempotencyKey"/>
/// ile idempotent yazılır. <see cref="AutomationActionRequest.DryRun"/> doluyken hiçbir şey yazmaz.
/// </summary>
public interface IAutomationAction
{
    Task<AutomationActionResult> ExecuteAsync(
        AutomationActionRequest request, AutomationCallContext context, CancellationToken cancellationToken);
}
