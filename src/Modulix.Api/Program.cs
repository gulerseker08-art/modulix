using Modulix.Api.Auth;
using Modulix.Api.Development;
using Modulix.Api.Records;
using Modulix.Api.Tenancy;
using Modulix.Infrastructure.Persistence;
using Modulix.Infrastructure.Records;

var builder = WebApplication.CreateBuilder(args);

// "migrate" komutu: bekleyen migration'ları uygular ve çıkar. Uygulama bu kullanıcıyla çalışmaz.
//   dotnet run --project src/Modulix.Api -- migrate
if (args.Contains("migrate", StringComparer.OrdinalIgnoreCase))
{
    var migrationsConnection = RequiredConnectionString(builder.Configuration, PersistenceSetup.MigrationsConnectionName);
    await PersistenceSetup.MigrateAsync(migrationsConnection);
    Console.WriteLine("Migration'lar uygulandı.");
    return;
}

builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddModulixPersistence(
    RequiredConnectionString(builder.Configuration, PersistenceSetup.AppConnectionName));
builder.Services.AddModulixAuth();
builder.Services.AddModulixTenancy();
builder.Services.AddModulixRecords();
builder.Services.AddHealthChecks()
    .AddDbContextCheck<ModulixDbContext>("database");

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
    Console.WriteLine($"Örnek veri hazır. Kullanıcılar: ayse@acme.demo, mehmet@beta.demo, deniz@ortak.demo (şifre: {DemoDataSeeder.DemoPassword})");
    return;
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseMiddleware<TenantContextMiddleware>();
app.UseAuthorization();

app.MapHealthChecks("/health");
app.MapAuthEndpoints();
app.MapTenantEndpoints();
app.MapRecordEndpoints();

app.Run();

static string RequiredConnectionString(IConfiguration configuration, string name) =>
    configuration.GetConnectionString(name) is { Length: > 0 } value
        ? value
        : throw new InvalidOperationException(
            $"ConnectionStrings:{name} bulunamadı. Önce scripts\\db-up.ps1 çalıştırın.");

/// <summary>Entegrasyon testleri (WebApplicationFactory) için.</summary>
public partial class Program;
