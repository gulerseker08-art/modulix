using System.Globalization;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Modulix.Core.Contracts.Events;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Modules.Crm.Domain;
using Modulix.Modules.Crm.Records;

namespace Modulix.Modules.Crm.Leads;

/// <summary>
/// Herkese açık Lead toplama (web formu / sunucudan sunucuya entegrasyon). Çağıran (Api) tenant'ı URL'deki firma kısa adından
/// sunucuda çözer ve isteği sistem kimliğiyle (<c>crm.lead-intake</c>) bu servise verir; gövdedeki hiçbir alan tenant, durum,
/// sorumlu, öncelik, iş birimi ya da rol belirleyemez. Kurallar:
/// <list type="bullet">
///   <item>Yalnızca izin verilen alanlar kabul edilir; bilinmeyen alan reddedilir. Uzunluk/biçim kuralları elle girişten ayrı ve daha sıkıdır.</item>
///   <item>Kimlik: sır başlığı varsa geçerli olmalıdır (sunucudan sunucuya). Yoksa gönderim tarayıcıdandır: form "yalnızca sunucu" değilse
///   ve <c>Origin</c> izinli kaynaklardan biriyse kabul edilir. Origin/CORS kimlik doğrulama DEĞİLDİR; asıl savunma alan sınırı, hız sınırı
///   ve tekrar engelidir.</item>
///   <item>Yanıt, kişinin ya da e-postanın sistemde olup olmadığını asla belli etmez; kabul edilen her gönderim aynı yanıtı alır.</item>
///   <item>Teknik tekrar: (<c>web-form:{formKey}</c>, gönderim kimliği) çifti zaten varsa yeni Lead açılmaz (idempotent, eşzamanlıda da).
///   Aynı kişinin farklı gönderimi ayrı Lead olur.</item>
///   <item>Varsayılan sorumlu pasif/üye değilse Lead atama havuzuna düşer; rastgele kullanıcıya atanmaz.</item>
///   <item>Gönderim pazarlama izni sayılmaz; izin altyapısı olmadığı için hiçbir izin kaydı oluşmaz (bkz. README).</item>
/// </list>
/// </summary>
public interface ILeadIntake
{
    Task<LeadIntakeResult> SubmitAsync(LeadIntakeSubmission submission, CancellationToken cancellationToken);

    /// <summary>Tarayıcı ön kontrolü (CORS): bu form için kaynak izinliyse kaynağı, değilse null verir.</summary>
    Task<string?> AllowedOriginAsync(string formKey, string? origin, CancellationToken cancellationToken);
}

/// <param name="Secret">Entegrasyon sırrı başlığı (sunucudan sunucuya); tarayıcıdan gelen istekte boştur.</param>
public sealed record LeadIntakeSubmission(string FormKey, string? Origin, string? Secret, JsonObject Fields);

public enum LeadIntakeOutcome
{
    /// <summary>Kabul edildi (yeni Lead, aynı gönderimin tekrarı ya da sessizce atılan spam: dışarıdan ayırt edilmez).</summary>
    Accepted,
    Invalid,
    Forbidden,
    NotFound,
}

public sealed record LeadIntakeResult(LeadIntakeOutcome Outcome, IReadOnlyDictionary<string, string[]>? Errors = null)
{
    public static LeadIntakeResult Accepted() => new(LeadIntakeOutcome.Accepted);
}

