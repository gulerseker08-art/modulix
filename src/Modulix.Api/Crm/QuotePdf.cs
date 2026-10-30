using System.Globalization;
using Modulix.Modules.Crm.Quotes;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Modulix.Api.Crm;

/// <summary>
/// Müşteri teklif belgesi (PDF). Girdisi yalnızca sabitlenmiş belge verisidir (<see cref="QuoteDocumentSnapshot"/>): canlı katalog,
/// firma kartı, maliyet ve iç not buraya hiç girmez. Tutarlar sunucudaki hesap motorunun sonucudur; burada yeniden hesaplanmaz.
/// Üretilen dosya SHA-256 özetiyle saklanır; şablon değişse de eski belge eski haliyle kalır (<see cref="TemplateVersion"/>).
/// </summary>
public static class QuotePdf
{
    /// <summary>Şablon sürümü: görünüm değiştiğinde artırılır; belgeyle birlikte saklanır.</summary>
    public const int TemplateVersion = 1;

    private static readonly NumberFormatInfo Numbers = new() { NumberDecimalSeparator = ",", NumberGroupSeparator = ".", NumberGroupSizes = [3] };
    private static readonly string Accent = "#1F4E79";
    private static readonly string Muted = "#5F6B7A";
    private static readonly string Line = "#C9D1DA";

    static QuotePdf()
    {
        // QuestPDF Community lisansı: yıllık geliri 1 milyon USD altındaki şirketler ve açık kaynak için ücretsizdir; aşan kullanım ücretli
        // lisans gerektirir (docs/adr/0019-teklif-yonetimi.md).
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public static byte[] Render(QuoteDocumentSnapshot quote) => Build(quote).GeneratePdf();

    /// <summary>Belge tanımı (PDF üretimi ve görsel denetim için ortak).</summary>
    public static QuestPDF.Infrastructure.IDocument Build(QuoteDocumentSnapshot quote)
    {
        var document = Document.Create(container => container.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(32);
            page.DefaultTextStyle(style => style.FontSize(9).FontColor("#1B2430"));
            page.Header().Element(header => Header(header, quote));
            page.Content().PaddingTop(12).Element(content => Body(content, quote));
            page.Footer().Element(footer => Footer(footer, quote));
        }));

