using System.Text.Json;
using Modulix.Web.Services;

namespace Modulix.Web.Tests;

/// <summary>
/// ADR-0034 (dilim 10a): Sözleşme modülünün etiket tabloları (<see cref="ContractLabels"/>, <see cref="ContractActionLabels"/>,
/// <see cref="TemplateLabels"/>, <see cref="SignatureLabels"/>, <see cref="CommitmentLabels"/>, <see cref="RenewalLabels"/>,
/// <see cref="ViewLabels"/>) sözlükten okunur. Sözlük yüklenmemişken kaynak (Türkçe) metin gösterilir; testler her kaynak satırının üç
/// sözlükte karşılığı olduğunu ve Türkçe sözlüğün kaynakla aynı kaldığını denetler.
/// </summary>
public class ContractLabelsTests
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
        (ContractLabels.PreparationGroup, ContractLabels.Preparations),
        (ContractLabels.ApprovalGroup, ContractLabels.Approvals),
        (ContractLabels.SignatureGroup, ContractLabels.Signatures),
        (ContractLabels.EffectiveGroup, ContractLabels.Effectives),
        (ContractLabels.RenewalModeGroup, ContractLabels.RenewalModes),
        (ContractLabels.ConfidentialityGroup, ContractLabels.Confidentialities),
        (ContractLabels.RiskLevelGroup, ContractLabels.RiskLevels),
        (ContractLabels.LineKindGroup, ContractLabels.LineKinds),
        (ContractLabels.PeriodGroup, ContractLabels.Periods),
        (ContractLabels.PartyRoleGroup, ContractLabels.PartyRoles),
        (ContractActionLabels.ActionGroup, ContractActionLabels.Actions),
        (ContractActionLabels.EventGroup, ContractActionLabels.Events),
        (ContractActionLabels.VersionStatusGroup, ContractActionLabels.VersionStatuses),
        (ContractActionLabels.FieldGroup, ContractActionLabels.Fields),
        (ContractActionLabels.ChangeGroup, ContractActionLabels.Changes),
        (TemplateLabels.StatusGroup, TemplateLabels.Statuses),
        (TemplateLabels.ActionGroup, TemplateLabels.Actions),
        (SignatureLabels.RequestStatusGroup, SignatureLabels.RequestStatuses),
        (SignatureLabels.SignatoryStatusGroup, SignatureLabels.SignatoryStatuses),
        (SignatureLabels.AssuranceGroup, SignatureLabels.Assurances),
        (SignatureLabels.EventTypeGroup, SignatureLabels.EventTypes),
        (SignatureLabels.OutcomeGroup, SignatureLabels.Outcomes),
        (CommitmentLabels.ObligorGroup, CommitmentLabels.Obligors),
        (CommitmentLabels.KindGroup, CommitmentLabels.Kinds),
        (CommitmentLabels.RecurrenceGroup, CommitmentLabels.Recurrences),
        (CommitmentLabels.DirectionGroup, CommitmentLabels.Directions),
        (CommitmentLabels.StatusGroup, CommitmentLabels.Statuses),
        (CommitmentLabels.OccurrenceStatusGroup, CommitmentLabels.OccurrenceStatuses),
        (CommitmentLabels.FollowUpGroup, CommitmentLabels.FollowUps),
        (RenewalLabels.OutcomeGroup, RenewalLabels.Outcomes),
        (RenewalLabels.ReasonGroup, RenewalLabels.Reasons),
        (RenewalLabels.SourceGroup, RenewalLabels.Sources),
        (RenewalLabels.ReminderKindGroup, RenewalLabels.ReminderKinds),
        (RenewalLabels.ReminderStatusGroup, RenewalLabels.ReminderStatuses),
        (RenewalLabels.RelationGroup, RenewalLabels.Relations),
        (ViewLabels.PresetGroup, ViewLabels.Presets),
        (ViewLabels.SortGroup, ViewLabels.Sorts),
        (ViewLabels.CounterGroup, ViewLabels.Counters),
    ];

    [Theory]
    [InlineData("tr")]
    [InlineData("en")]
    [InlineData("az")]
    public void Every_table_row_has_a_text_in_each_language(string language)
    {
        var texts = Dictionary(language);
        var missing = Tables()
            .SelectMany(t => t.Rows.Select(r => ContractText.KeyOf(t.Group, r.Key)))
            .Where(k => !texts.TryGetValue(k, out var text) || string.IsNullOrWhiteSpace(text))
            .ToList();

        Assert.True(missing.Count == 0, string.Join(Environment.NewLine, missing));
    }

    [Fact]
    public void Turkish_dictionary_matches_the_source_labels()
    {
        var texts = Dictionary("tr");
        var different = Tables()
            .SelectMany(t => t.Rows.Select(r => (Key: ContractText.KeyOf(t.Group, r.Key), r.Label)))
            .Where(r => texts[r.Key] != r.Label)
            .Select(r => $"{r.Key}: {texts[r.Key]} != {r.Label}")
            .ToList();

        Assert.True(different.Count == 0, string.Join(Environment.NewLine, different));
    }

    [Theory]
    [InlineData("tr")]
    [InlineData("en")]
    [InlineData("az")]
    public void Shared_contract_texts_exist_in_each_language(string language)
    {
        var texts = Dictionary(language);
        string[] keys = ["contracts.hiddenAmount", "contracts.days.today", "contracts.days.left", "contracts.days.past", "contracts.indefinite", "contracts.versionNo"];

        Assert.All(keys, key => Assert.False(string.IsNullOrWhiteSpace(texts.GetValueOrDefault(key)), key));
    }

    [Fact]
    public void Lookups_return_the_source_text_for_known_keys_and_the_key_otherwise()
    {
        Assert.Equal("Onaya gönder", ContractActionLabels.Action("submit-for-approval"));
        Assert.Equal("unknown-action", ContractActionLabels.Action("unknown-action"));
        Assert.Equal("Kabul edilmiş tekliften oluşturuldu", ContractActionLabels.History("CreatedFromQuote"));
        Assert.Equal("İlgili kişi", ContractActionLabels.Field("personId"));
        Assert.Equal("Değişti", ContractActionLabels.Change("Changed"));
        Assert.Equal("Arşivle", TemplateLabels.Action("archive"));
        Assert.Equal(string.Empty, TemplateLabels.Action(null));
        Assert.Equal("Tek seferlik", ContractLabels.Label(ContractLabels.Periods, string.Empty));
        Assert.Equal("Kefil / garantör", ContractLabels.Label(ContractLabels.PartyRoles, "Guarantor"));
        Assert.Equal("Other", ViewLabels.Label(ViewLabels.Presets, "Other"));
    }

    [Fact]
    public void Empty_key_maps_to_the_one_time_text()
    {
        Assert.Equal("contracts.period.onetime", ContractText.KeyOf(ContractLabels.PeriodGroup, string.Empty));
        Assert.Equal("contracts.sort.-createdat", ContractText.KeyOf(ViewLabels.SortGroup, "-createdAt"));
    }
}