/// <summary>"crm.lead-intake" kimliğinin izin çapası (herkese açık uç sistem adına çalışır); olay işlemez.</summary>
internal sealed class LeadIntakeAnchor : IEventSubscriber
{
    public Task HandleAsync(EventEnvelope envelope, CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed partial class LeadIntake(IRecordPipeline pipeline, LeadWorkflowGate gate, ITenantMembers members) : ILeadIntake
{
    private const string WebFormSource = "WebForm";

    private static readonly Dictionary<string, int> Limits = new(StringComparer.Ordinal)
    {
        ["submissionId"] = 100, ["subject"] = 150, ["message"] = 2000, ["firstName"] = 60, ["lastName"] = 60, ["companyName"] = 120,
        ["email"] = 200, ["phone"] = 30, ["jobTitle"] = 80, ["country"] = 2, ["city"] = 60, ["productInterest"] = 120, ["referrer"] = 300,
        ["utmSource"] = 80, ["utmMedium"] = 80, ["utmCampaign"] = 80, ["website2"] = 200,
    };

    public async Task<string?> AllowedOriginAsync(string formKey, string? origin, CancellationToken cancellationToken)
    {
        var form = await FindFormAsync(formKey, cancellationToken);
        return form is not null && IsActive(form) && OriginAllowed(form, origin) ? origin : null;
    }

    public async Task<LeadIntakeResult> SubmitAsync(LeadIntakeSubmission submission, CancellationToken cancellationToken)
    {
        var form = await FindFormAsync(submission.FormKey, cancellationToken);
        if (form is null || !IsActive(form))
        {
            return new LeadIntakeResult(LeadIntakeOutcome.NotFound);
        }

        // Kimlik: sır varsa o; yoksa tarayıcı kaynağı. Başarısız denemeler ayrıntı vermez.
        if (submission.Secret is { Length: > 0 } secret)
        {
            if (!await SecretMatchesAsync(submission.FormKey, secret, cancellationToken))
            {
                return new LeadIntakeResult(LeadIntakeOutcome.Forbidden);
            }
        }
        else if (form.Values["serverOnly"]?.GetValue<bool>() == true || !OriginAllowed(form, submission.Origin))
        {
            return new LeadIntakeResult(LeadIntakeOutcome.Forbidden);
        }

        var errors = Validate(submission.Fields);
        if (errors.Count > 0)
        {
            return new LeadIntakeResult(LeadIntakeOutcome.Invalid, errors);
        }

        var fields = submission.Fields;
        // Bal tuzağı: botlar görünmez alanı doldurur. Kabul edilmiş gibi yanıtlanır, kaydedilmez.
        if (!string.IsNullOrWhiteSpace(Text(fields, "website2")))
        {
            return LeadIntakeResult.Accepted();
        }

        var externalSource = $"web-form:{submission.FormKey}";
        var submissionId = Text(fields, "submissionId")!;
        if (await ExistsAsync(externalSource, submissionId, cancellationToken))
        {
            return LeadIntakeResult.Accepted();
        }

        Guid? owner = null;
        if (form.Values["defaultOwnerId"]?.GetValue<string>() is { } ownerText
            && Guid.TryParse(ownerText, out var ownerId)
            && await members.IsActiveMemberAsync(ownerId, cancellationToken))
        {
            owner = ownerId;
        }

        var values = Map(fields, form, externalSource, submissionId, owner);

        RecordResult<RecordData> created;
        using (gate.Open(CrmModule.Permissions.LeadsCreate))
        {
            created = await pipeline.CreateAsync(new RecordCommand(CrmModule.Entities.Lead, null, values), cancellationToken);
        }

        if (created.IsSuccess)
        {
            return LeadIntakeResult.Accepted();
        }

        // Eşzamanlı aynı gönderim: benzersiz indeks ikinciyi reddeder; ilki kayıtlıysa bu da başarıdır.
        if (created.Error!.Code == RecordErrorCode.Conflict && await ExistsAsync(externalSource, submissionId, cancellationToken))
        {
            return LeadIntakeResult.Accepted();
        }

        // Sunucu tarafı doğrulama (alan hataları geri verilir); başka her hata ayrıntı vermeden reddedilir.
        return created.Error.Code == RecordErrorCode.ValidationFailed
            ? new LeadIntakeResult(LeadIntakeOutcome.Invalid, (created.Error.FieldErrors ?? [])
                .Where(e => Limits.ContainsKey(e.Field))
                .GroupBy(e => e.Field).ToDictionary(g => g.Key, g => g.Select(e => e.Message).ToArray()))
            : new LeadIntakeResult(LeadIntakeOutcome.Forbidden);
    }

    // ---- yardımcılar

    private async Task<RecordData?> FindFormAsync(string formKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(formKey) || formKey.Length > LeadForm.KeyLength)
        {
            return null;
        }

        var result = await pipeline.QueryAsync(new RecordQuery(CrmModule.Entities.LeadForm, 1, 1, null,
            new Dictionary<string, string> { ["formKey"] = formKey }), cancellationToken);
        return result.IsSuccess ? result.Value!.Items.FirstOrDefault() : null;
    }

    private static bool IsActive(RecordData form) => form.Values["isActive"]?.GetValue<bool>() == true;

    private static bool OriginAllowed(RecordData form, string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin) || LeadFormOrigins.Parse(origin) is not { Count: 1 } parsed)
        {
            return false;
        }

