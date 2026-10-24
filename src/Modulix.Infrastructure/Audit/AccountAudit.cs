using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Modules;
using Modulix.Core.Platform;
using Modulix.Core.Records;
using Modulix.Infrastructure.Identity;
using Modulix.Infrastructure.Persistence;
using Modulix.Infrastructure.Platform;

namespace Modulix.Infrastructure.Audit;

/// <summary>İsteğin geldiği istemci: IP adresi ve tarayıcı bilgisi (kişisel veri; aydınlatma metninde yer alır).</summary>
public sealed record AccountClient(string? IpAddress, string? UserAgent)
{
    public const int UserAgentMaxLength = 300;
}

/// <summary>
/// Oturum olaylarını firmaların denetim kaydına yazar (Y5b, kayıt türü <c>core.account</c>). Giriş platform düzeyindedir;
/// olay, onu görmesi gereken firmaların her birine kendi bağlamında (RLS) ayrı yazılır:
/// başarılı giriş yalnızca seçilen firmaya, hatalı şifre, kilitlenme ve şifre sıfırlama kişinin etkin üye olduğu tüm
/// firmalara, davet kabulü daveti kabul edilen firmalara. Kayıtlı olmayan e-postayla deneme hiçbir firmaya yazılmaz.
/// </summary>
public sealed class AccountAudit(ModulixDbContext db, ITenantScopeFactory tenantScopes, ILogger<AccountAudit> logger)
{
    /// <param name="tenantIds">Yazılacak firmalar; <c>null</c>: kişinin etkin üye olduğu tüm açık firmalar.</param>
    /// <param name="reason">Başarısız girişin nedeni (<see cref="AccountAuditReasons"/>).</param>
    /// <param name="before">Değişen hesap alanlarının eski değerleri (ör. ad); yeni değerler <paramref name="details"/>'tadır.</param>
    /// <param name="details">Olaya eklenecek alanlar (ör. yeni ad, kapatılan oturum sayısı).</param>
    public async Task RecordAsync(
        ModulixUser user,
        AuditAction action,
        IReadOnlyCollection<Guid>? tenantIds,
        AccountClient client,
        CancellationToken cancellationToken,
        string? reason = null,
        JsonObject? before = null,
        JsonObject? details = null)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(client);
        tenantIds ??= await ActiveTenantIdsAsync(user.Id, cancellationToken);

        var values = new JsonObject
        {
            ["email"] = user.Email,
            ["ip"] = client.IpAddress,
            ["userAgent"] = client.UserAgent is { Length: > AccountClient.UserAgentMaxLength } agent
                ? agent[..AccountClient.UserAgentMaxLength]
                : client.UserAgent,
        };
        if (reason is not null)
        {
            values["reason"] = reason;
        }

        foreach (var (field, value) in details ?? [])
        {
            values[field] = value?.DeepClone();
        }

        foreach (var tenantId in tenantIds.Distinct())
        {
            await using var scope = tenantScopes.Create(new TenantContext(tenantId, user.Id, Guid.Empty));
            var services = scope.ServiceProvider;
            services.GetRequiredService<IAuditWriter>().Add(new AuditEntry(
                tenantId,
                user.Id,
                CoreModule.AuditedEntityKeys.Account,
                user.Id,
                action,
                RecordDiff.Compute(before?.DeepClone().AsObject(), values),
                ExecutionSource.Api,
                Activity.Current?.TraceId.ToString()));
            await services.GetRequiredService<ModulixDbContext>().SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>Kayıtlı olmayan e-postayla deneme: yalnızca uygulama günlüğüne yazılır (e-posta yazılmaz).</summary>
    public void UnknownAccount(AccountClient client) =>
        logger.LogWarning("Kayıtlı olmayan e-postayla giriş denemesi ({IpAddress}).", client.IpAddress);

    private async Task<IReadOnlyCollection<Guid>> ActiveTenantIdsAsync(Guid userId, CancellationToken cancellationToken) =>
        await (
            from membership in db.Memberships.AsNoTracking()
            join tenant in db.Tenants.AsNoTracking() on membership.TenantId equals tenant.Id
            where membership.UserId == userId
                  && membership.Status == MembershipStatus.Active
                  && tenant.Status == TenantStatus.Active
            select tenant.Id)
            .ToListAsync(cancellationToken);
}
