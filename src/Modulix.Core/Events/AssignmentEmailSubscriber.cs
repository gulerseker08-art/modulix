using System.Net;
using System.Text.Json.Nodes;
using Modulix.Core.Contracts.Events;
using Modulix.Core.Contracts.Modules;
using Modulix.Core.Contracts.Notifications;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Modules;

namespace Modulix.Core.Events;

/// <summary>
/// Atama e-postası (ADR-0007, <c>core.assignment-email</c>): kayıt başkasına atanınca (oluşturmada veya güncellemede
/// sorumlu değişince) yeni sorumluya "Size bir kayıt atandı" e-postası kuyruğa yazılır.
/// <list type="bullet">
///   <item>Kişi kendini atadıysa, kayıt devriyse (<see cref="EventReasons.Transfer"/>; devir tek özet gönderir) veya
///   sorumlu boşaltıldıysa e-posta yoktur.</item>
///   <item>Alıcı gönderim anında etkin üye değilse veya kaydı kendi yetkisiyle okuyamıyorsa e-posta yazılmaz; teslim
///   başarılı sayılır.</item>
///   <item>İçerik: kayıt türü, kaydın adı, atayan kişi, kayda bağlantı; hassas alan konmaz. Kullanıcı girdisi HTML'e
///   kodlanarak eklenir.</item>
///   <item>E-posta teslimle aynı işlemde yazılır: olay tekrar teslim edilse de ikinci e-posta oluşmaz.</item>
/// </list>
/// </summary>
public sealed class AssignmentEmailSubscriber(
    ModuleCatalog catalog,
    INotificationRecipients recipients,
    ITenantMembers members,
    IEmailQueue emails) : IEventSubscriber
{
    public async Task HandleAsync(EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (envelope.Reason == EventReasons.Transfer
            || envelope.Payload[OwnedRecordType.OwnerField] is not JsonObject change
            || change["new"] is not JsonValue value
            || !value.TryGetValue<string>(out var text)
            || !Guid.TryParse(text, out var ownerId)
            || ownerId == envelope.UserId)
        {
            return;
        }

        if (await recipients.FindAsync(ownerId, cancellationToken) is not { } recipient
            || await recipients.ReadRecordAsync(recipient, new RecordRef(envelope.EntityKey, envelope.RecordId), cancellationToken) is not { } record)
        {
            return;
        }

        var entity = catalog.FindAuditedEntity(envelope.EntityKey);
        var title = string.Join(' ', (entity?.TitleFields ?? [])
            .Select(f => record.Values[f] is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : null)
            .Where(s => !string.IsNullOrEmpty(s)));
        var path = catalog.Modules.SelectMany(m => m.OwnedRecords).FirstOrDefault(t => t.EntityKey == envelope.EntityKey)?.Path;
        var link = path is null ? null : recipients.Link(path.Replace("{id}", envelope.RecordId.ToString(), StringComparison.Ordinal));

        emails.Enqueue(Message(
            recipient,
            entity?.DisplayName ?? envelope.EntityKey,
            string.IsNullOrEmpty(title) ? "(adsız kayıt)" : title,
            await ActorNameAsync(envelope.UserId, cancellationToken),
            link));
    }

    private async Task<string> ActorNameAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (userId == Guid.Empty)
        {
            return "Modülix (otomatik işlem)";
        }

        return (await members.ListActiveAsync(cancellationToken)).FirstOrDefault(m => m.UserId == userId)?.DisplayName ?? "Bir kullanıcı";
    }

    private static EmailMessage Message(NotificationRecipient recipient, string entityName, string title, string assignedBy, string? link)
    {
        var html = string.Concat(
            $"<p>Merhaba {Encode(recipient.DisplayName)},</p>",
            $"<p>{Encode(assignedBy)}, size bir kayıt atadı: <strong>{Encode(title)}</strong> ({Encode(entityName)}).</p>",
            link is null ? string.Empty : $"<p><a href=\"{Encode(link)}\">Kaydı aç</a></p>",
            "<p style=\"color:#666\">Kaydın sorumlusu artık sizsiniz. Bu e-posta bilgi amaçlıdır.</p>");
        var text = string.Join(
            "\n\n",
            new[]
            {
                $"Merhaba {recipient.DisplayName},",
                $"{assignedBy}, size bir kayıt atadı: {title} ({entityName}).",
                link is null ? null : $"Kaydı aç: {link}",
                "Kaydın sorumlusu artık sizsiniz. Bu e-posta bilgi amaçlıdır.",
            }.OfType<string>());

        return new EmailMessage(recipient.Email, "Size bir kayıt atandı", html) { ToName = recipient.DisplayName, TextBody = text };
    }

    private static string Encode(string value) => WebUtility.HtmlEncode(value);
}
