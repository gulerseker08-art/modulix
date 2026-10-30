using System.Net.Mail;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modulix.Core.Contracts.Notifications;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Events;
using Modulix.Infrastructure.Persistence;
using Modulix.Infrastructure.Tenancy;

namespace Modulix.Infrastructure.Email;

public enum EmailStatus
{
    /// <summary>Gönderilmeyi bekliyor (ilk deneme veya tekrar deneme zamanı gelmedi).</summary>
    Pending = 1,

    Sent = 2,

    /// <summary>Kalıcı hata veya deneme hakkı bitti; işleyici bir daha denemez.</summary>
    Failed = 3,
}

/// <summary>
/// E-posta kuyruğu (platform.EmailMessages). ADR-0020: kuyruk <b>platform tablolarındadır</b>; tek işleyici tüm
/// tenant'ların e-postalarını gönderir ve giriş anındaki (aktif tenant olmayan) platform e-postaları da (şifre
/// sıfırlama, kurtarma kodu) buraya yazılabilir. Her satır varsa <see cref="TenantId"/> taşır; platform e-postasında boştur.
/// </summary>
public sealed class QueuedEmail
{
    public const int AddressMaxLength = 320;
    public const int NameMaxLength = 200;
    public const int SubjectMaxLength = 400;
    public const int BodyMaxLength = 500_000;
    public const int LastErrorMaxLength = 2000;
    public const int ReferenceMaxLength = 100;

    private QueuedEmail()
    {
        // EF Core için
    }

    public QueuedEmail(EmailMessage message, Guid? tenantId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(message);

        Id = Guid.CreateVersion7();
        TenantId = tenantId;
        ToAddress = ValidateAddress(message.To);
        ToName = ValidateName(message.ToName);
        Subject = ValidateSubject(message.Subject);
        HtmlBody = ValidateBody(message.HtmlBody, nameof(message.HtmlBody), required: true)!;
        TextBody = ValidateBody(message.TextBody, nameof(message.TextBody), required: false);
        Reference = ValidateReference(message.Reference);
        Status = EmailStatus.Pending;
        CreatedAt = now;
        NextAttemptAt = now;
    }

    public Guid Id { get; private set; }

    public Guid? TenantId { get; private set; }

    public string ToAddress { get; private set; } = string.Empty;

    public string? ToName { get; private set; }

    public string Subject { get; private set; } = string.Empty;

    public string HtmlBody { get; private set; } = string.Empty;

    public string? TextBody { get; private set; }

    /// <summary>Çağıranın gönderimi izlemek için verdiği anahtar (alıcıya görünmez).</summary>
    public string? Reference { get; private set; }

    public EmailStatus Status { get; private set; }

    /// <summary>Başlamış denemeler; işleyici e-postayı alırken artırır.</summary>
    public int AttemptCount { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>
    /// Bekleyen e-posta bu zamandan sonra gönderilir. İşleyici e-postayı alırken bunu kira süresi kadar
    /// ileri atar; işleyici gönderim ortasında kapanırsa e-posta kira bitince yeniden denenir.
    /// </summary>
    public DateTimeOffset NextAttemptAt { get; private set; }

    public DateTimeOffset? SentAt { get; private set; }

    public string? LastError { get; private set; }

    public void MarkSent(DateTimeOffset now)
    {
        Status = EmailStatus.Sent;
        SentAt = now;
        LastError = null;
    }

    /// <summary>Kalıcı hatada veya deneme hakkı bittiyse <see cref="EmailStatus.Failed"/>, değilse tekrar denemeyi planlar.</summary>
    public void MarkAttemptFailed(string error, bool permanent, DateTimeOffset now, int maxAttempts)
    {
        LastError = error.Length > LastErrorMaxLength ? error[..LastErrorMaxLength] : error;
        if (permanent || AttemptCount >= maxAttempts)
        {
            Status = EmailStatus.Failed;
            return;
        }

        NextAttemptAt = now + RetryDelay(AttemptCount);
    }

    /// <summary>1., 2., 3. ve sonraki başarısız denemeden sonra bekleme: 1 dk, 5 dk, 15 dk, 1 saat.</summary>
    public static TimeSpan RetryDelay(int failedAttempts) => failedAttempts switch
    {
        <= 1 => TimeSpan.FromMinutes(1),
        2 => TimeSpan.FromMinutes(5),
        3 => TimeSpan.FromMinutes(15),
        _ => TimeSpan.FromHours(1),
    };

    private static string ValidateAddress(string address)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        var trimmed = address.Trim();

        // MailAddress "Ad <adres>" biçimini de kabul eder; burada yalnızca çıplak adres geçerlidir.
        if (trimmed.Length > AddressMaxLength
            || !MailAddress.TryCreate(trimmed, out var parsed)
            || parsed.Address != trimmed)
        {
            throw new ArgumentException($"Geçersiz e-posta adresi: '{address}'.", nameof(address));
        }

        return trimmed;
    }

