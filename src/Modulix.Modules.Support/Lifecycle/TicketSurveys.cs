using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Notifications;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Modules.Support.Domain;
using Modulix.Modules.Support.Records;

namespace Modulix.Modules.Support.Lifecycle;

internal static class SurveyTokens
{
    private const int Bytes = 32;

    public static string New() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(Bytes)).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>Biçim denetimi: veritabanına gitmeden bariz geçersiz belirteçleri eler.</summary>
    public static bool LooksValid(string? token) =>
        token is { Length: >= 40 and <= 64 } && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}

/// <summary>
/// Çözülen talep için anketi açar ve e-postasını kuyruğa yazar (çözüm işlemiyle aynı işlemde). Ayar kapalıysa, alıcı e-postası yoksa ya da bu çözüm döngüsü için anket varsa hiçbir şey yapmaz.
/// Anket bağlantısı <c>App:PublicUrl</c> ayarlıysa e-postaya girer; ayarlı değilse anket oluşur ama e-posta yazılmaz (<see cref="SatisfactionSurvey.QueuedAt"/> boş kalır, bu dürüstçe görünür).
/// Belirteç yalnızca burada, bir kez üretilir.
/// </summary>
internal sealed class SurveyIssuer(SupportReader reader, IEmailQueue emails, INotificationRecipients links, ITenantContextAccessor tenant)
{
    public async Task IssueAsync(SupportStoreContext context, SupportTicket ticket, TicketPolicySettings settings, CancellationToken cancellationToken)
    {
        if (!settings.SurveyEnabled || tenant.Current is not { } current)
        {
            return;
        }

        var (address, name) = await reader.RecipientAsync(ticket, cancellationToken);
        if (address is null)
        {
            return;
        }

        var cycle = ticket.ReopenCount + 1;
        if (await context.Data.Set<SatisfactionSurvey>().AnyAsync(s => s.TicketId == ticket.Id && s.Cycle == cycle, cancellationToken))
        {
            return;
        }

        var token = SurveyTokens.New();
        var survey = new SatisfactionSurvey
        {
            TicketId = ticket.Id,
            Cycle = cycle,
            TokenHash = SurveyTokens.Hash(token),
            RecipientEmail = address,
            ExpiresAt = context.Time.GetUtcNow().AddDays(Math.Clamp(settings.SurveyValidDays, 1, 90)),
        };

        if (links.Link($"anket/{current.TenantId:N}#t={token}") is { } url)
        {
            try
            {
                var greeting = string.IsNullOrWhiteSpace(name) ? "Merhaba," : $"Merhaba {name.Trim()},";
                var text = $"{greeting}\n\n{ticket.Number} numaralı talebiniz çözüldü. Deneyiminizi 1-5 arası puanlamanız bizim için değerli.\n\nAnket: {url}\n\nBu bağlantı size özeldir ve {survey.ExpiresAt:dd.MM.yyyy} tarihine kadar geçerlidir.";
                var html = $"<p>{WebUtility.HtmlEncode(greeting)}</p><p>{WebUtility.HtmlEncode(ticket.Number)} numaralı talebiniz çözüldü. Deneyiminizi 1-5 arası puanlamanız bizim için değerli.</p>"
                    + $"<p><a href=\"{WebUtility.HtmlEncode(url)}\">Anketi yanıtla</a></p>"
                    + $"<p style=\"color:#666\">Bu bağlantı size özeldir ve {survey.ExpiresAt:dd.MM.yyyy} tarihine kadar geçerlidir.</p>";
                emails.Enqueue(new EmailMessage(address, $"[{ticket.Number}] Deneyiminizi puanlayın", html)
                {
                    ToName = string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
                    TextBody = text,
                    Reference = $"support.survey:{survey.Id}",
                });
                survey.QueuedAt = context.Time.GetUtcNow();
            }
            catch (ArgumentException)
            {
                // Geçersiz adres: anket oluşur, e-posta yazılmaz (QueuedAt boş).
            }
        }

        context.Data.Set<SatisfactionSurvey>().Add(survey);
    }
}

public sealed record SurveyItem(Guid Id, int Cycle, string State, DateTimeOffset? QueuedAt, DateTimeOffset ExpiresAt, DateTimeOffset? AnsweredAt, int? Rating, string? Comment, DateTimeOffset CreatedAt);

