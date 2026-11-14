using System.Globalization;
using System.Text.Json;
using Modulix.Shared.Platform;
using Modulix.Web.Services;

namespace Modulix.Web.Tests;

/// <summary>
/// ADR-0034 (Platform P1): Platform yönetimi ekranlarının etiket tabloları (<see cref="PlatformLabels"/>) sözlükten okunur. Sözlük
/// yüklenmemişken kaynak (Türkçe) metin gösterilir; testler her kaynak satırının üç sözlükte karşılığı olduğunu, Türkçe sözlüğün paylaşılan
/// sözleşmelerdeki etiketlerle aynı kaldığını ve birleştirilmiş Türkçe metinlerin eski sabit metinle aynı çıktığını denetler.
/// </summary>
public class PlatformLabelsTests
{
    private static Dictionary<string, string> Dictionary(string language)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "src", "Modulix.Core", "Localization", "Texts", "tr.json")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var path = Path.Combine(directory!.FullName, "src", "Modulix.Core", "Localization", "Texts", $"{language}.json");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))!;
    }

    // Sözlük testte yüklenmediği için tablolar kaynak metni döndürür.
    private static IEnumerable<(string Group, IReadOnlyList<(string Key, string Label)> Rows)> Tables() =>
    [
        (PlatformLabels.TenantStateGroup, PlatformLabels.TenantStates),
        (PlatformLabels.LicenseSourceGroup, PlatformLabels.LicenseSources),
        (PlatformLabels.EventStateGroup, PlatformLabels.EventStates),
        (PlatformLabels.AuditActionGroup, PlatformLabels.AuditActions),
        (PlatformLabels.RoleGroup, PlatformLabels.Roles),
        (PlatformLabels.BillingStatusGroup, PlatformLabels.BillingStatuses),
    ];

    private static IEnumerable<(string Key, string Label)> Rows() =>
        Tables().SelectMany(t => t.Rows.Select(r => (AdminLabels.KeyOf(t.Group, r.Key), r.Label)));

    [Theory]
    [InlineData("tr")]
    [InlineData("en")]
    [InlineData("az")]
    public void Every_table_row_has_a_text_in_each_language(string language)
    {
        var texts = Dictionary(language);
        var missing = Rows()
            .Select(r => r.Key)
            .Where(k => !texts.TryGetValue(k, out var text) || string.IsNullOrWhiteSpace(text))
            .ToList();

        Assert.True(missing.Count == 0, string.Join(Environment.NewLine, missing));
    }

    [Fact]
    public void Turkish_dictionary_matches_the_shared_labels()
    {
        var texts = Dictionary("tr");
        var different = Rows()
            .Where(r => texts[r.Key] != r.Label)
            .Select(r => $"{r.Key}: {texts[r.Key]} != {r.Label}")
            .ToList();

        Assert.True(different.Count == 0, string.Join(Environment.NewLine, different));
    }

    [Fact]
    public void Tables_cover_every_value()
    {
        Assert.Equal(Enum.GetValues<TenantState>().Length, PlatformLabels.TenantStates.Count);
        Assert.Equal(Enum.GetValues<LicenseSource>().Length, PlatformLabels.LicenseSources.Count);
        Assert.Equal(Enum.GetValues<EventDeliveryStateDto>().Length, PlatformLabels.EventStates.Count);
        Assert.Equal(PlatformAuditText.All.Count, PlatformLabels.AuditActions.Count);
        Assert.Equal(4, PlatformLabels.Roles.Count);

        // Ödeme durumu: her enum değeri ve vadesi geçmiş bekleyen kayıt.
        Assert.Equal(Enum.GetValues<BillingStatusDto>().Length + 1, PlatformLabels.BillingStatuses.Count);
    }

    [Fact]
    public void Lookups_return_the_source_text()
    {
        Assert.Equal("Askıda", PlatformLabels.TenantStateOf(TenantState.Suspended));
        Assert.Equal("İstisna", PlatformLabels.LicenseSourceOf(LicenseSource.Exception));
        Assert.Equal("Bekletiliyor", PlatformLabels.EventStateOf(EventDeliveryStateDto.Held));
        Assert.Equal("Plan atandı", PlatformLabels.AuditActionOf("tenant.plan-assigned"));
        Assert.Equal("unknown.action", PlatformLabels.AuditActionOf("unknown.action"));
        Assert.Equal("Platform sahibi", PlatformLabels.RoleOf(PlatformRoleNames.Owner));
        Assert.Equal("Other", PlatformLabels.RoleOf("Other"));
        Assert.Equal("platform.audit-action.tenant.module-changed", AdminLabels.KeyOf(PlatformLabels.AuditActionGroup, "tenant.module-changed"));
    }

    [Fact]
    public void Billing_status_shows_overdue_only_for_pending_records()
    {
        static BillingRecordDto Record(BillingStatusDto status, bool overdue) =>
            new(Guid.NewGuid(), Guid.NewGuid(), "Acme", new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31), 100m, "TRY", null, null, null, status, null, null,
                DateTimeOffset.UnixEpoch, overdue);

        Assert.Equal("Bekliyor", PlatformLabels.BillingStatusOf(Record(BillingStatusDto.Pending, false)));
        Assert.Equal("Vadesi geçti", PlatformLabels.BillingStatusOf(Record(BillingStatusDto.Pending, true)));
        Assert.Equal("Ödendi", PlatformLabels.BillingStatusOf(Record(BillingStatusDto.Paid, true)));
        Assert.Equal("İptal", PlatformLabels.BillingStatusOf(Record(BillingStatusDto.Cancelled, false)));
    }

    [Fact]
    public void Turkish_dictionary_reproduces_the_composed_texts()
    {
        // Birleştirilmiş metinler Türkçe sözlükle eski sabit metnin verdiği sonucu üretmelidir.
        var texts = Dictionary("tr");
        string Format(string key, params object?[] args) => string.Format(CultureInfo.InvariantCulture, texts[key], args);

        Assert.Equal("Acme açıldı. Ayşe zaten Modulix kullanıyordu; yönetici olarak eklendi ve bilgilendirildi.", Format("platform.tenants.createdExisting", "Acme", "Ayşe"));
        Assert.Equal("Acme açıldı. a@b.c adresine yönetici daveti gönderildi.", Format("platform.tenants.createdInvited", "Acme", "a@b.c"));
        Assert.Equal("30 günde 1 · 60 günde 2 · 90 günde 3", Format("platform.home.expiringCounts", 1, 2, 3));
        Assert.Equal("4 hakkın süresi geçti (1 salt okunur)", Format("platform.home.expired", 4, 1));
        Assert.Equal("Acme: CRM kapatıldı. Veri silinmedi.", Format("platform.moduleRights.disabled", "Acme", "CRM"));
        Assert.Equal("CRM açıldı.", Format("platform.tenantDetail.moduleEnabled", "CRM"));
        Assert.Equal("Acme için CRM kapatılsın mı?", Format("platform.moduleToggle.confirm", "Acme", "CRM"));
        Assert.Equal("Lojistik profili uygulandı: 2 modül, 3 alan.", Format("platform.tenantDetail.profileApplied", "Lojistik", "2 modül, 3 alan"));
        Assert.Equal("5 gün kaldı", Format("platform.expirations.daysLeft", "5"));
        Assert.Equal("%12,5 (1 / 8)", Format("platform.health.rate", "12,5", 1, 8));
        Assert.Equal("2 sa 5 dk", Format("platform.age.hoursMinutes", 2, 5));
        Assert.Equal("3 teslim yeniden denenecek.", Format("platform.events.retried", 3));
        Assert.Equal("CRM: kapatıldı", Format("platform.auditList.moduleOff", "CRM"));
        Assert.Equal("Fırsat: rol 1 · kısılan 2 · serbest 3 · yönetici kısılan 4 · serbest 5", Format("platform.scopeLimits.previewType", "Fırsat", 1, 2, 3, 4, 5));
        Assert.Equal(
            "Platform işlemleri için iki adımlı doğrulama zorunludur; açana kadar sunucu bu alandaki istekleri reddeder. <a>Hesabım</a> sayfasından açın.",
            Format("platform.gate.twoFactor", "<a>", "</a>"));
    }
}