    private static string? ValidateReference(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }

        var trimmed = reference.Trim();
        if (trimmed.Length > ReferenceMaxLength || HasLineBreak(trimmed))
        {
            throw new ArgumentException($"Başvuru anahtarı en fazla {ReferenceMaxLength} karakter olabilir ve satır sonu içeremez.", nameof(reference));
        }

        return trimmed;
    }

    private static string? ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var trimmed = name.Trim();
        if (trimmed.Length > NameMaxLength || HasLineBreak(trimmed))
        {
            throw new ArgumentException($"Alıcı adı en fazla {NameMaxLength} karakter olabilir ve satır sonu içeremez.", nameof(name));
        }

        return trimmed;
    }

    private static string ValidateSubject(string subject)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        var trimmed = subject.Trim();
        if (trimmed.Length > SubjectMaxLength || HasLineBreak(trimmed))
        {
            throw new ArgumentException($"Konu en fazla {SubjectMaxLength} karakter olabilir ve satır sonu içeremez.", nameof(subject));
        }

        return trimmed;
    }

    private static string? ValidateBody(string? body, string paramName, bool required)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return required ? throw new ArgumentException("E-posta içeriği boş olamaz.", paramName) : null;
        }

        if (body.Length > BodyMaxLength)
        {
            throw new ArgumentException($"E-posta içeriği en fazla {BodyMaxLength} karakter olabilir.", paramName);
        }

        return body;
    }

    private static bool HasLineBreak(string value) => value.AsSpan().IndexOfAny('\r', '\n') >= 0;
}

internal sealed class QueuedEmailConfiguration : IEntityTypeConfiguration<QueuedEmail>
{
    public void Configure(EntityTypeBuilder<QueuedEmail> builder)
    {
        builder.ToTable("EmailMessages", DatabaseSchemas.Platform);
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.ToAddress).HasMaxLength(QueuedEmail.AddressMaxLength).IsRequired();
        builder.Property(e => e.ToName).HasMaxLength(QueuedEmail.NameMaxLength);
        builder.Property(e => e.Subject).HasMaxLength(QueuedEmail.SubjectMaxLength).IsRequired();
        builder.Property(e => e.HtmlBody).IsRequired();
        builder.Property(e => e.LastError).HasMaxLength(QueuedEmail.LastErrorMaxLength);
        builder.Property(e => e.Reference).HasMaxLength(QueuedEmail.ReferenceMaxLength);
        builder.HasIndex(e => new { e.Status, e.NextAttemptAt });
        builder.HasIndex(e => new { e.TenantId, e.CreatedAt });
        builder.HasIndex(e => new { e.TenantId, e.Reference });
    }
}

public enum SmtpSecurity
{
    /// <summary>Port 465 ise SSL, değilse sunucu destekliyorsa STARTTLS.</summary>
    Auto = 0,

