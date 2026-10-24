using Modulix.Api.Access;
using Modulix.Core.Authorization;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Modules;
using Modulix.Core.Platform;
using Modulix.Shared.Users;

namespace Modulix.Api.Users;

/// <summary>
/// Kullanıcı yönetimi (Y2): /api/users. Hepsi aktif tenant ve <c>core.users.manage</c> ister. Kimlik üyelik
/// kimliğidir; başka tenant'ın üyeliği 404 döner. Kurallar <see cref="IUserAdministration"/>'dadır.
/// </summary>
public static class UserEndpoints
{
    public static IEndpointRouteBuilder MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        var users = app.MapGroup("/api/users")
            .RequireAuthorization()
            .RequirePermission(CoreModule.Permissions.ManageUsers)
            .WithTags("Users");

        users.MapGet("/", ListAsync);
        users.MapGet("/{membershipId:guid}", GetAsync);
        users.MapPost("/invitations", InviteAsync);
        users.MapPost("/{membershipId:guid}/invitation/resend", (Guid membershipId, HttpContext http, IUserAdministration admin, CancellationToken ct) =>
            Run(() => admin.ResendInvitationAsync(membershipId, PublicUrl.Resolve(http), ct)));
        users.MapDelete("/{membershipId:guid}/invitation", (Guid membershipId, IUserAdministration admin, CancellationToken ct) =>
            Run(() => admin.CancelInvitationAsync(membershipId, ct)));
        users.MapPost("/{membershipId:guid}/disable", DisableAsync);
        users.MapGet("/{membershipId:guid}/owned-records", GetOwnedRecordsAsync);
        users.MapGet("/{membershipId:guid}/access", GetAccessAsync);
        users.MapPost("/{membershipId:guid}/transfer-records", TransferRecordsAsync);
        users.MapPost("/{membershipId:guid}/enable", (Guid membershipId, IUserAdministration admin, CancellationToken ct) =>
            Run(() => admin.EnableAsync(membershipId, ct)));
        users.MapPost("/{membershipId:guid}/password-reset", (Guid membershipId, HttpContext http, IUserAdministration admin, CancellationToken ct) =>
            Run(() => admin.SendPasswordResetAsync(membershipId, PublicUrl.Resolve(http), ct)));
        users.MapPost("/{membershipId:guid}/close-sessions", (Guid membershipId, IUserAdministration admin, CancellationToken ct) =>
            Run(() => admin.CloseSessionsAsync(membershipId, ct)));
        users.MapPost("/{membershipId:guid}/unlock", (Guid membershipId, IUserAdministration admin, CancellationToken ct) =>
            Run(() => admin.UnlockAsync(membershipId, ct)));

