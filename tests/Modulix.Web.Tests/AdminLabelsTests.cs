using System.Text.Json;
using Modulix.Shared.Access;
using Modulix.Shared.Users;
using Modulix.Web.Services;

namespace Modulix.Web.Tests;

/// <summary>
/// ADR-0034 (dilim 16a): Yönetim ekranlarının etiket tabloları (<see cref="AdminLabels"/>) sözlükten okunur. Sözlük yüklenmemişken kaynak
/// (Türkçe) metin gösterilir; testler her kaynak satırının üç sözlükte karşılığı olduğunu ve Türkçe sözlüğün paylaşılan sözleşmelerdeki
/// etiketlerle aynı kaldığını denetler.
/// </summary>
public class AdminLabelsTests
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
        (AdminLabels.UserStatusGroup, AdminLabels.UserStatuses),
        (AdminLabels.ScopeGroup, AdminLabels.Scopes),
        (AdminLabels.ScopeDescriptionGroup, AdminLabels.ScopeDescriptions),
        (AdminLabels.AssignLevelGroup, AdminLabels.AssignLevels),
        (AdminLabels.UnitKindGroup, AdminLabels.UnitKinds),
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
    public void Tables_cover_every_enum_value()
    {
        Assert.Equal(Enum.GetValues<UserStatus>().Length, AdminLabels.UserStatuses.Count);
        Assert.Equal(Enum.GetValues<RecordScope>().Length, AdminLabels.Scopes.Count);
        Assert.Equal(Enum.GetValues<RecordScope>().Length, AdminLabels.ScopeDescriptions.Count);
        Assert.Equal(Enum.GetValues<RoleAssignLevel>().Length, AdminLabels.AssignLevels.Count);
        Assert.Equal(BusinessUnitKindLabels.Labels.Count, AdminLabels.UnitKinds.Count);
    }

    [Fact]
    public void Lookups_return_the_source_text()
    {
        Assert.Equal("Davet bekliyor", AdminLabels.Status(UserStatus.Invited));
        Assert.Equal("Departmanın ve alt departmanların kayıtları", AdminLabels.Scope(RecordScope.UnitAndBelow));
        Assert.Equal("Yalnızca sorumlusu kendisi olan kayıtlar.", AdminLabels.ScopeDescription(RecordScope.Own));
        Assert.Equal("Kullanıcıya ve departmana", AdminLabels.AssignLevel(RoleAssignLevel.Both));
        Assert.Equal("Bayi", AdminLabels.UnitKind(BusinessUnitKindLabels.Dealer));
        Assert.Equal("unknown", AdminLabels.UnitKind("unknown"));
        Assert.Equal("admin.scope.unitandbelow", AdminLabels.KeyOf(AdminLabels.ScopeGroup, nameof(RecordScope.UnitAndBelow)));
    }

    [Fact]
    public void Turkish_dictionary_reproduces_the_composed_texts()
    {
        // Birleştirilmiş metinler Türkçe sözlükle eski sabit metnin verdiği sonucu üretmelidir.
        var texts = Dictionary("tr");
        Assert.Equal("Ayşe zaten Modulix kullanıyordu; kuruluşunuza eklendi ve bilgilendirildi.", string.Format(texts["admin.users.existingAdded"], "Ayşe"));
        Assert.Equal("Satış: 8 izin", string.Format(texts["admin.access.roleSummary"], "Satış", 8));
        Assert.Equal("Tüm kayıtlar + Ekibinin kayıtları (birleşimi)", string.Format(texts["admin.access.union"], texts["admin.scope.all"] + " + " + texts["admin.scope.team"]));
        Assert.Equal("Rol (kopya)", string.Format(texts["admin.roleDetail.copyName"], "Rol"));
    }
}
