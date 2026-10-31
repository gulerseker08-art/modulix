using System.Globalization;
using Modulix.Modules.Contracts.Lifecycle;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Modulix.Api.Contracts;

/// <summary>
/// Sözleşme belgesi (PDF) çizici (ADR-0022, dilim 4). Girdisi yalnızca render edilmiş bloklardır (<see cref="ContractPdfModel"/>): canlı sözleşme,
/// müşteri kartı ya da şablon buraya girmez. Başlık satırı her sayfada yinelenen uzun kalem tablosu, sayfa sonu, "Sayfa x / y" alt bilgisi ve
/// Türkçe karakterler desteklenir. Örnek şablonlar ve kesinleşmemiş (taslak kopya) belgeler her sayfanın üstünde açıkça işaretlenir.
/// Üretim tarihi modelden gelir (saat değil): aynı girdiden üretilen belgenin üst bilgisi sabittir.
/// </summary>
public static class ContractPdf
{
    /// <summary>Çizim sürümü: görünüm değiştiğinde artırılır.</summary>
    public const int RendererVersion = 1;

    private static readonly string Accent = "#1F4E79";
    private static readonly string Muted = "#5F6B7A";
    private static readonly string Line = "#C9D1DA";
    private static readonly string Warning = "#B42318";

    static ContractPdf()
    {
        // QuestPDF Community lisansı: yıllık geliri 1 milyon USD altındaki şirketler ve açık kaynak için ücretsizdir (docs/adr/0019-teklif-yonetimi.md).
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public static byte[] Render(ContractPdfModel model) => Build(model).GeneratePdf();

    /// <summary>Belge tanımı (PDF üretimi ve görsel denetim için ortak).</summary>
    public static QuestPDF.Infrastructure.IDocument Build(ContractPdfModel model)
    {
        var document = Document.Create(container => container.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(40);
            page.DefaultTextStyle(style => style.FontSize(10).FontColor("#1B2430").LineHeight(1.35f));
            page.Header().Element(header => Header(header, model));
            page.Content().PaddingTop(10).Column(column =>
            {
                column.Spacing(8);
                foreach (var block in model.Blocks)
                {
                    Block(column, block);
                }
            });
            page.Footer().Element(footer => Footer(footer, model));
        }));