/// <summary>Talebin anketleri (personel görünümü). Talep okuma izni ve veri kapsamı geçerlidir; belirteç ve alıcı adresi dönmez.</summary>
public interface ITicketSurveys
{
    Task<RecordResult<IReadOnlyList<SurveyItem>>> ListAsync(Guid ticketId, CancellationToken cancellationToken);
}

internal sealed class TicketSurveys(IRecordPipeline pipeline, SupportStoreContext context) : ITicketSurveys
{
    public async Task<RecordResult<IReadOnlyList<SurveyItem>>> ListAsync(Guid ticketId, CancellationToken cancellationToken)
    {
        var ticket = await pipeline.GetAsync(new RecordRef(SupportModule.Entities.Ticket, ticketId), cancellationToken);
        if (!ticket.IsSuccess)
        {
            return RecordResult<IReadOnlyList<SurveyItem>>.Fail(ticket.Error!.Code, ticket.Error.Message, ticket.Error.FieldErrors);
        }

        var now = context.Time.GetUtcNow();
        var rows = await context.Data.Set<SatisfactionSurvey>().AsNoTracking().Where(s => s.TicketId == ticketId).OrderBy(s => s.Cycle).ToListAsync(cancellationToken);
        return RecordResult<IReadOnlyList<SurveyItem>>.Ok([.. rows.Select(s => new SurveyItem(
            s.Id, s.Cycle, s.AnsweredAt is not null ? "Answered" : s.ExpiresAt <= now ? "Expired" : s.QueuedAt is null ? "NotSent" : "Sent",
            s.QueuedAt, s.ExpiresAt, s.AnsweredAt, s.Rating, s.Comment, s.CreatedAt))]);
    }
}

public enum SurveyPortalError
{
    NotFound,
    Invalid,
    Conflict,
}

public sealed record SurveyPortalView(string State, bool CanAnswer, string? Message, string TicketNumber, string Subject, DateTimeOffset ExpiresAt, int? Rating);

public sealed record SurveyPortalResult<T>(T? Value, SurveyPortalError? Error, string? Message, IReadOnlyDictionary<string, string>? Fields)
{
    public bool IsSuccess => Error is null;

    public static SurveyPortalResult<T> Ok(T value) => new(value, null, null, null);

    public static SurveyPortalResult<T> Fail(SurveyPortalError error, string message, IReadOnlyDictionary<string, string>? fields = null) => new(default, error, message, fields);
}

/// <summary>
/// Herkese açık anket sayfasının arkasındaki servis (oturumsuz; kimlik doğrulama bağlantı belirtecine sahip olmaktır). Belirteç veritabanında yalnızca özet olarak durur; geçersiz,
/// süresi dolmuş ya da yanıtlanmış bağlantı ayırt edilir ama talep içeriği (konu ve numara dışında) sızmaz. Yanıt tek kullanımlıdır: eşzamanlı ikinci yanıt sürüm denetimiyle reddedilir.
/// Yanıt talebin tarihçesine yazılır; düşük puan ayrı tarihçe olayıdır (takip için). Sistem kimliği "support.survey" yalnızca talebi okur ve tarihçe yazar.
/// </summary>
public interface ISurveyPortal
{
    Task<SurveyPortalResult<SurveyPortalView>> ViewAsync(string token, CancellationToken cancellationToken);

    Task<SurveyPortalResult<SurveyPortalView>> AnswerAsync(string token, int rating, string? comment, CancellationToken cancellationToken);
}

internal sealed class SurveyPortal(IRecordPipeline pipeline, TicketWorkflowGate gate, SupportStoreContext context) : ISurveyPortal
{
    private const string Unknown = "Bağlantı geçersiz.";

    public async Task<SurveyPortalResult<SurveyPortalView>> ViewAsync(string token, CancellationToken cancellationToken) =>
        await ResolveAsync(token, cancellationToken) is { } found
            ? SurveyPortalResult<SurveyPortalView>.Ok(ViewOf(found.Survey, found.Number, found.Subject))
            : SurveyPortalResult<SurveyPortalView>.Fail(SurveyPortalError.NotFound, Unknown);