        // Üretim anı değil sunum anı: aynı veriden üretilen belgenin üst bilgisi sabit kalır.
        document.WithMetadata(new DocumentMetadata
        {
            Title = $"{quote.QuoteNumber} V{quote.RevisionNo} - {quote.Title}",
            Author = quote.Seller.Name,
            Subject = "Teklif",
            Creator = "ModüliX",
            Producer = "ModüliX",
            CreationDate = quote.PresentedAt,
            ModifiedDate = quote.PresentedAt,
        });
        return document;
    }

    public static string Money(decimal amount, string currency) => $"{amount.ToString("N2", Numbers)} {currency}";

    private static string Date(DateTimeOffset value) => value.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);

    private static string Quantity(decimal value, string? unit) => $"{value.ToString("#,0.####", Numbers)}{(unit is null ? string.Empty : $" {unit}")}";

    private static void Header(IContainer container, QuoteDocumentSnapshot quote) =>
        container.Column(column =>
        {
            column.Item().Row(row =>
            {
                row.RelativeItem().Column(seller =>
                {
                    seller.Item().Text(quote.Seller.Name).FontSize(16).Bold().FontColor(Accent);
                    if (quote.Seller.Details is { Length: > 0 } details)
                    {
                        seller.Item().Text(details).FontColor(Muted).FontSize(8);
                    }
                });
                row.ConstantItem(190).AlignRight().Column(info =>
                {
                    info.Item().Text("TEKLİF").FontSize(16).Bold().FontColor(Accent);
                    info.Item().Text($"No: {quote.QuoteNumber}  ·  Revizyon V{quote.RevisionNo}").FontSize(8);
                    info.Item().Text($"Tarih: {quote.IssueDate.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)}").FontSize(8);
                    if (quote.ValidUntil is { } until)
                    {
                        info.Item().Text($"Geçerlilik: {Date(until)}").FontSize(8).Bold();
                    }
                });
            });
            column.Item().PaddingTop(6).LineHorizontal(1).LineColor(Accent);
        });

    private static void Body(IContainer container, QuoteDocumentSnapshot quote) =>
        container.Column(column =>
        {
            column.Spacing(10);
            column.Item().Row(row =>
            {
                row.RelativeItem().Column(customer =>
                {
                    customer.Item().Text("SAYIN").FontSize(7).FontColor(Muted).Bold();
                    customer.Item().Text(quote.CustomerName).FontSize(11).Bold();
                    var detail = string.Join(" · ", new[] { quote.CustomerCity, quote.CustomerTaxNumber is null ? null : $"VKN/TCKN: {quote.CustomerTaxNumber}" }.Where(p => p is not null));
                    if (detail.Length > 0)
                    {
                        customer.Item().Text(detail).FontColor(Muted);
                    }

                    var contact = string.Join(" · ", new[] { quote.ContactName, quote.ContactEmail }.Where(p => p is not null));
                    if (contact.Length > 0)
                    {
                        customer.Item().Text($"İlgili: {contact}");
                    }
                });
            });

            column.Item().Column(title =>
            {
                title.Item().Text(quote.Title).FontSize(12).Bold();
                if (quote.Description is { Length: > 0 } description)
                {
                    title.Item().PaddingTop(2).Text(description);
                }
            });

            column.Item().Element(content => Lines(content, quote));
            column.Item().AlignRight().Element(content => Totals(content, quote));
            column.Item().Element(content => Terms(content, quote));
        });

    private static void Lines(IContainer container, QuoteDocumentSnapshot quote) =>
        container.Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.ConstantColumn(22);
                columns.RelativeColumn(5);
                columns.RelativeColumn(1.6f);
                columns.RelativeColumn(1.8f);
                columns.RelativeColumn(1.5f);
                columns.RelativeColumn(1f);
                columns.RelativeColumn(2f);
            });
            table.Header(header =>
            {
                foreach (var (text, right) in new[] { ("#", false), ("Kalem", false), ("Miktar", true), ("Birim fiyat", true), ("İndirim", true), ("KDV", true), ("Net tutar", true) })
                {
                    var cell = header.Cell().Background(Accent).PaddingVertical(4).PaddingHorizontal(3);
                    (right ? cell.AlignRight() : cell).Text(text).FontColor(Colors.White).Bold().FontSize(8);
                }
            });

            string? group = null;
            var index = 0;
            foreach (var line in quote.Lines)
            {
                if (line.GroupName is { Length: > 0 } name && name != group)
                {
                    group = name;
                    table.Cell().ColumnSpan(7).Background("#EEF2F6").PaddingVertical(3).PaddingHorizontal(3).Text(name).Bold();
                }

                index++;
                var muted = !line.Included;
                IContainer Cell(IContainer c) => c.BorderBottom(0.5f).BorderColor(Line).PaddingVertical(4).PaddingHorizontal(3);
                table.Cell().Element(Cell).Text(index.ToString(CultureInfo.InvariantCulture)).FontColor(Muted);
                table.Cell().Element(Cell).Column(item =>
                {
                    item.Item().Text(text =>
                    {
                        text.Span(line.Name).SemiBold();
                        if (line.Code is { Length: > 0 } code)
                        {
                            text.Span($"  ({code})").FontColor(Muted).FontSize(8);
                        }
                    });
                    if (RoleLabel(line) is { } label)
                    {
                        item.Item().Text(label).FontSize(7.5f).Italic().FontColor(Muted);
                    }

                    if (line.Description is { Length: > 0 } description)
                    {
                        item.Item().Text(description).FontSize(8).FontColor(Muted);
                    }

                    if (line.RecurringPeriod is { Length: > 0 } period)
                    {
                        item.Item().Text($"Tekrarlayan: {PeriodLabel(period)}").FontSize(7.5f).FontColor(Muted);
                    }
                });
                table.Cell().Element(Cell).AlignRight().Text(Quantity(line.Quantity, line.Unit));
                table.Cell().Element(Cell).AlignRight().Text(line.UnitPrice.ToString("N2", Numbers));
                table.Cell().Element(Cell).AlignRight().Text(DiscountText(line));
                table.Cell().Element(Cell).AlignRight().Text($"%{line.TaxRatePercent.ToString("0.##", Numbers)}");
                table.Cell().Element(Cell).AlignRight().Text(muted ? "—" : line.Net.ToString("N2", Numbers)).FontColor(muted ? Muted : "#1B2430");
            }
        });

    private static void Totals(IContainer container, QuoteDocumentSnapshot quote) =>
        container.Width(240).Column(column =>
        {
            void Row(string label, string value, bool strong = false)
            {
                column.Item().PaddingVertical(2).Row(row =>
                {
                    var l = row.RelativeItem().Text(label);
                    var v = row.ConstantItem(110).AlignRight().Text(value);
                    if (strong)
                    {
                        l.Bold();
                        v.Bold();
                    }
                });
            }

            Row("Liste toplamı", Money(quote.ListTotal, quote.Currency));
            if (quote.DiscountTotal > 0)
            {
                Row("Toplam indirim", "-" + Money(quote.DiscountTotal, quote.Currency));
            }

            Row(quote.PriceIncludesTax ? "Net toplam (KDV hariç)" : "Net toplam", Money(quote.NetTotal, quote.Currency));
            foreach (var group in quote.TaxGroups)
            {
                Row($"KDV %{group.RatePercent.ToString("0.##", Numbers)}", Money(group.Tax, quote.Currency));
            }

            column.Item().PaddingVertical(2).LineHorizontal(1).LineColor(Accent);
            Row("GENEL TOPLAM", Money(quote.GrossTotal, quote.Currency), strong: true);
            if (quote.OptionalNet > 0)
            {
                column.Item().PaddingTop(4).Text($"Opsiyonel/alternatif kalemler toplama dahil değildir (net {Money(quote.OptionalNet, quote.Currency)}).")
                    .FontSize(7.5f).FontColor(Muted);
            }
        });

    private static void Terms(IContainer container, QuoteDocumentSnapshot quote)
    {
        var terms = new[] { ("Ödeme koşulları", quote.PaymentTerms), ("Teslimat koşulları", quote.DeliveryTerms), ("Ticari koşullar", quote.CommercialTerms) }
            .Where(t => t.Item2 is { Length: > 0 }).ToList();
        if (terms.Count == 0)
        {
            return;
        }

        container.Column(column =>
        {
            column.Spacing(6);
            foreach (var (label, text) in terms)
            {
                column.Item().Column(item =>
                {
                    item.Item().Text(label).Bold().FontColor(Accent);
                    item.Item().Text(text!).FontSize(8.5f);
                });
            }
        });
    }

    private static void Footer(IContainer container, QuoteDocumentSnapshot quote) =>
        container.Column(column =>
        {
            column.Item().LineHorizontal(0.5f).LineColor(Line);
            column.Item().PaddingTop(3).Row(row =>
            {
                row.RelativeItem().Text(quote.Seller.FooterNote ?? string.Empty).FontSize(7.5f).FontColor(Muted);
                row.ConstantItem(140).AlignRight().Text(text =>
                {
                    text.DefaultTextStyle(style => style.FontSize(7.5f).FontColor(Muted));
                    text.Span($"{quote.QuoteNumber} V{quote.RevisionNo}  ·  Sayfa ");
                    text.CurrentPageNumber();
                    text.Span(" / ");
                    text.TotalPages();
                });
            });
        });

    private static string? RoleLabel(QuoteSnapshotLine line) => line.Role switch
    {
        "Optional" => line.IsSelected ? "Opsiyonel — seçili (toplama dahil)" : "Opsiyonel — toplama dahil değil",
        "Alternative" => line.IsSelected ? "Alternatif — seçili (toplama dahil)" : "Alternatif — toplama dahil değil",
        "PackageChild" => "Paket bileşeni",
        _ => null,
    };

    private static string DiscountText(QuoteSnapshotLine line) => line.DiscountKind switch
    {
        "Percent" when line.DiscountValue > 0 => $"%{line.DiscountValue.ToString("0.##", Numbers)}",
        "Amount" when line.DiscountValue > 0 => line.DiscountValue.ToString("N2", Numbers),
        _ => "—",
    };

    private static string PeriodLabel(string period) => period switch
    {
        "Monthly" => "aylık",
        "Quarterly" => "üç aylık",
        "Yearly" => "yıllık",
        _ => period,
    };
}