    /// <summary>Şifrelemesiz; yalnızca yerel test posta kutusu (Mailpit) için.</summary>
    None = 1,

    StartTls = 2,

    SslOnConnect = 3,
}

public sealed class SmtpOptions
{
    public string? Host { get; set; }

    public int Port { get; set; } = 587;

    public SmtpSecurity Security { get; set; } = SmtpSecurity.Auto;

    /// <summary>Boşsa kimlik doğrulama yapılmaz.</summary>
    public string? UserName { get; set; }

    /// <summary>Kod deposuna yazılmaz; user-secrets veya ortam değişkeni (Email__Smtp__Password).</summary>
    public string? Password { get; set; }

    public int TimeoutSeconds { get; set; } = 60;
}

/// <summary>"Email" yapılandırma bölümü.</summary>
public sealed class EmailOptions
{
    public const string SectionName = "Email";

    public string? FromAddress { get; set; }

    public string FromName { get; set; } = "Modülix";

    public SmtpOptions Smtp { get; set; } = new();

    /// <summary>Arka plan işleyicisi çalışsın mı? Entegrasyon testleri kapatır ve işleyiciyi kendisi çağırır.</summary>
    public bool WorkerEnabled { get; set; } = true;

    public int PollIntervalSeconds { get; set; } = 10;

    public int BatchSize { get; set; } = 20;

    public int MaxAttempts { get; set; } = 5;

    /// <summary>Gönderen adresi ve SMTP sunucusu tanımlı mı? Değilse e-postalar kuyrukta bekler.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(FromAddress) && !string.IsNullOrWhiteSpace(Smtp.Host);
}

/// <summary>Kuyruktan alınmış, gönderilecek e-posta.</summary>
public sealed record OutgoingEmail(Guid Id, string ToAddress, string? ToName, string Subject, string HtmlBody, string? TextBody);

/// <summary>Alıcı veya içerik reddedildi; tekrar denemek sonucu değiştirmez.</summary>
public sealed class PermanentEmailFailureException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>E-postayı gerçekten gönderen katman (SMTP). Kalıcı hatada <see cref="PermanentEmailFailureException"/> fırlatır.</summary>
public interface IEmailTransport
{
    Task SendAsync(OutgoingEmail email, CancellationToken cancellationToken);
}

/// <summary>
/// E-postayı platform kuyruğuna yazar (ADR-0020). Kuyruk platform tablolarındadır; gönderim ertelenir (Pending).
/// İki durum:
/// <list type="bullet">
///   <item><b>Olay teslimi içinde</b> (<see cref="EventHandlingScope"/> etkin): e-posta, isteğin bağlamına
///   eklenir ama <b>burada kaydedilmez</b>; dağıtıcı teslim başarıyla onaylanınca kaydeder (tam-bir-kez). Teslim
///   iptal/yeniden denemede yazılmaz, böylece bildirim tekrarlanmaz.</item>
///   <item><b>İstek/giriş akışında</b>: kendi kısa ömürlü bağlamında hemen kalıcı kılınır; aktif tenant
///   olmasa da (şifre sıfırlama vb.) çalışır.</item>
/// </list>
/// </summary>
internal sealed class EfEmailQueue(
    ModulixDbContext db,
    IServiceScopeFactory scopes,
    ITenantContextAccessor tenantAccessor,
    EventHandlingScope handling,
    TimeProvider time) : IEmailQueue
{
    public void Enqueue(EmailMessage message)
    {
        var email = new QueuedEmail(message, tenantAccessor.Current?.TenantId, time.GetUtcNow());
        if (handling.Subscription is not null)
        {
            // Olay teslimiyle birlikte onaylanır: dağıtıcı başarıda bu bağlamı kaydeder, iptalde kaydetmez.
            db.EmailMessages.Add(email);
            return;
        }

        // İsteğin bağlamındaki yarım işten bağımsız, ayrı bir bağlamda hemen kalıcı kılınır (aktif tenant gerekmez).
        using var scope = scopes.CreateScope();
        var queue = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        queue.EmailMessages.Add(email);
        queue.SaveChanges();
    }
}

