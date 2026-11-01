using Modulix.Api.Access;
using Modulix.Api.Account;
using Modulix.Api.Audit;
using Modulix.Api.Auth;
using Modulix.Api.Crm;
using Modulix.Api.Customization;
using Modulix.Api.Development;
using Modulix.Api.Email;
using Modulix.Api.Events;
using Modulix.Api.Lookups;
using Modulix.Api.Organization;
using Modulix.Api.Platform;
using Modulix.Api.Records;
using Modulix.Api.Settings;
using Modulix.Api.Tenancy;
using Modulix.Api.Users;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Contracts.Modules;
using Modulix.Core.Customization;
using Modulix.Core.Modules;
using Modulix.Infrastructure.Email;
using Modulix.Infrastructure.Events;
using Modulix.Infrastructure.Persistence;
using Modulix.Infrastructure.Records;
using Modulix.Infrastructure.Users;
using Modulix.Modules.Crm;
using Modulix.Modules.Logistics;
using Modulix.Modules.Contracts;
using Modulix.Modules.Orders;
using Modulix.Api.Contracts;
using Modulix.Api.Orders;

var builder = WebApplication.CreateBuilder(args);

// "migrate" komutu: bekleyen migration'ları uygular ve çıkar. Uygulama bu kullanıcıyla çalışmaz.
//   dotnet run --project src/Modulix.Api -- migrate
if (args.Contains("migrate", StringComparer.OrdinalIgnoreCase))
{
    // Platform ve tenant zincirleri aynı veritabanına uygulanır (ADR-0020); modül tabloları modele girsin diye katalog verilir.
    var migrationsConnection = RequiredConnectionString(builder.Configuration, PersistenceSetup.MigrationsConnectionName);
    await PersistenceSetup.MigrateAsync(migrationsConnection, ModuleCatalog.Create(ModulixModules.Create()));
    Console.WriteLine("Migration'lar uygulandı.");
    return;
}

builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddModulixPersistence(
    RequiredConnectionString(builder.Configuration, PersistenceSetup.AppConnectionName),
    RequiredConnectionString(builder.Configuration, PersistenceSetup.MigrationsConnectionName));
builder.Services.AddModulixAuth();
builder.Services.AddModulixTenancy();

builder.Services.AddModulixModules(ModulixModules.Create());
builder.Services.AddScoped<Modulix.Core.Contracts.Activities.IFollowUpActivities, Modulix.Api.Contracts.HostFollowUpActivities>();

// Sektör profilleri (Y9c): tanımlar açılışta modül kataloğuna göre doğrulanır.
builder.Services.AddSingleton(sp => new SectorProfileCatalog(SectorProfiles.Create(), sp.GetRequiredService<ModuleCatalog>()));

builder.Services.AddModulixRecords();
builder.Services.AddScoped<Modulix.Modules.Crm.Opportunities.IOpportunityPolicyProvider, Modulix.Api.Crm.OpportunityPolicyProvider>();
builder.Services.AddScoped<Modulix.Modules.Crm.Quotes.IQuotePolicyProvider, Modulix.Api.Crm.QuotePolicyProvider>();
builder.Services.AddScoped<Modulix.Modules.Contracts.Lifecycle.IContractPolicyProvider, Modulix.Api.Contracts.ContractPolicyProvider>();
builder.Services.AddScoped<Modulix.Modules.Orders.Lifecycle.IOrderPolicyProvider, Modulix.Api.Orders.OrderPolicyProvider>();
builder.Services.AddSingleton<Modulix.Modules.Contracts.Lifecycle.IContractPdfRenderer, Modulix.Api.Contracts.ContractPdfRenderer>();
builder.Services.AddScoped<Modulix.Api.Crm.QuoteDocumentService>();
builder.Services.AddScoped<Modulix.Api.Crm.QuoteDelivery>();

// E-posta: kuyruk + arka plan gönderici. Ayarlar "Email" bölümünde; SMTP şifresi user-secrets veya ortam değişkeninde.
builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection(EmailOptions.SectionName));
builder.Services.AddModulixEmail();
builder.Services.AddHostedService<EmailDispatchWorker>();

// Olaylar (ADR-0007): outbox'taki olaylar abonelere dağıtılır ve teslim edilir. Ayarlar "Events" bölümünde.
builder.Services.Configure<EventOptions>(builder.Configuration.GetSection(EventOptions.SectionName));
builder.Services.AddModulixEvents();
builder.Services.AddLeadIntakeRateLimit(builder.Configuration);
builder.Services.AddQuotePublicRateLimit(builder.Configuration);
builder.Services.AddContractSignatureRateLimit(builder.Configuration);
builder.Services.AddHostedService<EventDispatchWorker>();
builder.Services.Configure<Modulix.Api.Crm.QuoteExpiryOptions>(builder.Configuration.GetSection(Modulix.Api.Crm.QuoteExpiryOptions.SectionName));
builder.Services.AddSingleton<Modulix.Api.Crm.QuoteExpiryRunner>();
builder.Services.AddHostedService<Modulix.Api.Crm.QuoteExpiryWorker>();
builder.Services.Configure<Modulix.Api.Contracts.ContractJobsOptions>(builder.Configuration.GetSection(Modulix.Api.Contracts.ContractJobsOptions.SectionName));
builder.Services.AddSingleton<Modulix.Api.Contracts.ContractJobsRunner>();
builder.Services.AddHostedService<Modulix.Api.Contracts.ContractJobsWorker>();
builder.Services.Configure<Modulix.Api.Orders.OrderJobsOptions>(builder.Configuration.GetSection(Modulix.Api.Orders.OrderJobsOptions.SectionName));
builder.Services.AddSingleton<Modulix.Api.Orders.OrderJobsRunner>();
builder.Services.AddHostedService<Modulix.Api.Orders.OrderJobsWorker>();