        document.WithMetadata(new DocumentMetadata
        {
            Title = $"{model.ContractNumber} V{model.ContractVersionNo} - {model.Title}",
            Author = model.ProviderName ?? "ModüliX",
            Subject = "Sözleşme",
            Creator = "ModüliX",
            Producer = "ModüliX",
            CreationDate = model.GeneratedAt,
            ModifiedDate = model.GeneratedAt,
        });
        return document;
    }

    private static void Header(IContainer container, ContractPdfModel model) =>
        container.Column(column =>
        {
            if (model.IsExample)
            {
                column.Item().Background("#FEF3F2").Padding(4).Text("ÖRNEK METİN — hukuki inceleme yapılmamıştır; kurumunuzun onaylı metniyle değiştirin.").Bold().FontSize(8).FontColor(Warning);
            }

            if (!model.IsFinal)
            {
                column.Item().Background("#FFFAEB").Padding(4).Text("TASLAK KOPYA — içerik henüz onaya/imzaya gönderilmemiştir ve değişebilir.").Bold().FontSize(8).FontColor("#B54708");
            }

            column.Item().PaddingTop(model.IsExample || !model.IsFinal ? 6 : 0).Row(row =>
            {
                row.RelativeItem().Column(provider =>
                {
                    if (model.ProviderName is { Length: > 0 } name)
                    {
                        provider.Item().Text(name).FontSize(14).Bold().FontColor(Accent);
                    }

                    if (model.ProviderDetails is { Length: > 0 } details)
                    {
                        provider.Item().Text(details).FontSize(8).FontColor(Muted);
                    }
                });
                row.ConstantItem(200).AlignRight().Column(info =>
                {
                    info.Item().Text($"Sözleşme no: {model.ContractNumber}").FontSize(9).Bold();
                    info.Item().Text($"Sürüm: V{model.ContractVersionNo}").FontSize(8).FontColor(Muted);
                    info.Item().Text($"Belge tarihi: {model.GeneratedAt.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)}").FontSize(8).FontColor(Muted);
                });
            });
            column.Item().PaddingTop(4).LineHorizontal(0.75f).LineColor(Line);
        });

    private static void Footer(IContainer container, ContractPdfModel model) =>
        container.Column(column =>
        {
            column.Item().LineHorizontal(0.5f).LineColor(Line);
            column.Item().PaddingTop(3).Row(row =>
            {
                row.RelativeItem().Text(text =>
                {
                    text.DefaultTextStyle(style => style.FontSize(7.5f).FontColor(Muted));
                    text.Span(model.FooterNote is { Length: > 0 } note ? note + " · " : string.Empty);
                    text.Span($"{model.TemplateName} (şablon V{model.TemplateVersionNo})");
                });
                row.ConstantItem(90).AlignRight().Text(text =>
                {
                    text.DefaultTextStyle(style => style.FontSize(8).FontColor(Muted));
                    text.Span("Sayfa ");
                    text.CurrentPageNumber();
                    text.Span(" / ");
                    text.TotalPages();
                });
            });
        });

    private static void Block(ColumnDescriptor column, DocBlock block)
    {
        switch (block)
        {
            case HeadingBlock { Level: 1 } h:
                column.Item().PaddingTop(4).Text(h.Text).FontSize(15).Bold().FontColor(Accent);
                break;
            case HeadingBlock h:
                column.Item().PaddingTop(2).Text(h.Text).FontSize(11.5f).Bold().FontColor(Accent);
                break;
            case ParagraphBlock p:
                column.Item().Text(p.Text);
                break;
            case TableBlock t:
                column.Item().Element(c => LineTable(c, t));
                break;
            case PartyListBlock list:
                column.Item().Column(items =>
                {
                    foreach (var item in list.Items)
                    {
                        items.Item().Text($"• {item}");
                    }
                });
                break;
            case SignatureBlock s:
                // İmza alanı sayfa arasında bölünmez.
                column.Item().PaddingTop(14).ShowEntire().Row(row =>
                {
                    row.Spacing(24);
                    foreach (var slot in s.Slots)
                    {
                        row.RelativeItem().Column(box =>
                        {
                            box.Item().Text(slot.Title).Bold();
                            box.Item().Height(46);
                            box.Item().LineHorizontal(0.75f).LineColor("#1B2430");
                            box.Item().PaddingTop(2).Text(slot.Name is { Length: > 0 } name ? name : " ").FontSize(9);
                            box.Item().Text("Ad Soyad / Kaşe / İmza / Tarih").FontSize(7).FontColor(Muted);
                        });
                    }
                });
                break;
            case PageBreakBlock:
                column.Item().PageBreak();
                break;
        }
    }

    /// <summary>Kalem tablosu: başlık satırı her sayfada yinelenir; satırlar sayfa arasında bölünmez.</summary>
    private static void LineTable(IContainer container, TableBlock block) =>
        container.Column(column =>
        {
            column.Item().Table(table =>
            {
                table.ColumnsDefinition(columns =>
                {
                    columns.ConstantColumn(18);
                    columns.RelativeColumn(3.2f);
                    columns.RelativeColumn(1.2f);
                    columns.RelativeColumn(1.6f);
                    columns.ConstantColumn(34);
                    columns.RelativeColumn(1.1f);
                    columns.RelativeColumn(1.5f);
                    columns.RelativeColumn(1.5f);
                });
                table.Header(header =>
                {
                    foreach (var title in block.Header)
                    {
                        header.Cell().Background(Accent).Padding(3).Text(title).FontSize(8).Bold().FontColor(Colors.White);
                    }
                });
                foreach (var row in block.Rows)
                {
                    foreach (var cell in row)
                    {
                        table.Cell().ShowEntire().BorderBottom(0.5f).BorderColor(Line).Padding(3).Text(cell).FontSize(8.5f);
                    }
                }
            });
            if (block.Totals.Count > 0)
            {
                column.Item().PaddingTop(6).AlignRight().ShowEntire().Column(totals =>
                {
                    foreach (var line in block.Totals)
                    {
                        totals.Item().Text(line).Bold().FontSize(9);
                    }
                });
            }
        });
}

/// <summary>Sözleşme PDF çizicisinin host uygulaması (<see cref="IContractPdfRenderer"/>).</summary>
internal sealed class ContractPdfRenderer : IContractPdfRenderer
{
    public byte[] Render(ContractPdfModel model) => ContractPdf.Render(model);
}
