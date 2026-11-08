using System.Text.Json.Nodes;
using Modulix.Shared.Records;
using Modulix.Web.Services;

namespace Modulix.Web.Tests;

/// <summary>Kayıt seçicide kaydı tanıtan metin (ADR-0027, Dilim 5d): etiket alanı, yoksa kısa kimlik.</summary>
public class RecordPickerLabelsTests
{
    private static RecordResponse Record(Guid id, JsonObject values) => new("crm.lead", id, 1, values);

    [Fact]
    public void The_label_field_text_is_used_trimmed()
    {
        var record = Record(Guid.NewGuid(), new JsonObject { ["subject"] = "  Anka Lojistik  " });

        Assert.Equal("Anka Lojistik", RecordPickerLabels.Label(record, "subject"));
    }

    [Fact]
    public void A_missing_blank_or_non_text_label_falls_back_to_the_short_id()
    {
        var id = Guid.Parse("3f9a1c2e-0000-0000-0000-000000000001");
        var expected = "Kayıt 3f9a1c2e";

        Assert.Equal(expected, RecordPickerLabels.Label(Record(id, new JsonObject()), "subject"));
        Assert.Equal(expected, RecordPickerLabels.Label(Record(id, new JsonObject { ["subject"] = "   " }), "subject"));
        Assert.Equal(expected, RecordPickerLabels.Label(Record(id, new JsonObject { ["subject"] = null }), "subject"));
        Assert.Equal(expected, RecordPickerLabels.Label(Record(id, new JsonObject { ["subject"] = 42 }), "subject"));
        Assert.Equal(expected, RecordPickerLabels.Label(Record(id, new JsonObject { ["subject"] = new JsonObject() }), "subject"));
    }

    [Fact]
    public void Without_a_label_field_every_record_is_shown_by_its_short_id()
    {
        var id = Guid.Parse("abcdef01-0000-0000-0000-000000000002");
        var record = Record(id, new JsonObject { ["subject"] = "Görünmemeli" });

        Assert.Equal("Kayıt abcdef01", RecordPickerLabels.Label(record, null));
        Assert.Equal("Kayıt abcdef01", RecordPickerLabels.Label(record, ""));
        Assert.Null(RecordPickerLabels.TextOf(null, "subject"));
    }

    [Fact]
    public void An_older_catalog_payload_without_a_label_field_still_works()
    {
        // Eski yanıtlarda alan yoktur; varsayılan null kalır (geriye uyum) ve seçici kısa kimliğe düşer.
        var json = """{"moduleKey":"crm","entityKey":"crm.lead","displayName":"Müşteri adayı","fields":[],"events":[],"relations":[]}""";

        var entity = System.Text.Json.JsonSerializer.Deserialize<Modulix.Shared.Automation.AutomationEntityDto>(
            json, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;

        Assert.Null(entity.LabelField);
    }
}