        return app;
    }

    private static async Task<IResult> ListAsync(
        string? search, UserStatus? status, Guid? roleId, int? page, int? pageSize, IUserAdministration admin, CancellationToken ct)
    {
        var result = await admin.ListAsync(
            new UserQuery(search, (MembershipStatus?)status, roleId, page ?? 1, pageSize ?? 25), ct);
        return Results.Ok(new UserPageResponse(result.Items.Select(ToDto).ToList(), result.Page, result.PageSize, result.TotalCount));
    }

    private static async Task<IResult> GetAsync(Guid membershipId, IUserAdministration admin, CancellationToken ct) =>
        await admin.GetAsync(membershipId, ct) is { } user ? Results.Ok(ToDto(user)) : NotFound();

    /// <summary>
    /// Gövde isteğe bağlıdır (kayıtları devralacak üye) ve elle okunur: gövde parametresi içerik türü olmayan isteği
    /// uç noktaya hiç ulaştırmaz (Accepts meta verisi de öyle).
    /// </summary>
    private static async Task<IResult> DisableAsync(Guid membershipId, HttpRequest http, IUserAdministration admin, CancellationToken ct)
    {
        DisableUserRequest? request = null;
        if (http.HasJsonContentType())
        {
            try
            {
                request = await http.ReadFromJsonAsync<DisableUserRequest>(ct);
            }
            catch (System.Text.Json.JsonException)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "İstek gövdesi okunamadı.");
            }
        }

        return await Run(() => admin.DisableAsync(membershipId, request?.TransferToMembershipId, ct));
    }

    private static async Task<IResult> GetOwnedRecordsAsync(Guid membershipId, IUserAdministration admin, CancellationToken ct)
    {
        try
        {
            var owned = await admin.GetOwnedRecordsAsync(membershipId, ct);
            return Results.Ok(owned.Select(o => new OwnedRecordsDto(o.EntityKey, o.Label, o.Count)).ToList());
        }
        catch (Exception ex) when (ToProblem(ex) is { } problem)
        {
            return problem;
        }
    }

    /// <summary>"Bu kullanıcı neye erişebilir": geçerli izinler, veren rollerle; matris için açık modüllerin izinleri.</summary>
    private static async Task<IResult> GetAccessAsync(
        Guid membershipId, IUserAdministration admin, ModuleCatalog catalog, ICurrentAccess access, CancellationToken ct)
    {
        try
        {
            var result = await admin.GetAccessAsync(membershipId, ct);
            return Results.Ok(new UserAccessDto(
                result.IsAdministrator,
                await AccessEndpoints.AssignablePermissionsAsync(catalog, access, ct),
                result.Permissions.Select(p => new PermissionGrantDto(p.Key, p.GrantedBy.Select(r => new UserRoleDto(r.Id, r.Name, r.IsSystem)).ToList())).ToList()));
        }
        catch (Exception ex) when (ToProblem(ex) is { } problem)
        {
            return problem;
        }
    }

    private static async Task<IResult> TransferRecordsAsync(
        Guid membershipId, TransferRecordsRequest request, IUserAdministration admin, CancellationToken ct)
    {
        try
        {
            return Results.Ok(new TransferRecordsResponse(await admin.TransferRecordsAsync(membershipId, request.ToMembershipId, ct)));
        }
        catch (Exception ex) when (ToProblem(ex) is { } problem)
        {
            return problem;
        }
    }

    private static async Task<IResult> InviteAsync(InviteUserRequest request, HttpContext http, IUserAdministration admin, CancellationToken ct)
    {
        try
        {
            var result = await admin.InviteAsync(
                new UserInvitation(request.Email, request.DisplayName, request.RoleIds ?? [], PublicUrl.Resolve(http)), ct);
            return Results.Created($"/api/users/{result.User.MembershipId}", new InviteUserResponse(ToDto(result.User), result.ExistingAccount));
        }
        catch (Exception ex) when (ToProblem(ex) is { } problem)
        {
            return problem;
        }
    }

    /// <summary>Durum değiştiren işlemler: başarıda 204, kural ihlalinde sorun ayrıntısı.</summary>
    internal static async Task<IResult> Run(Func<Task> action)
    {
        try
        {
            await action();
            return Results.NoContent();
        }
        catch (Exception ex) when (ToProblem(ex) is { } problem)
        {
            return problem;
        }
    }

    /// <summary>Kullanıcı yönetimi hatalarının HTTP karşılığı; mesaj başlıktadır (arayüz başlığı gösterir).</summary>
    internal static IResult? ToProblem(Exception exception) => exception switch
    {
        UserAdministrationException { Error: UserAdministrationError.NotFound } => NotFound(),
        UserAdministrationException { Error: UserAdministrationError.AdministratorRequired } ex =>
            Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: ex.Message, detail: ex.Message),
        UserAdministrationException { Error: UserAdministrationError.PermissionDenied } ex =>
            Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: ex.Message, detail: ex.Message),
        UserAdministrationException { Error: UserAdministrationError.TooManyRequests } ex =>
            Results.Problem(statusCode: StatusCodes.Status429TooManyRequests, title: ex.Message),
        UserAdministrationException ex => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: ex.Message),
        RoleConflictException ex => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: ex.Message),
        ArgumentException ex => Results.ValidationProblem(
            new Dictionary<string, string[]> { [ex.ParamName ?? string.Empty] = [FieldMessage(ex)] },
            title: "Lütfen işaretli alanları düzeltin.",
            statusCode: StatusCodes.Status400BadRequest),
        _ => null,
    };

    internal static UserDto ToDto(TenantUser user) => new(
        user.MembershipId,
        user.UserId,
        user.DisplayName,
        user.Email,
        (UserStatus)user.Status,
        user.Roles.Select(r => new UserRoleDto(r.Id, r.Name, r.IsSystem)).ToList(),
        user.CreatedAt,
        user.InvitedAt,
        user.LastSignInAt,
        user.IsLockedOut,
        user.IsCurrentUser);

    // ArgumentException.Message sonuna " (Parameter 'x')" ekler; kullanıcıya yalnızca asıl mesaj gösterilir.
    private static string FieldMessage(ArgumentException ex) =>
        ex.ParamName is { } name ? ex.Message.Replace($" (Parameter '{name}')", string.Empty) : ex.Message;

    // Başka tenant'ın üyeliği de "bulunamadı" döner; varlığı belli edilmez.
    private static IResult NotFound() =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Kullanıcı bulunamadı.");
}

/// <summary>
/// E-postadaki bağlantıların kökü: <c>App:PublicUrl</c> (ör. https://app.modulix.com.tr). Geliştirmede boşsa isteğin
/// adresi kullanılır. Üretimde zorunludur: isteğin Host başlığına güvenilirse saldırgan, kendi adresine giden şifre
/// sıfırlama bağlantıları ürettirebilir.
/// </summary>
public static class PublicUrl
{
    public const string ConfigurationKey = "App:PublicUrl";

    public static string Resolve(HttpContext http)
    {
        var configured = http.RequestServices.GetRequiredService<IConfiguration>()[ConfigurationKey];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.TrimEnd('/');
        }

        if (http.RequestServices.GetRequiredService<IHostEnvironment>().IsDevelopment())
        {
            return $"{http.Request.Scheme}://{http.Request.Host}";
        }

        throw new InvalidOperationException($"{ConfigurationKey} ayarı gerekli: e-postadaki bağlantılar bu adresle oluşturulur.");
    }
}
