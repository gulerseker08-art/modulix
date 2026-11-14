using System.Text.Json;
using Modulix.Shared.Audit;
using Modulix.Shared.Customization;
using Modulix.Shared.Imports;
using Modulix.Web.Services;

namespace Modulix.Web.Tests;

/// <summary>
/// ADR-0034 (dilim 16c): Ayarlar, özel alanlar, form tasarımı, alt profiller, iletişim tercihleri, denetim kaydı ve içe aktarma ekranlarının
/// etiket tabloları (<see cref="SettingsLabels"/>) sözlükten okunur. Sözlük yüklenmemişken kaynak (Türkçe) metin gösterilir; testler her kaynak
/// satırının üç sözlükte karşılığı olduğunu, Türkçe sözlüğün paylaşılan etiketlerle aynı kaldığını, tabloların bütün değerleri kapsadığını ve
/// birleştirilmiş Türkçe metinlerin eski sabit metinle aynı çıktığını denetler.
/// </summary>
public class SettingsLabelsTests
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
        (SettingsLabels.FieldTypeGroup, SettingsLabels.FieldTypes),
        (SettingsLabels.SpanGroup, SettingsLabels.Spans),
        (SettingsLabels.AuditActionGroup, SettingsLabels.AuditActions),
        (SettingsLabels.AuditSourceGroup, SettingsLabels.AuditSources),
        (SettingsLabels.SubProfileStatusGroup, SettingsLabels.SubProfileStatusLabels),
        (SettingsLabels.SubProfileOptionGroup, SettingsLabels.SubProfileOptionLabels),
        (SettingsLabels.RuleEffectGroup, SettingsLabels.RuleEffects),
        (SettingsLabels.ConsentStatusGroup, SettingsLabels.ConsentStatuses),
        (SettingsLabels.ConsentPurposeGroup, SettingsLabels.ConsentPurposes),
        (SettingsLabels.SuppressionReasonGroup, SettingsLabels.SuppressionReasons),
        (SettingsLabels.ImportStatusGroup, SettingsLabels.ImportStatuses),
        (SettingsLabels.ImportModeGroup, SettingsLabels.ImportModes),
        (SettingsLabels.ImportPolicyGroup, SettingsLabels.ImportPolicies),
        (SettingsLabels.ImportSeverityGroup, SettingsLabels.ImportSeverities),
        (SettingsLabels.ImportCodeGroup, SettingsLabels.ImportCodes),
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
    public void Turkish_dictionary_matches_the_source_labels()
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
        Assert.Equal(Enum.GetValues<CustomFieldType>().Length, SettingsLabels.FieldTypes.Count);
        Assert.Equal(FormSpanLabels.All.Count, SettingsLabels.Spans.Count);
        Assert.Equal(Enum.GetValues<AuditOperation>().Length, SettingsLabels.AuditActions.Count);
        Assert.Equal(Enum.GetValues<AuditSource>().Length, SettingsLabels.AuditSources.Count);
        Assert.Equal(ImportText.Statuses.Count, SettingsLabels.ImportStatuses.Count);
        Assert.Equal(ImportText.Codes.Count, SettingsLabels.ImportCodes.Count);

        // Paylaşılan etiket tablosu bir değeri tanımıyorsa adı olduğu gibi döner; kaynak satırında hiçbir ad İngilizce teknik ad kalmamalı.
        Assert.DoesNotContain(Rows(), r => r.Label == r.Key.Split('.')[^1]);
    }

    [Fact]
    public void Lookups_return_the_source_text()
    {
        Assert.Equal("Çok satırlı metin", SettingsLabels.FieldType(CustomFieldType.MultilineText));
        Assert.Equal("Üçte iki", SettingsLabels.Span(8));
        Assert.Equal("Şifre sıfırlama bağlantısı istedi", SettingsLabels.Audit(AuditOperation.PasswordResetRequested));
        Assert.Equal("Arka plan işi · core.assignment-email", SettingsLabels.Audit(AuditSource.BackgroundJob, "core.assignment-email"));
        Assert.Equal("Etkin", SettingsLabels.SubProfileStatus(SubProfileStatuses.Active));
        Assert.Equal("Liste değeri", SettingsLabels.SubProfileOption(SubProfileOptionKinds.ListValue));
        Assert.Equal("Salt okunur yap", SettingsLabels.RuleEffect("ReadOnly"));
        Assert.Equal("Kalıcı geri dönme", SettingsLabels.SuppressionReason("HardBounce"));
        Assert.Equal("Kısmen başarılı", SettingsLabels.ImportStatus("PartiallySucceeded"));
        Assert.Equal("Dosya daha önce yüklendi", SettingsLabels.ImportCode("file-repeated"));
        Assert.Equal("unknown", SettingsLabels.ImportCode("unknown"));
        Assert.Equal("admin.import-code.required-missing", AdminLabels.KeyOf(SettingsLabels.ImportCodeGroup, "required-missing"));
        Assert.Equal("admin.rule-effect.requireapproval", AdminLabels.KeyOf(SettingsLabels.RuleEffectGroup, "RequireApproval"));
    }

    [Fact]
    public void Turkish_dictionary_reproduces_the_composed_texts()
    {
        // Birleştirilmiş metinler Türkçe sözlükle eski sabit metnin verdiği sonucu üretmelidir.
        var texts = Dictionary("tr");
        Assert.Equal("Kaydet (3 değişiklik)", string.Format(texts["admin.lookups.saveCount"], 3));
        Assert.Equal("Kutuda yazarak arama yapılır (2+ karakter). Aranan alanlar: Ad, Vergi no.", string.Format(texts["admin.lookups.searchOn"], 2, "Ad, Vergi no"));
        Assert.Equal("Yeni sekme 3", string.Format(texts["admin.formDesigner.newTabLabel"], 3));
        Assert.Equal(" (4 seçenek)", " " + string.Format(texts["admin.subProfiles.choiceCount"], 4));
        Assert.Equal("Dosya en fazla 10 MB olabilir.", string.Format(texts["admin.importNew.tooLarge"], 10));
        Assert.Equal(
            "5 kayıt silindi, 1 kayıt zaten silinmişti; 2 kayıt korundu: Değişmiş.",
            string.Format(texts["admin.importRun.removed"], 5) + string.Format(texts["admin.importRun.alreadyRemoved"], 1)
                + string.Format(texts["admin.importRun.kept"], 2, "Değişmiş."));
        Assert.Equal("CRM, Lojistik modülü firmada kapalı; bu modülün alanları ve değerleri eklenmez. Modülü platform yöneticisi açabilir.",
            string.Format(texts["admin.sectorProfile.unavailable"], "CRM, Lojistik"));

        // Form tasarımındaki açıklama, sabit alan ikonunun yerini {0} ile tutar.
        Assert.Equal(2, texts["admin.formDesigner.intro"].Split("{0}").Length);
    }
}