    public async Task<SurveyPortalResult<SurveyPortalView>> AnswerAsync(string token, int rating, string? comment, CancellationToken cancellationToken)
    {
        if (await ResolveAsync(token, cancellationToken) is not { } found)
        {
            return SurveyPortalResult<SurveyPortalView>.Fail(SurveyPortalError.NotFound, Unknown);
        }

        var survey = found.Survey;
        var (open, reason) = SurveyRules.CanAnswer(survey.AnsweredAt, survey.ExpiresAt, context.Time.GetUtcNow());
        if (!open)
        {
            return SurveyPortalResult<SurveyPortalView>.Fail(SurveyPortalError.Conflict, reason!);
        }

        if (!SurveyRules.IsValidRating(rating))
        {
            return SurveyPortalResult<SurveyPortalView>.Fail(SurveyPortalError.Invalid, "Puan 1-5 arasında olmalıdır.", new Dictionary<string, string> { ["rating"] = "Puan 1-5 arasında olmalıdır." });
        }

        var text = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
        if (text is { } c && c.Length > SatisfactionSurvey.CommentMaxLength)
        {
            return SurveyPortalResult<SurveyPortalView>.Fail(SurveyPortalError.Invalid, "Yorum çok uzun.", new Dictionary<string, string> { ["comment"] = $"En fazla {SatisfactionSurvey.CommentMaxLength} karakter olabilir." });
        }

        var kind = SurveyRules.IsLow(rating) ? TicketEventKinds.LowSatisfaction : TicketEventKinds.SurveyAnswered;
        var now = context.Time.GetUtcNow();
        var values = new JsonObject { ["lastActivityAt"] = now.ToString("O", CultureInfo.InvariantCulture) };
        try
        {
            using (gate.Open([SupportModule.Permissions.TicketsWrite], kind, $"Puan: {rating}/5",
                async (ctx, ticket, ct) =>
                {
                    var tracked = await ctx.Data.Set<SatisfactionSurvey>().SingleAsync(s => s.Id == survey.Id, ct);
                    if (tracked.AnsweredAt is not null)
                    {
                        throw new DbUpdateConcurrencyException("Anket zaten yanıtlandı.");
                    }

                    tracked.AnsweredAt = now;
                    tracked.Rating = rating;
                    tracked.Comment = text;
                    tracked.Touch();
                }))
            {
                var updated = await pipeline.UpdateAsync(new RecordCommand(SupportModule.Entities.Ticket, survey.TicketId, values, null) { Reason = $"support.{kind}" }, cancellationToken);
                if (!updated.IsSuccess)
                {
                    return SurveyPortalResult<SurveyPortalView>.Fail(SurveyPortalError.Conflict, "Anket şu an yanıtlanamıyor.");
                }
            }
        }
        catch (DbUpdateException)
        {
            return SurveyPortalResult<SurveyPortalView>.Fail(SurveyPortalError.Conflict, "Bu anket daha önce yanıtlandı.");
        }

        var refreshed = await ResolveAsync(token, cancellationToken);
        return refreshed is null
            ? SurveyPortalResult<SurveyPortalView>.Fail(SurveyPortalError.NotFound, Unknown)
            : SurveyPortalResult<SurveyPortalView>.Ok(ViewOf(refreshed.Survey, refreshed.Number, refreshed.Subject));
    }

    private sealed record Found(SatisfactionSurvey Survey, string Number, string Subject);

    private async Task<Found?> ResolveAsync(string token, CancellationToken cancellationToken)
    {
        if (!SurveyTokens.LooksValid(token))
        {
            return null;
        }

        var hash = SurveyTokens.Hash(token);
        var survey = await context.Data.Set<SatisfactionSurvey>().AsNoTracking().FirstOrDefaultAsync(s => s.TokenHash == hash, cancellationToken);
        if (survey is null)
        {
            return null;
        }

        var ticket = await context.Data.Set<SupportTicket>().AsNoTracking().Where(t => t.Id == survey.TicketId).Select(t => new { t.Number, t.Subject }).FirstOrDefaultAsync(cancellationToken);
        return ticket is null ? null : new Found(survey, ticket.Number, ticket.Subject);
    }

    private SurveyPortalView ViewOf(SatisfactionSurvey survey, string number, string subject)
    {
        var (open, reason) = SurveyRules.CanAnswer(survey.AnsweredAt, survey.ExpiresAt, context.Time.GetUtcNow());
        var state = survey.AnsweredAt is not null ? "answered" : open ? "open" : "expired";
        return new SurveyPortalView(state, open, open ? null : reason, number, subject, survey.ExpiresAt, survey.Rating);
    }
}

