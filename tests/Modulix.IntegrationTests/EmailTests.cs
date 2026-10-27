using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Api.Tenancy;
using Modulix.Core.Contracts.Notifications;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Infrastructure.Email;
using Modulix.Infrastructure.Persistence;

namespace Modulix.IntegrationTests;

/// <summary>
/// E-posta kuyruğu ve işleyicisi. Testler aynı sınıfta, sırayla çalışır: işleyici veritabanındaki tüm bekleyen
/// e-postaları alır; paralel çalışan iki e-posta testi birbirinin e-postasını gönderebilirdi.
/// </summary>
public class EmailTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const int MaxAttempts = 5;

    private readonly FakeEmailTransport transport = new();
    private readonly MutableTimeProvider time = new(DateTimeOffset.UtcNow);

    [Fact]
    public async Task Queued_email_is_sent_once_and_tied_to_active_tenant()
    {
        var tenant = await factory.CreateTenantAsync();
        await using var host = FakeHost();

        var id = await EnqueueAsync(host.Services, Message(UniqueAddress()), tenant.Id);
        Assert.Equal(EmailStatus.Pending, (await LoadAsync(host.Services, id)).Status);

        await DrainAsync(host.Services);
        await DrainAsync(host.Services);

        Assert.Single(transport.Sent, e => e.Id == id);
        var email = await LoadAsync(host.Services, id);
        Assert.Equal(EmailStatus.Sent, email.Status);
        Assert.Equal(tenant.Id, email.TenantId);
        Assert.Equal(1, email.AttemptCount);
        Assert.Equal(time.GetUtcNow(), email.SentAt);
        Assert.Null(email.LastError);
    }

    [Fact]
    public async Task Email_without_active_tenant_is_a_platform_email()
    {
        await using var host = FakeHost();

        var id = await EnqueueAsync(host.Services, Message(UniqueAddress()), tenantId: null);
        await DrainAsync(host.Services);

        var email = await LoadAsync(host.Services, id);
        Assert.Null(email.TenantId);
        Assert.Equal(EmailStatus.Sent, email.Status);
    }

    [Fact]
    public async Task Email_is_persisted_immediately_on_enqueue()
    {
        // ADR-0009: e-posta kuyruğu platform veritabanındadır ve kuyruğa yazma hemen kalıcı kılınır (iş işleminin tenant
        // bağlantısını paylaşmaz), böylece aktif tenant olmayan giriş akışlarında da çalışır. Gönderim yine ertelenir.
        await using var host = FakeHost();
        var address = UniqueAddress();

        using (var scope = host.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<IEmailQueue>().Enqueue(Message(address));
        }

        using var check = host.Services.CreateScope();
        Assert.True(await Db(check).EmailMessages.AnyAsync(e => e.ToAddress == address));
    }

    [Fact]
    public async Task Failed_send_is_retried_with_backoff_then_marked_failed()
    {
        await using var host = FakeHost();
        var address = UniqueAddress();
        transport.FailFor(address, permanent: false);

        var id = await EnqueueAsync(host.Services, Message(address), tenantId: null);

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            await DrainAsync(host.Services);
            var email = await LoadAsync(host.Services, id);
            Assert.Equal(attempt, email.AttemptCount);
            Assert.Contains("geçici hata", email.LastError);

            if (attempt < MaxAttempts)
            {
                Assert.Equal(EmailStatus.Pending, email.Status);
                Assert.Equal(time.GetUtcNow() + QueuedEmail.RetryDelay(attempt), email.NextAttemptAt);

                // Zamanı gelmeden tekrar denenmez.
                await DrainAsync(host.Services);
                Assert.Equal(attempt, (await LoadAsync(host.Services, id)).AttemptCount);

                time.Advance(QueuedEmail.RetryDelay(attempt));
            }
            else
            {
                Assert.Equal(EmailStatus.Failed, email.Status);
            }
        }

        // Deneme hakkı biten e-posta, sunucu düzelse bile bir daha denenmez.
        transport.StopFailing(address);
        time.Advance(TimeSpan.FromDays(1));
        await DrainAsync(host.Services);
        Assert.Equal(MaxAttempts, (await LoadAsync(host.Services, id)).AttemptCount);
        Assert.DoesNotContain(transport.Sent, e => e.Id == id);
    }

    [Fact]
    public async Task Permanent_failure_is_not_retried()
    {
        await using var host = FakeHost();
        var address = UniqueAddress();
        transport.FailFor(address, permanent: true);

        var id = await EnqueueAsync(host.Services, Message(address), tenantId: null);
        await DrainAsync(host.Services);
        time.Advance(TimeSpan.FromDays(1));
        await DrainAsync(host.Services);

        var email = await LoadAsync(host.Services, id);
        Assert.Equal(EmailStatus.Failed, email.Status);
        Assert.Equal(1, email.AttemptCount);
        Assert.Contains("reddetti", email.LastError);
    }

    [Fact]
    public async Task Concurrent_dispatchers_send_each_email_once()
    {
        await using var host = FakeHost();
        transport.Delay = TimeSpan.FromMilliseconds(50);

        var ids = new List<Guid>();
        for (var i = 0; i < 6; i++)
        {
            ids.Add(await EnqueueAsync(host.Services, Message(UniqueAddress()), tenantId: null));
        }

        await Task.WhenAll(DrainAsync(host.Services), DrainAsync(host.Services), DrainAsync(host.Services));

        foreach (var id in ids)
        {
            Assert.Single(transport.Sent, e => e.Id == id);
            Assert.Equal(1, (await LoadAsync(host.Services, id)).AttemptCount);
        }
    }

    [Theory]
    [InlineData("gecersiz", "Konu")]
    [InlineData("Ad Soyad <ad@ornek.com>", "Konu")]
    [InlineData("ad@ornek.com", "")]
    [InlineData("ad@ornek.com", "Konu\r\nBcc: kotu@ornek.com")]
    public async Task Invalid_message_is_rejected(string to, string subject)
    {
        await using var host = FakeHost();
        using var scope = host.Services.CreateScope();
        var queue = scope.ServiceProvider.GetRequiredService<IEmailQueue>();

        Assert.ThrowsAny<ArgumentException>(() => queue.Enqueue(new EmailMessage(to, subject, "<p>içerik</p>")));
    }

    [Fact]
    public async Task Empty_body_is_rejected()
    {
        await using var host = FakeHost();
        using var scope = host.Services.CreateScope();
        var queue = scope.ServiceProvider.GetRequiredService<IEmailQueue>();

        Assert.ThrowsAny<ArgumentException>(() => queue.Enqueue(new EmailMessage(UniqueAddress(), "Konu", " ")));
    }

    /// <summary>
    /// Gerçek SMTP gönderimi: yerel test posta kutusu Mailpit (compose.yaml; yerelde scripts\db-up.ps1, CI'da
    /// iş akışı başlatır). Development ayarları localhost:1025'e gönderir; Mailpit'in API'si 8025'tedir.
    /// </summary>
    [Fact]
    public async Task Smtp_transport_delivers_to_local_mailbox()
    {
        var address = UniqueAddress();
        var subject = $"Modülix test {Guid.NewGuid():N}";

        var id = await EnqueueAsync(
            factory.Services,
            new EmailMessage(address, subject, "<p>Merhaba <b>dünya</b></p>") { ToName = "Ayşe Yılmaz", TextBody = "Merhaba dünya" },
            tenantId: null);

        for (var i = 0; i < 20 && (await LoadAsync(factory.Services, id)).Status == EmailStatus.Pending; i++)
        {
            await DrainAsync(factory.Services);
        }

        var email = await LoadAsync(factory.Services, id);
        Assert.True(email.Status == EmailStatus.Sent, $"Durum {email.Status}: {email.LastError}");

        using var mailpit = new HttpClient { BaseAddress = new Uri("http://localhost:8025") };
        var result = await mailpit.GetFromJsonAsync<JsonObject>($"/api/v1/search?query={Uri.EscapeDataString($"to:{address}")}");
        var message = Assert.Single(result!["messages"]!.AsArray());
        Assert.Equal(subject, message!["Subject"]!.GetValue<string>());
        Assert.Equal("noreply@modulix.local", message["From"]!["Address"]!.GetValue<string>());
        Assert.Equal("Ayşe Yılmaz", message["To"]![0]!["Name"]!.GetValue<string>());
    }

    private WebApplicationFactory<Program> FakeHost() =>
        factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IEmailTransport>(transport);
            services.AddSingleton<TimeProvider>(time);
            services.Configure<EmailOptions>(options => options.MaxAttempts = MaxAttempts);
        }));

    private static async Task<Guid> EnqueueAsync(IServiceProvider services, EmailMessage message, Guid? tenantId)
    {
        using var scope = services.CreateScope();
        if (tenantId is { } id)
        {
            scope.ServiceProvider.GetRequiredService<HttpTenantContextAccessor>().Current =
                new TenantContext(id, Guid.NewGuid(), Guid.NewGuid());
        }

        // ADR-0009: kuyruk platform DB'sine hemen yazılır (EfEmailQueue kendi bağlamını kullanır); kimliği adresten okunur.
        scope.ServiceProvider.GetRequiredService<IEmailQueue>().Enqueue(message);
        return await Db(scope).EmailMessages.AsNoTracking()
            .Where(e => e.ToAddress == message.To)
            .OrderByDescending(e => e.CreatedAt)
            .Select(e => e.Id)
            .FirstAsync();
    }

    /// <summary>Zamanı gelmiş e-posta kalmayana kadar işleyiciyi çalıştırır.</summary>
    private static async Task DrainAsync(IServiceProvider services)
    {
        for (var i = 0; i < 50; i++)
        {
            using var scope = services.CreateScope();
            if (await scope.ServiceProvider.GetRequiredService<EmailDispatcher>().DispatchPendingAsync(CancellationToken.None) == 0)
            {
                return;
            }
        }
    }

    private static async Task<QueuedEmail> LoadAsync(IServiceProvider services, Guid id)
    {
        using var scope = services.CreateScope();
        return await Db(scope).EmailMessages.AsNoTracking().SingleAsync(e => e.Id == id);
    }

    private static PlatformDbContext Db(IServiceScope scope) => scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

    private static EmailMessage Message(string to) => new(to, "Deneme", "<p>Deneme</p>");

    private static string UniqueAddress() => $"e-{Guid.NewGuid():N}@test.modulix";

    private sealed class FakeEmailTransport : IEmailTransport
    {
        private readonly ConcurrentDictionary<string, bool> failures = new(StringComparer.OrdinalIgnoreCase);

        public ConcurrentQueue<OutgoingEmail> Sent { get; } = new();

        public TimeSpan Delay { get; set; }

        public void FailFor(string address, bool permanent) => failures[address] = permanent;

        public void StopFailing(string address) => failures.TryRemove(address, out _);

        public async Task SendAsync(OutgoingEmail email, CancellationToken cancellationToken)
        {
            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancellationToken);
            }

            if (failures.TryGetValue(email.ToAddress, out var permanent))
            {
                throw permanent
                    ? new PermanentEmailFailureException("Sunucu alıcıyı reddetti.")
                    : new IOException("SMTP sunucusuna ulaşılamadı (geçici hata).");
            }

            Sent.Enqueue(email);
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset now = start;

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan by) => now += by;
    }
}
