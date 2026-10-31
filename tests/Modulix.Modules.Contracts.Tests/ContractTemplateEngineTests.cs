using Modulix.Modules.Contracts.Lifecycle;

namespace Modulix.Modules.Contracts.Tests;

/// <summary>ADR-0022 dilim 4: şablon motoru. Kontrollü değişkenler, kod/HTML'in etkisizliği, eksik veri, bloklar, Türkçe karakterler, örnek metin.</summary>
public class ContractTemplateEngineTests
{
    private static ContractDocumentData Data(Dictionary<string, string?>? values = null, int lineCount = 2)
    {
        var all = new Dictionary<string, string?>
        {
            ["sozlesme.numara"] = "SZ-20261031-ABC123",
            ["sozlesme.baslik"] = "Yıllık bakım hizmeti",
            ["musteri.unvan"] = "Çağlayan Şişecam Gıda A.Ş.",
            ["sozlesme.baslangic_tarihi"] = "01.11.2026",
            ["sozlesme.bitis_tarihi"] = "31.10.2027",
        };
        foreach (var (key, value) in values ?? [])
        {
            all[key] = value;
        }

        var rows = Enumerable.Range(1, lineCount).Select(i => (IReadOnlyList<string>)[i.ToString(), $"Kalem {i}", "1", "100,00 TRY", "20", "Tek seferlik", "100,00 TRY", "120,00 TRY"]).ToList();
        return new ContractDocumentData(all, rows, ["#", "Kalem", "Miktar", "Birim fiyat", "Vergi %", "Dönem", "Net", "Toplam"], ["Genel toplam: 240,00 TRY"],
            ["Müşteri: Çağlayan Şişecam Gıda A.Ş."], [new SignatureSlot("Müşteri adına", "İğdır Öğüt"), new SignatureSlot("Hizmet veren adına", "Modül A.Ş.")]);
    }

    [Fact]
    public void Variables_headings_and_paragraphs_are_rendered_and_turkish_characters_survive()
    {
        var body = "# SÖZLEŞME {{sozlesme.numara}}\n\nMüşteri: {{musteri.unvan}}\nBaşlangıç: {{sozlesme.baslangic_tarihi}}\n\n## Konu\n\n{{sozlesme.baslik}}";
        Assert.Empty(ContractTemplateEngine.Validate(body));

        var render = ContractTemplateEngine.Render(body, Data());
        Assert.Empty(render.Missing);
        Assert.Equal(new HeadingBlock(1, "SÖZLEŞME SZ-20261031-ABC123"), render.Blocks[0]);
        Assert.Equal(new ParagraphBlock("Müşteri: Çağlayan Şişecam Gıda A.Ş.\nBaşlangıç: 01.11.2026"), render.Blocks[1]);
        Assert.Equal(new HeadingBlock(2, "Konu"), render.Blocks[2]);
        Assert.Equal(new ParagraphBlock("Yıllık bakım hizmeti"), render.Blocks[3]);
    }

    [Theory]
    [InlineData("{{bilinmeyen.alan}}", "Bilinmeyen değişken")]
    [InlineData("{{ Musteri.Unvan }}", "Geçersiz değişken söz dizimi")]
    [InlineData("{{musteri.unvan", "Kapanmamış")]
    [InlineData("a }} b", "Kapanmamış")]
    [InlineData("{% if x %}", "yönergeleri")]
    [InlineData("{{musteri.unvan + 1}}", "Geçersiz değişken söz dizimi")]
    [InlineData("{{kalem_tablosu}} metin", "tek başına")]
    [InlineData("Önce {{imza_alani}}", "tek başına")]
    public void Unknown_malformed_expression_and_misplaced_block_variables_are_rejected(string body, string expected)
    {
        var issues = ContractTemplateEngine.Validate(body);
        Assert.Contains(issues, i => i.Message.Contains(expected, StringComparison.Ordinal));
        Assert.Throws<InvalidOperationException>(() => ContractTemplateEngine.Render(body, Data()));
    }

    [Fact]
    public void Empty_and_oversized_bodies_are_rejected()
    {
        Assert.Contains(ContractTemplateEngine.Validate("  "), i => i.Message.Contains("boş"));
        Assert.Contains(ContractTemplateEngine.Validate(new string('a', ContractTemplateEngine.BodyMaxLength + 1)), i => i.Message.Contains("en fazla"));
    }

    [Fact]
    public void Html_and_template_syntax_inside_values_are_inert_text_and_never_re_evaluated()
    {
        var hostile = "<script>alert(1)</script> {{sozlesme.numara}} {% x %}";
        var render = ContractTemplateEngine.Render("Müşteri: {{musteri.unvan}}", Data(new() { ["musteri.unvan"] = hostile }));

        // Değer olduğu gibi metin olarak basılır: HTML yorumlanmaz, içindeki {{...}} yeniden değiştirilmez.
        Assert.Equal(new ParagraphBlock("Müşteri: " + hostile), Assert.Single(render.Blocks));
        Assert.Empty(render.Missing);
    }