        return LeadFormOrigins.Parse(form.Values["allowedOrigins"]?.GetValue<string>())?.Contains(parsed[0], StringComparer.Ordinal) == true;
    }

    // Özet hiçbir zaman okunmaz: sunulan sırrın özeti, form kimliğiyle birlikte süzgeç olarak depoya verilir.
    private async Task<bool> SecretMatchesAsync(string formKey, string secret, CancellationToken cancellationToken)
    {
        var result = await pipeline.QueryAsync(new RecordQuery(CrmModule.Entities.LeadForm, 1, 1, null,
            new Dictionary<string, string> { ["formKey"] = formKey, ["secretHash"] = LeadFormSecrets.Hash(secret) }), cancellationToken);
        return result.IsSuccess && result.Value!.TotalCount > 0;
    }

    private async Task<bool> ExistsAsync(string externalSource, string submissionId, CancellationToken cancellationToken)
    {
        var existing = await pipeline.QueryAsync(new RecordQuery(CrmModule.Entities.Lead, 1, 1, null,
            new Dictionary<string, string> { ["submission"] = $"{externalSource}|{submissionId}" }), cancellationToken);
        return existing.IsSuccess && existing.Value!.TotalCount > 0;
    }

    private static string? Text(JsonObject fields, string name) =>
        fields[name] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;

    /// <summary>Web formu doğrulaması (elle girişten ayrı): bilinmeyen alan, uzunluk, biçim ve gerekli bilgiler.</summary>
    internal static Dictionary<string, string[]> Validate(JsonObject fields)
    {
        var errors = new Dictionary<string, string[]>();
        foreach (var (name, node) in fields)
        {
            if (!Limits.TryGetValue(name, out var limit))
            {
                errors[name] = ["Bilinmeyen alan."];
                continue;
            }

            if (node is null)
            {
                continue;
            }

            if (node is not JsonValue value || !value.TryGetValue<string>(out var content))
            {
                errors[name] = ["Metin olmalıdır."];
            }
            else if (content.Length > limit || ControlChars().IsMatch(content))
            {
                errors[name] = [$"En fazla {limit} karakter olabilir ve denetim karakteri içeremez."];
            }
        }

        if (errors.Count > 0)
        {
            return errors;
        }

        var submissionId = Text(fields, "submissionId");
        if (submissionId is null || !SubmissionIdPattern().IsMatch(submissionId))
        {
            errors["submissionId"] = ["8-100 karakterlik tekil bir gönderim kimliği gerekir (harf, rakam, - _ . :)."];
        }

        var email = Text(fields, "email");
        if (email is not null && (ContactNormalization.NormalizeEmail(email) is null || !MailAddress.TryCreate(email, out _)))
        {
            errors["email"] = ["Geçerli bir e-posta adresi girin."];
        }

        var phone = Text(fields, "phone");
        if (phone is not null && ContactNormalization.NormalizePhone(phone) is null)
        {
            errors["phone"] = ["Geçerli bir telefon numarası girin."];
        }

        if (Text(fields, "country") is { } country && !(country.Length == 2 && country.All(char.IsAsciiLetter)))
        {
            errors["country"] = ["İki harfli ülke kodu girin (ör. TR)."];
        }

        if (email is null && phone is null && !errors.ContainsKey("email"))
        {
            errors["email"] = ["E-posta ya da telefon gerekir."];
        }

        if (Text(fields, "firstName") is null && Text(fields, "lastName") is null && Text(fields, "companyName") is null)
        {
            errors["lastName"] = ["Ad, soyad ya da firma adı gerekir."];
        }

        if (Text(fields, "message") is null && Text(fields, "productInterest") is null && Text(fields, "subject") is null)
        {
            errors["message"] = ["Talebinizi kısaca yazın."];
        }

        return errors;
    }

    private static JsonObject Map(JsonObject f, RecordData form, string externalSource, string submissionId, Guid? owner)
    {
        var name = form.Values["name"]?.GetValue<string>() ?? "Web formu";
        var who = Text(f, "companyName") ?? string.Join(' ', new[] { Text(f, "firstName"), Text(f, "lastName") }.Where(n => n is not null));
        var subject = Text(f, "subject") ?? $"Web formu: {who}";
        return new JsonObject
        {
            ["subject"] = subject.Length > Lead.SubjectMaxLength ? subject[..Lead.SubjectMaxLength] : subject,
            ["description"] = Text(f, "message"),
            ["firstName"] = Text(f, "firstName"),
            ["lastName"] = Text(f, "lastName"),
            ["companyName"] = Text(f, "companyName"),
            ["email"] = Text(f, "email"),
            ["phone"] = Text(f, "phone"),
            ["jobTitle"] = Text(f, "jobTitle"),
            ["country"] = Text(f, "country"),
            ["city"] = Text(f, "city"),
            ["productInterest"] = Text(f, "productInterest"),
            ["referrer"] = Text(f, "referrer"),
            ["utmSource"] = Text(f, "utmSource"),
            ["utmMedium"] = Text(f, "utmMedium"),
            ["utmCampaign"] = Text(f, "utmCampaign"),
            // Aşağıdakileri istemci değil sunucu belirler.
            ["source"] = WebFormSource,
            ["sourceDetail"] = name,
            ["externalSource"] = externalSource,
            ["externalSubmissionId"] = submissionId,
            ["firstReceivedAt"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ["ownerId"] = owner?.ToString(),
        };
    }

    [GeneratedRegex(@"[\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F]")]
    private static partial Regex ControlChars();

    [GeneratedRegex(@"^[A-Za-z0-9_\-.:]{8,100}$")]
    private static partial Regex SubmissionIdPattern();
}

/// <summary>Entegrasyon sırrı üretimi ve özeti (düz sır saklanmaz).</summary>
internal static class LeadFormSecrets
{
    public const string Prefix = "lfs_";

    public static string NewSecret() =>
        Prefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    public static string Hash(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))).ToLowerInvariant();
}
