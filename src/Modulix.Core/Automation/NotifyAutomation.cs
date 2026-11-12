using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Modulix.Core.Contracts.Automation;
using Modulix.Core.Contracts.Notifications;

namespace Modulix.Core.Automation;

/// <summary>
/// "Bildirim gönder" (ADR-0038 dilim 2b; ADR-0027 Core aksiyonu): seçilen üyenin uygulama içi bildirim kutusuna kısa bir ileti yazar. Sektörden
/// bağımsızdır; her kayıt türünün kuralında kullanılır (ör. "belgenin bitişine 30 gün kala filo sorumlusuna bildir"). Alıcı kaydı <b>kendi
/// yetkisiyle</b> göremiyorsa bildirim gönderilmez: başlıktaki kayıt değişkenleri ({{alan}}) görmediği bilgiyi sızdırmasın. Aynı kural adımı aynı
/// kayıt için aynı alıcıya bir kez yazar (bildirim kutusunun tekilleştirme anahtarı adımın idempotency anahtarıdır).
/// </summary>
public static class NotifyAutomation
{
    public const string NotifyAction = "core.notify";

    /// <summary>Alıcı seçimi: tenant'ın etkin üyeleri (Core üye lookup'ı).</summary>
    public const string RecipientLookup = "core.notification.recipient";

    public const string NotificationKind = "automation";

    public const int MaxTitleLength = 200;

    public static IReadOnlyList<AutomationActionRegistration> Actions { get; } =
    [
        AutomationActionRegistration.For<NotifyAction>(new AutomationActionDefinition(
            NotifyAction,
            "Bildirim gönder",
            "Seçilen kişinin bildirim kutusuna kısa bir ileti yazar. Kişi kaydı kendi yetkisiyle göremiyorsa bildirim gönderilmez. "
            + "Aynı kural aynı kayıt için aynı kişiye bir kez yazar.",
            [
                new AutomationParameter("recipientId", "Kime", AutomationFieldType.Lookup)
                {
                    LookupKey = RecipientLookup,
                    Description = "Firmanın etkin üyesi. Kaydın sorumlu alanına da bağlanabilir.",
                },
                new AutomationParameter("title", "İleti", AutomationFieldType.Text)
                {
                    MaxLength = MaxTitleLength,
                    Description = "Kayıt değişkeni için {{alan}} yazılabilir (ör. bitiş tarihi). Hassas bilgi yazmayın; ayrıntı kayıtta görülür.",
                },
            ],
            [],
            [],
            AutomationSideEffect.Write,
            SupportsDryRun: true)),
    ];
}

internal sealed class NotifyAction(INotificationRecipients recipients, INotificationInbox inbox) : IAutomationAction
{
    public async Task<AutomationActionResult> ExecuteAsync(AutomationActionRequest request, AutomationCallContext context, CancellationToken cancellationToken)
    {
        var title = Text(request.Parameters["title"]);
        if (title is null)
        {
            return new AutomationActionResult(AutomationActionStatus.Failed, "İleti boş.");
        }

        if (title.Length > NotifyAutomation.MaxTitleLength)
        {
            title = title[..NotifyAutomation.MaxTitleLength];
        }

        if (!Guid.TryParse(Text(request.Parameters["recipientId"]), out var userId)
            || await recipients.FindAsync(userId, cancellationToken) is not { } recipient)
        {
            return new AutomationActionResult(AutomationActionStatus.Failed, "Alıcı bu firmanın etkin üyesi değil; bildirim gönderilmedi.");
        }

        if (request.Target is { } target && await recipients.ReadRecordAsync(recipient, target, cancellationToken) is null)
        {
            return new AutomationActionResult(AutomationActionStatus.Skipped, $"{recipient.DisplayName} bu kaydı göremiyor; bildirim gönderilmedi.");
        }

        if (request.DryRun)
        {
            return new AutomationActionResult(AutomationActionStatus.Planned, $"{recipient.DisplayName} kişisine bildirim gidecek: \"{title}\"");
        }

        // Tekilleştirme anahtarı adımın idempotency anahtarının özetidir (sütun sınırı 150; anahtarın uzunluğuna bağlı kalmaz).
        var dedupe = "kural:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.IdempotencyKey)));
        await inbox.PostAsync(new InboxMessage(recipient.UserId, NotifyAutomation.NotificationKind, title, null, dedupe), cancellationToken);
        return new AutomationActionResult(AutomationActionStatus.Succeeded, $"{recipient.DisplayName} kişisine bildirim gönderildi.");
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;
}