/// <summary>
/// Bekleyen e-postaları gönderir. Birden fazla uygulama örneği aynı anda çalışabilir: her e-posta koşullu bir
/// UPDATE ile tek bir işleyiciye alınır, böylece iki kez gönderilmez.
/// </summary>
public sealed class EmailDispatcher(
    ModulixDbContext db,
    IEmailTransport transport,
    IOptions<EmailOptions> options,
    TimeProvider time,
    ILogger<EmailDispatcher> logger)
{
    /// <summary>Alınan e-postanın başka işleyiciye geçmeden önce gönderilmesi gereken süre; SMTP zaman aşımından uzun olmalı.</summary>
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    /// <summary>Zamanı gelmiş en fazla <see cref="EmailOptions.BatchSize"/> e-postayı dener; denenen sayıyı döner.</summary>
    public async Task<int> DispatchPendingAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (!settings.IsConfigured)
        {
            return 0;
        }

        var now = time.GetUtcNow();
        var dueIds = await db.EmailMessages.AsNoTracking()
            .Where(e => e.Status == EmailStatus.Pending && e.NextAttemptAt <= now)
            .OrderBy(e => e.NextAttemptAt)
            .Select(e => e.Id)
            .Take(Math.Max(1, settings.BatchSize))
            .ToListAsync(cancellationToken);

        var attempted = 0;
        foreach (var id in dueIds)
        {
            if (await TryClaimAsync(id, now, cancellationToken))
            {
                await SendClaimedAsync(id, settings, cancellationToken);
                attempted++;
            }
        }

        return attempted;
    }

    private async Task<bool> TryClaimAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var leaseUntil = now + Lease;
        var claimed = await db.EmailMessages
            .Where(e => e.Id == id && e.Status == EmailStatus.Pending && e.NextAttemptAt <= now)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(e => e.NextAttemptAt, leaseUntil)
                    .SetProperty(e => e.AttemptCount, e => e.AttemptCount + 1),
                cancellationToken);
        return claimed == 1;
    }

    private async Task SendClaimedAsync(Guid id, EmailOptions settings, CancellationToken cancellationToken)
    {
        var email = await db.EmailMessages.SingleAsync(e => e.Id == id, cancellationToken);
        try
        {
            await transport.SendAsync(
                new OutgoingEmail(email.Id, email.ToAddress, email.ToName, email.Subject, email.HtmlBody, email.TextBody),
                cancellationToken);
            email.MarkSent(time.GetUtcNow());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Uygulama kapanıyor: kira bitince e-posta yeniden denenir.
            throw;
        }
        catch (Exception exception)
        {
            var permanent = exception is PermanentEmailFailureException;
            email.MarkAttemptFailed($"{exception.GetType().Name}: {exception.Message}", permanent, time.GetUtcNow(), settings.MaxAttempts);

            // Alıcı adresi kişisel veridir; kayda yalnızca e-postanın kimliği yazılır.
            logger.LogWarning(
                exception,
                "E-posta {EmailId} gönderilemedi (deneme {Attempt}, durum {Status}).",
                email.Id,
                email.AttemptCount,
                email.Status);
        }

        await db.SaveChangesAsync(CancellationToken.None);
    }
}

public static class EmailSetup
{
    /// <summary>
    /// E-posta kuyruğu, işleyici ve SMTP gönderici. Ayarları host bağlar
    /// (<c>services.Configure&lt;EmailOptions&gt;(configuration.GetSection(EmailOptions.SectionName))</c>).
    /// </summary>
    public static IServiceCollection AddModulixEmail(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IEmailTransport, SmtpEmailTransport>();
        services.AddScoped<IEmailQueue, EfEmailQueue>();
        services.AddScoped<EmailDispatcher>();
        return services;
    }
}
