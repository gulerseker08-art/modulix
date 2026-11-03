using Modulix.Api.Access;
using Modulix.Api.Users;
using Modulix.Core.Communication;
using Modulix.Core.Contracts.Communication;
using Modulix.Core.Modules;
using Modulix.Shared.Communication;

namespace Modulix.Api.Communication;

/// <summary>
/// Ortak iletişim tercihi uç noktaları (/api/communication; ADR-0025). Okuma, izin/ret kaydı ve engel ayrı izinlerdir.
/// Tenant her istekte oturumdan çözülür. API'den kaydedilen izin yalnızca insan kaynaklıdır (manual, form, preference-center,
/// import); sistem ve sağlayıcı kaynakları iç servislere aittir. Reddedilmiş izin içe aktarmayla yeniden açılamaz.
/// </summary>
public static class CommunicationEndpoints
{
    private const string Prefix = "/api/communication";

    /// <summary>API'den kabul edilen izin kaynakları.</summary>
    private static readonly string[] ApiSources = [ConsentSources.Manual, ConsentSources.Form, ConsentSources.PreferenceCenter, ConsentSources.Import];

    public static IEndpointRouteBuilder MapCommunicationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(Prefix).RequireAuthorization().WithTags("İletişim tercihleri");

        group.MapGet("/preferences", (string? channel, string? purpose, string? status, string? address, int? page, int? pageSize,
            ICommunicationAdministration service, CancellationToken ct) => RunAsync(async () =>
        {
            var query = new PreferenceQuery(
                Optional<CommunicationChannel>(channel, nameof(channel)),
                Optional<CommunicationPurpose>(purpose, nameof(purpose)),
                Optional<ConsentStatus>(status, nameof(status)),
                address,
                page ?? 1,
                pageSize ?? 25);
            var result = await service.ListPreferencesAsync(query, ct);
            return Results.Ok(new PreferencePageDto([.. result.Items.Select(ToDto)], result.Page, result.PageSize, result.TotalCount));
        })).RequirePermission(CoreModule.Permissions.ReadCommunication);

        group.MapGet("/preferences/{id:guid}/events", async (Guid id, ICommunicationAdministration service, CancellationToken ct) =>
            await service.EventsAsync(id, ct) is { } events
                ? Results.Ok(events.Select(e => new ConsentEventDto(
                    e.Id, e.FromStatus.ToString(), e.ToStatus.ToString(), e.Source, e.EvidenceRef, e.Reason, e.ChangedBy, e.ChangedAt)).ToList())
                : NotFound("İletişim tercihi bulunamadı.")).RequirePermission(CoreModule.Permissions.ReadCommunication);

        group.MapPost("/check", (CheckCommunicationRequest request, ICommunicationPreferences preferences, CancellationToken ct) => RunAsync(async () =>
        {
            var decision = await preferences.EvaluateAsync(
                Required<CommunicationChannel>(request.Channel, nameof(request.Channel)),
                Required<CommunicationPurpose>(request.Purpose, nameof(request.Purpose)),
                request.Address ?? string.Empty,
                ct);
            return Results.Ok(new CommunicationDecisionDto(decision.Outcome.ToString(), decision.Code, decision.Message));
        })).RequirePermission(CoreModule.Permissions.ReadCommunication);

        group.MapPut("/preferences", (SaveConsentRequest request, ICommunicationAdministration service, CancellationToken ct) => RunAsync(async () =>
        {
            var source = string.IsNullOrWhiteSpace(request.Source) ? ConsentSources.Manual : request.Source.Trim().ToLowerInvariant();
            if (!ApiSources.Contains(source))
            {
                throw new ArgumentException($"Kaynak '{string.Join("', '", ApiSources)}' değerlerinden biri olmalıdır.", nameof(request.Source));
            }

            var view = await service.RecordConsentAsync(
                new ConsentChange(
                    Required<CommunicationChannel>(request.Channel, nameof(request.Channel)),
                    request.Address ?? string.Empty,
                    Required<CommunicationPurpose>(request.Purpose, nameof(request.Purpose)),
                    Required<ConsentStatus>(request.Status, nameof(request.Status)),
                    source,
                    request.EvidenceRef,
                    request.Reason,
                    request.PartyId,
                    request.PersonId),
                ct);
            return Results.Ok(ToDto(view));
        })).RequirePermission(CoreModule.Permissions.ManageCommunication);

        group.MapGet("/suppressions", (string? channel, string? address, bool? active, int? page, int? pageSize,
            ICommunicationAdministration service, CancellationToken ct) => RunAsync(async () =>
        {
            var result = await service.ListSuppressionsAsync(
                new SuppressionQuery(Optional<CommunicationChannel>(channel, nameof(channel)), address, active, page ?? 1, pageSize ?? 25), ct);
            return Results.Ok(new SuppressionPageDto([.. result.Items.Select(ToDto)], result.Page, result.PageSize, result.TotalCount));
        })).RequirePermission(CoreModule.Permissions.ReadCommunication);

