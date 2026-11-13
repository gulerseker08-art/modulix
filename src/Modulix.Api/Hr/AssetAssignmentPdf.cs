using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Modulix.Api.Hr;

/// <summary>Zimmet tutanağının girdisi: yalnızca kayıt boru hattından okunmuş (çağıranın görebildiği) değerler.</summary>
public sealed record AssetAssignmentPdfModel(
    string AssetTag,
    string AssetName,
    string? Category,
    string? Brand,
    string? Model,
    string? SerialNumber,
    string EmployeeName,
    string? EmployeeNumber,
    string? JobTitle,
    string AssignedOn,
    string? ExpectedReturnOn,
    string? ConditionOut,
    string? AcknowledgedAt,
    string? ReturnedOn,
    string? ConditionIn,
    string Status,
    string GeneratedAt);

/// <summary>
/// Zimmet tutanağı (ADR-0042 dilim 4): demirbaş, teslim alan çalışan, teslim tarihi ve durumu, varsa iade bilgisi ve iki imza alanı. Türkçe
/// karakterler desteklenir. Tutanağın yasal biçimi doğrulanmadı (ADR-0042 bölüm 8); firma kendi metnini sonra şablonla ekleyebilir.
/// </summary>
public static class AssetAssignmentPdf
{
    private const string Accent = "#1F4E79";
    private const string Muted = "#5F6B7A";
    private const string Line = "#C9D1DA";

    static AssetAssignmentPdf()
    {
        // QuestPDF Community lisansı (docs/adr/0019-teklif-yonetimi.md).
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public static byte[] Render(AssetAssignmentPdfModel model) => Document.Create(container => container.Page(page =>
    {
        page.Size(PageSizes.A4);
        page.Margin(40);
        page.DefaultTextStyle(style => style.FontSize(10).FontColor("#1B2430").LineHeight(1.35f));
        page.Header().Column(column =>
        {
            column.Item().Text("ZİMMET TUTANAĞI").FontSize(16).Bold().FontColor(Accent);
            column.Item().Text($"Demirbaş {model.AssetTag} · Düzenlenme {model.GeneratedAt}").FontColor(Muted);
            column.Item().PaddingTop(6).LineHorizontal(0.5f).LineColor(Line);
        });
        page.Content().PaddingTop(12).Column(column =>
        {
            column.Spacing(10);
            Section(column, "Demirbaş",
            [
                ("Envanter numarası", model.AssetTag), ("Ad", model.AssetName), ("Kategori", model.Category), ("Marka / model", Join(model.Brand, model.Model)),
                ("Seri numarası", model.SerialNumber),
            ]);
            Section(column, "Teslim alan",
            [
                ("Ad soyad", model.EmployeeName), ("Sicil numarası", model.EmployeeNumber), ("Unvan", model.JobTitle),
            ]);
            Section(column, "Teslim",
            [
                ("Teslim tarihi", model.AssignedOn), ("Beklenen iade", model.ExpectedReturnOn), ("Teslimdeki durum", model.ConditionOut),
                ("Çalışanın teslim onayı", model.AcknowledgedAt ?? "Onaylanmadı"), ("Durum", model.Status),
            ]);
            if (model.ReturnedOn is not null)
            {
                Section(column, "İade", [("İade tarihi", model.ReturnedOn), ("İadedeki durum", model.ConditionIn)]);
            }

            column.Item().PaddingTop(8).Text("Yukarıda bilgileri yazılı demirbaşı eksiksiz ve çalışır durumda teslim aldım. Görevim sona erdiğinde ya da "
                + "istendiğinde aynı durumda iade edeceğimi kabul ederim.").FontColor(Muted);
            column.Item().PaddingTop(30).Row(row =>
            {
                Signature(row.RelativeItem(), "Teslim eden");
                row.ConstantItem(40);
                Signature(row.RelativeItem(), $"Teslim alan: {model.EmployeeName}");
            });
        });
        page.Footer().AlignRight().Text(text =>
        {
            text.DefaultTextStyle(style => style.FontSize(8).FontColor(Muted));
            text.Span("Sayfa ");
            text.CurrentPageNumber();
            text.Span(" / ");
            text.TotalPages();
        });
    })).GeneratePdf();

    private static void Section(ColumnDescriptor column, string title, IReadOnlyList<(string Label, string? Value)> rows)
    {
        column.Item().Text(title).Bold().FontColor(Accent);
        column.Item().Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.ConstantColumn(150);
                columns.RelativeColumn();
            });
            foreach (var (label, value) in rows)
            {
                table.Cell().BorderBottom(0.5f).BorderColor(Line).PaddingVertical(3).Text(label).FontColor(Muted);
                table.Cell().BorderBottom(0.5f).BorderColor(Line).PaddingVertical(3).Text(string.IsNullOrWhiteSpace(value) ? "—" : value);
            }
        });
    }

    private static void Signature(IContainer container, string label) =>
        container.Column(column =>
        {
            column.Item().Height(40);
            column.Item().LineHorizontal(0.5f).LineColor(Line);
            column.Item().Text(label).FontSize(9).FontColor(Muted);
            column.Item().Text("Tarih / imza").FontSize(8).FontColor(Muted);
        });

    private static string? Join(string? a, string? b) => string.Join(" ", new[] { a, b }.Where(s => !string.IsNullOrWhiteSpace(s))) is { Length: > 0 } joined ? joined : null;
}