    [Fact]
    public void Html_in_the_template_body_is_plain_text_not_markup()
    {
        var render = ContractTemplateEngine.Render("<b>kalın</b> <img src=x onerror=alert(1)>", Data());
        Assert.Equal(new ParagraphBlock("<b>kalın</b> <img src=x onerror=alert(1)>"), Assert.Single(render.Blocks));
    }

    [Fact]
    public void Missing_values_are_listed_by_label_and_only_for_variables_that_are_used()
    {
        var render = ContractTemplateEngine.Render("Vergi no: {{musteri.vergi_no}} / yetkili: {{musteri.yetkili}} / {{musteri.vergi_no}}", Data());
        Assert.Equal(["Müşteri vergi / kimlik no", "Müşteri yetkilisi (ad soyad)"], render.Missing);

        var complete = ContractTemplateEngine.Render("Ünvan: {{musteri.unvan}}", Data());
        Assert.Empty(complete.Missing);
        Assert.Equal(["musteri.vergi_no", "musteri.yetkili"], ContractTemplateEngine.UsedVariables("{{musteri.vergi_no}} {{musteri.yetkili}} {{musteri.vergi_no}}"));
    }

    [Fact]
    public void Blocks_render_tables_parties_signatures_and_page_breaks_and_an_empty_line_table_is_missing()
    {
        var body = "{{taraflar_listesi}}\n\n{{kalem_tablosu}}\n\n{{sayfa_sonu}}\n\n{{imza_alani}}";
        var render = ContractTemplateEngine.Render(body, Data(lineCount: 120));
        Assert.Empty(render.Missing);
        Assert.IsType<PartyListBlock>(render.Blocks[0]);
        var table = Assert.IsType<TableBlock>(render.Blocks[1]);
        Assert.Equal(120, table.Rows.Count);
        Assert.IsType<PageBreakBlock>(render.Blocks[2]);
        Assert.Equal(2, Assert.IsType<SignatureBlock>(render.Blocks[3]).Slots.Count);

        var empty = ContractTemplateEngine.Render("{{kalem_tablosu}}", Data(lineCount: 0));
        Assert.Contains("Kalem tablosu (sözleşmede kalem yok)", empty.Missing);
    }

    [Fact]
    public void Financial_variables_are_detected_so_the_permission_can_be_required()
    {
        Assert.True(ContractTemplateEngine.UsesFinancials("Toplam: {{sozlesme.genel_toplam}}"));
        Assert.True(ContractTemplateEngine.UsesFinancials("{{kalem_tablosu}}"));
        Assert.False(ContractTemplateEngine.UsesFinancials("Ünvan: {{musteri.unvan}} {{imza_alani}}"));
    }

    [Fact]
    public void The_sample_template_is_valid_and_marked_as_an_example()
    {
        var body = ContractTemplateSamples.ServiceBody.Replace("\r\n", "\n");
        Assert.Empty(ContractTemplateEngine.Validate(body));
        Assert.Contains("hukuki inceleme yapılmamıştır", ContractTemplateSamples.Notice);
        Assert.Contains("hukuki inceleme yapılmamıştır", ContractTemplateSamples.ServiceName);
        Assert.True(ContractTemplateEngine.UsedVariables(body).Count > 10);
    }

    [Fact]
    public void Every_catalog_variable_key_is_valid_and_unique_and_blocks_are_flagged()
    {
        var keys = ContractTemplateVariables.All.Select(v => v.Key).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
        Assert.All(keys, key => Assert.Empty(ContractTemplateEngine.Validate(ContractTemplateVariables.Find(key)!.IsBlock ? $"{{{{{key}}}}}" : $"x {{{{{key}}}}}")));
        Assert.Equal(4, ContractTemplateVariables.All.Count(v => v.IsBlock));
    }

    [Theory]
    [InlineData("Yıllık Bakım Sözleşmesi (Şişecam)", "yillik-bakim-sozlesmesi-sisecam")]
    [InlineData("ÇĞİÖŞÜ çğıöşü", "cgiosu-cgiosu")]
    [InlineData("!!!", "belge")]
    public void File_name_slug_is_ascii_and_bounded(string text, string expected)
    {
        Assert.Equal(expected, ContractTemplateEngine.Slug(text));
    }

    [Fact]
    public void Money_and_date_use_turkish_formats()
    {
        Assert.Equal("1.234,50 TRY", ContractTemplateEngine.Money(1234.5m, "TRY"));
        Assert.Equal("01.11.2026", ContractTemplateEngine.Date(new DateOnly(2026, 11, 1)));
    }
}