        group.MapPost("/suppressions", (SaveSuppressionRequest request, ICommunicationAdministration service, CancellationToken ct) => RunAsync(async () =>
        {
            var view = await service.SuppressAsync(
                new SuppressionRequest(
                    Required<CommunicationChannel>(request.Channel, nameof(request.Channel)),
                    request.Address ?? string.Empty,
                    Required<SuppressionReason>(request.Reason, nameof(request.Reason)),
                    ConsentSources.Manual,
                    request.Note,
                    request.EndsAt),
                ct);
            return Results.Ok(ToDto(view));
        })).RequirePermission(CoreModule.Permissions.SuppressCommunication);

        group.MapPost("/suppressions/{id:guid}/lift", (Guid id, LiftSuppressionRequest request, ICommunicationAdministration service, CancellationToken ct) => RunAsync(async () =>
            await service.LiftAsync(id, request.Reason, ct) is { } view ? Results.Ok(ToDto(view)) : NotFound("Engel bulunamadı.")))
            .RequirePermission(CoreModule.Permissions.SuppressCommunication);

        // Konular (ADR-0025, dilim 14): tanım ve konu tercihi okuma/yönetim izinleriyle aynı ayrımı izler (konu izin vermez, yalnızca daraltır).
        group.MapGet("/topics", (bool? activeOnly, ICommunicationAdministration service, CancellationToken ct) => RunAsync(async () =>
            Results.Ok((await service.ListTopicsAsync(activeOnly ?? false, ct)).Select(ToDto).ToList())))
            .RequirePermission(CoreModule.Permissions.ReadCommunication);

        group.MapPut("/topics", (SaveTopicRequest request, ICommunicationAdministration service, CancellationToken ct) => RunAsync(async () =>
            Results.Ok(ToDto(await service.SaveTopicAsync(request.Key ?? string.Empty, request.Name ?? string.Empty, request.IsActive, ct)))))
            .RequirePermission(CoreModule.Permissions.ManageCommunication);

        group.MapGet("/topic-preferences", (string? channel, string? address, string? topic, bool? optedOut, int? page, int? pageSize,
            ICommunicationAdministration service, CancellationToken ct) => RunAsync(async () =>
        {
            var result = await service.ListTopicPreferencesAsync(
                new TopicPreferenceQuery(Optional<CommunicationChannel>(channel, nameof(channel)), address, topic, optedOut, page ?? 1, pageSize ?? 25), ct);
            return Results.Ok(new TopicPreferencePageDto([.. result.Items.Select(ToDto)], result.Page, result.PageSize, result.TotalCount));
        })).RequirePermission(CoreModule.Permissions.ReadCommunication);

        group.MapPut("/topic-preferences", (SaveTopicPreferenceRequest request, ICommunicationAdministration service, CancellationToken ct) => RunAsync(async () =>
        {
            var source = string.IsNullOrWhiteSpace(request.Source) ? ConsentSources.Manual : request.Source.Trim().ToLowerInvariant();
            if (!ApiSources.Contains(source))
            {
                throw new ArgumentException($"Kaynak '{string.Join("', '", ApiSources)}' değerlerinden biri olmalıdır.", nameof(request.Source));
            }

            var view = await service.RecordTopicPreferenceAsync(
                new TopicPreferenceChange(
                    Required<CommunicationChannel>(request.Channel, nameof(request.Channel)), request.Address ?? string.Empty, request.TopicKey ?? string.Empty, request.OptedOut, source),
                ct);
            return Results.Ok(ToDto(view));
        })).RequirePermission(CoreModule.Permissions.ManageCommunication);

        return app;
    }

    private static async Task<IResult> RunAsync(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception ex) when (UserEndpoints.ToProblem(ex) is { } problem)
        {
            return problem;
        }
    }

    /// <summary>Sayı gibi görünen değerler (ör. "1") kabul edilmez; yalnızca ad.</summary>
    private static T Required<T>(string? value, string parameter)
        where T : struct, Enum =>
        Optional<T>(value, parameter) ?? throw new ArgumentException("Bu alan zorunludur.", parameter);

    private static T? Optional<T>(string? value, string parameter)
        where T : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return Enum.TryParse<T>(value.Trim(), ignoreCase: true, out var parsed) && Enum.IsDefined(parsed) && !char.IsDigit(value.Trim()[0])
            ? parsed
            : throw new ArgumentException($"Geçersiz değer: '{value}'.", parameter);
    }

    private static IResult NotFound(string title) => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: title);

    private static PreferenceDto ToDto(PreferenceView p) => new(
        p.Id, p.Channel.ToString(), p.Address, p.Purpose.ToString(), p.Status.ToString(), p.Source, p.EvidenceRef, p.PartyId, p.PersonId, p.ChangedBy, p.ChangedAt);

    private static TopicDto ToDto(TopicView t) => new(t.Id, t.Key, t.Name, t.IsActive, t.CreatedAt, t.ChangedAt);

    private static TopicPreferenceDto ToDto(TopicPreferenceView p) =>
        new(p.Id, p.Channel.ToString(), p.Address, p.TopicKey, p.OptedOut, p.Source, p.ChangedBy, p.ChangedAt);

    private static SuppressionDto ToDto(SuppressionView s) => new(
        s.Id, s.Channel.ToString(), s.Address, s.Reason.ToString(), s.Source, s.Note, s.StartsAt, s.EndsAt, s.IsActive, s.CreatedBy, s.CreatedAt,
        s.LiftedAt, s.LiftedBy, s.LiftReason);
}