// Kullanıcı yönetimi: davet sınırı "Users" bölümünde; e-postadaki bağlantıların kökü App:PublicUrl (üretimde zorunlu).
builder.Services.Configure<UserAdministrationOptions>(builder.Configuration.GetSection(UserAdministrationOptions.SectionName));
builder.Services.Configure<PublicLinkOptions>(builder.Configuration.GetSection(PublicLinkOptions.SectionName));

// Hesap güvenliği (Y7a): "Şifremi unuttum" sınırları "Account" bölümünde.
builder.Services.Configure<AccountOptions>(builder.Configuration.GetSection(AccountOptions.SectionName));
builder.Services.AddHealthChecks()
    .AddDbContextCheck<ModulixDbContext>("database")
    .AddCheck<EventHealthCheck>("events");

var app = builder.Build();

// "seed" komutu: yalnızca geliştirme ortamında örnek tenant ve kullanıcıları oluşturur ve çıkar.
//   dotnet run --project src/Modulix.Api -- seed
if (args.Contains("seed", StringComparer.OrdinalIgnoreCase))
{
    if (!app.Environment.IsDevelopment())
    {
        throw new InvalidOperationException("Örnek veri yalnızca Development ortamında oluşturulabilir.");
    }

    await DemoDataSeeder.SeedAsync(app.Services);
    Console.WriteLine($"Örnek veri hazır. Kullanıcılar: ayse@acme.demo, mehmet@beta.demo, deniz@ortak.demo, platform yöneticisi platform@modulix.demo (şifre: {DemoDataSeeder.DemoPassword})");
    return;
}

// "platform-admin" komutu: kişiyi platform yöneticisi yapar (--remove: kaldırır) ve çıkar. Arayüzden atanamaz.
//   dotnet run --project src/Modulix.Api -- platform-admin kisi@ornek.com
if (args.Contains(PlatformAdministratorCommand.Name, StringComparer.OrdinalIgnoreCase))
{
    Console.WriteLine(await PlatformAdministratorCommand.RunAsync(app.Services, args));
    return;
}

// "tenant-export" / "tenant-delete" komutları: bir tenant'ın bütün verisini TenantId ile dışa aktarır ya da siler (ADR-0020 Dilim 3).
//   dotnet run --project src/Modulix.Api -- tenant-export acme [--dir exports/acme]
//   dotnet run --project src/Modulix.Api -- tenant-delete acme --confirm acme
if (TenantDataCommand.Matches(args))
{
    Console.WriteLine(await TenantDataCommand.RunAsync(app.Services, args));
    return;
}

// "test-email" komutu: verilen adrese deneme e-postası gönderir ve sonucu yazar; SMTP ayarlarını sınamak içindir.
//   dotnet run --project src/Modulix.Api -- test-email adres@ornek.com
if (args.Contains("test-email", StringComparer.OrdinalIgnoreCase))
{
    var to = args.SkipWhile(a => !a.Equals("test-email", StringComparison.OrdinalIgnoreCase)).Skip(1).FirstOrDefault()
        ?? throw new InvalidOperationException("Alıcı adresi gerekli: -- test-email adres@ornek.com");
    Console.WriteLine(await TestEmail.SendAsync(app.Services, to));
    return;
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

// Arayüz (Blazor WebAssembly) API ile aynı adresten sunulur; oturum çerezi SameSite=Strict kalabilir.
app.UseBlazorFrameworkFiles();
app.UseStaticFiles();

app.UseRateLimiter();
app.UseAuthentication();
app.UseMiddleware<TenantContextMiddleware>();
app.UseMiddleware<CompanyContextMiddleware>();
app.UseAuthorization();

app.MapHealthChecks("/health");
app.MapAuthEndpoints();
app.MapAccountEndpoints();
app.MapTenantEndpoints();
app.MapAccessEndpoints();
app.MapUserEndpoints();
app.MapCompanyEndpoints();
app.MapTeamEndpoints();
app.MapCustomFieldEndpoints();
app.MapChoiceListEndpoints();
app.MapLookupEndpoints();
app.MapFormLayoutEndpoints();
app.MapSectorProfileEndpoints();
app.MapRecordEndpoints();
app.MapCrmEndpoints();
app.MapContractsEndpoints();
app.MapOrdersEndpoints();
app.MapLeadIntakeEndpoints();
app.MapQuotePublicEndpoints();
app.MapContractSignatureWebhook();
app.MapPlatformEndpoints();
app.MapPlatformEventEndpoints();
app.MapAuditEndpoints();
app.MapSettingsEndpoints();

// Bilinmeyen API adresi 404 döner; diğer adresler arayüzün sayfalarıdır.
app.Map("/api/{**path}", () => Results.NotFound());
app.MapFallbackToFile("index.html");

app.Run();

static string RequiredConnectionString(IConfiguration configuration, string name) =>
    configuration.GetConnectionString(name) is { Length: > 0 } value
        ? value
        : throw new InvalidOperationException(
            $"ConnectionStrings:{name} bulunamadı. Önce scripts\\db-up.ps1 çalıştırın.");

/// <summary>Entegrasyon testleri (WebApplicationFactory) için.</summary>
public partial class Program;

/// <summary>
/// Uygulamanın modülleri. Yeni bir modül eklemek için IModule uygulamasını listeye ekleyin; API, migration
/// komutu ve 'dotnet ef migrations add' aynı listeyi kullanır.
/// </summary>
internal static class ModulixModules
{
    public static IModule[] Create() => [new CrmModule(), new LogisticsModule(), new ContractsModule(), new OrdersModule()];
}
