using System.Globalization;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Notifications;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Infrastructure.Email;
using Modulix.Infrastructure.Persistence;
using Modulix.Modules.Crm.Quotes;

namespace Modulix.Api.Crm;

/// <summary>Bir paylaşım bağlantısının e-posta gönderim durumu. <see cref="Status"/> <see cref="QuoteDeliveryStatus"/> değerlerinden biridir.</summary>
public sealed record QuoteLinkDelivery(string Status, string? Error, DateTimeOffset? ProviderAcceptedAt);

/// <summary>
/// Teklif e-postası gönderimi (ADR-0019, dilim 5). E-posta platform kuyruğuna yazılır (<see cref="IEmailQueue"/>); arka plan işleyicisi SMTP'ye
/// verir. Durum kuyruktan okunur ve dürüstçe adlandırılır: <c>Succeeded</c> yalnızca sağlayıcının (SMTP sunucusunun) kabul ettiğini söyler,
/// alıcıya ulaştığını söylemez. Gönderen/SMTP yapılandırılmamışsa e-posta kuyrukta <c>Pending</c> bekler; hiçbir şey sahte "gönderildi" olmaz.
/// Geliştirmede SMTP yerel Mailpit'tir, gerçek müşteriye e-posta gitmez.
/// </summary>
internal sealed class QuoteDelivery(IEmailQueue emails, PlatformDbContext platform, ITenantContextAccessor tenant)
{
    public const string ReferencePrefix = "crm.quote-link:";

    public static string Reference(Guid linkId) => $"{ReferencePrefix}{linkId:N}";

    /// <summary>Müşteri bağlantı e-postasını kuyruğa yazar. Başarısızlıkta istisna fırlatır (çağıran yakalar ve durumu <c>Failed</c> gösterir).</summary>
    public void Enqueue(
        Guid linkId, string to, string? recipientName, string sellerName, string quoteNumber, string quoteTitle, DateTimeOffset? validUntil, string url) =>
        emails.Enqueue(QuoteEmails.ShareLink(to, recipientName, sellerName, quoteNumber, quoteTitle, validUntil, url) with { Reference = Reference(linkId) });

    /// <summary>Bağlantıların e-posta durumu; e-postası olmayan bağlantı sonuçta yoktur (gönderilmedi).</summary>
    public async Task<IReadOnlyDictionary<Guid, QuoteLinkDelivery>> GetAsync(IEnumerable<Guid> linkIds, CancellationToken cancellationToken)
    {
        var tenantId = tenant.Current?.TenantId;
        var references = linkIds.ToDictionary(Reference, id => id);
        if (tenantId is null || references.Count == 0)
        {
            return new Dictionary<Guid, QuoteLinkDelivery>();
        }

        var keys = references.Keys.ToList();
        var rows = await platform.EmailMessages.AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.Reference != null && keys.Contains(e.Reference))
            .Select(e => new { e.Reference, e.Status, e.AttemptCount, e.LastError, e.SentAt })
            .ToListAsync(cancellationToken);
        return rows.ToDictionary(r => references[r.Reference!], r => Map(r.Status, r.AttemptCount, r.LastError, r.SentAt));
    }

    /// <summary>Kuyruk durumunu teklif gönderim durumuna eşler (saf).</summary>
    public static QuoteLinkDelivery Map(EmailStatus status, int attempts, string? error, DateTimeOffset? sentAt) => status switch
    {
        EmailStatus.Sent => new QuoteLinkDelivery(QuoteDeliveryStatus.Succeeded, null, sentAt),
        EmailStatus.Failed => new QuoteLinkDelivery(QuoteDeliveryStatus.Failed, Shorten(error), null),
        // Deneme başlamış ama bitmemişse (tekrar deneme bekliyor) işleniyor; hata kısa gösterilir.
        _ when attempts > 0 => new QuoteLinkDelivery(QuoteDeliveryStatus.Processing, Shorten(error), null),
        _ => new QuoteLinkDelivery(QuoteDeliveryStatus.Pending, null, null),
    };

    /// <summary>Teklifin gönderim durumu: e-postası olan en son bağlantının durumu; hiç e-posta yoksa <c>NotSent</c>.</summary>
    public static string Overall(IEnumerable<(DateTimeOffset CreatedAt, QuoteLinkDelivery? Delivery)> links) =>
        links.Where(l => l.Delivery is not null).OrderByDescending(l => l.CreatedAt).Select(l => l.Delivery!.Status).FirstOrDefault() ?? QuoteDeliveryStatus.NotSent;

    /// <summary>Gönderim durumunun Türkçe adı (tarihçe notu için).</summary>
    public static string Label(string status) => status switch
    {
        QuoteDeliveryStatus.Pending => "Gönderim bekliyor",
        QuoteDeliveryStatus.Processing => "İşleniyor",
        QuoteDeliveryStatus.Succeeded => "Sağlayıcı kabul etti",
        QuoteDeliveryStatus.Failed => "Gönderilemedi",
        _ => "Gönderilmedi",
    };

    private static string? Shorten(string? error) => string.IsNullOrWhiteSpace(error) ? null : error.Length > 200 ? error[..200] : error;
}

/// <summary>Müşteriye giden teklif e-postası. Kullanıcı girdisi (ad, başlık, firma adı) HTML'e kodlanarak eklenir; konu tek satırdır.</summary>
internal static class QuoteEmails
{
    public static EmailMessage ShareLink(string to, string? recipientName, string sellerName, string quoteNumber, string quoteTitle, DateTimeOffset? validUntil, string url)
    {
        var greeting = string.IsNullOrWhiteSpace(recipientName) ? "Merhaba," : $"Merhaba {recipientName.Trim()},";
        var title = OneLine(quoteTitle);
        var seller = OneLine(sellerName);
        var paragraphs = new List<string>
        {
            $"{seller} sizin için \"{title}\" başlıklı {quoteNumber} numaralı teklifi hazırladı.",
            "Teklifi incelemek, PDF olarak indirmek ve kabul ya da ret kararınızı iletmek için aşağıdaki bağlantıyı açın.",
        };
        if (validUntil is { } until)
        {
            paragraphs.Add($"Teklifin geçerlilik süresi {until.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)} tarihinde sona erer.");
        }

        var footer = "Bu bağlantı size özeldir; başkalarıyla paylaşmayın. Bu teklifi beklemiyorsanız e-postayı yok sayabilirsiniz.";
        var html = string.Concat(
            $"<p>{Encode(greeting)}</p>",
            string.Concat(paragraphs.Select(p => $"<p>{Encode(p)}</p>")),
            $"<p><a href=\"{Encode(url)}\">Teklifi görüntüle</a></p>",
            $"<p style=\"color:#666\">{Encode(footer)}</p>");
        var text = string.Join("\n\n", [greeting, .. paragraphs, $"Teklifi görüntüle: {url}", footer]);
        return new EmailMessage(to, $"Teklif {quoteNumber} — {seller}", html) { ToName = string.IsNullOrWhiteSpace(recipientName) ? null : recipientName.Trim(), TextBody = text };
    }

    private static string OneLine(string value) => string.Join(' ', value.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();

    private static string Encode(string value) => WebUtility.HtmlEncode(value);
}
