using System.Globalization;
using System.Text.Json.Nodes;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Customization;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Modules.Crm.Domain;
using Modulix.Modules.Crm.Records;

namespace Modulix.Modules.Crm.Leads;

/// <summary>
/// Lead'in firma/kişi/fırsata dönüşümü. Tüm yazımlar kullanıcının kendi yetkileriyle kayıt boru hattından geçer (dönüşüm izni tek
/// başına firma/kişi/fırsat oluşturma hakkı vermez: yeni firma için firma yazma, yeni kişi için kişi yazma, fırsat için fırsat
/// yazma izni ve mevcut kayıtlar için okuma kapsamı gerekir) ve tek veritabanı işleminde yürür: herhangi bir adım başarısız olursa
/// hiçbir kayıt kalmaz. Eşzamanlı iki dönüşümde Lead sürümü ve "Lead başına tek fırsat" benzersiz indeksi yalnızca birini geçirir.
/// <list type="bullet">
///   <item>Mevcut firma/kişi kayıtlarının verisi DEĞİŞTİRİLMEZ (Lead bilgisiyle ezilmez); yalnızca yeni kayıt açılır ya da firma–kişi
///   ilişkisi eklenir.</item>
///   <item>Aktiviteler kopyalanmaz ve taşınmaz: Lead'in aktiviteleri Lead'e bağlı kalır, fırsat ekranı bunları kaynak Lead
///   üzerinden gösterir (açık görevler Lead sorumlusunda tamamlanır).</item>
///   <item>Fırsat <c>sourceLeadId</c> taşır; kaynak/kampanya bilgisi Lead'de kalır ve oradan izlenir.</item>
///   <item>Fırsat, firma kaydı ister; gerçek kişi müşteriler için tür "Individual" olan bir firma (ortak taraf) açılır.</item>
/// </list>
/// </summary>
public interface ILeadConversion
{
    /// <summary>Yazmadan, dönüşümde aktarılacak değerleri ve engelleri gösterir.</summary>
    Task<RecordResult<LeadConversionPreview>> PreviewAsync(Guid leadId, CancellationToken cancellationToken);

    Task<RecordResult<LeadConversionResult>> ConvertAsync(LeadConversionRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Dönüşüm isteği. Firma: <see cref="CompanyId"/> (mevcut) ya da <see cref="CreateCompany"/> (yeni) — ikisinden tam biri.
/// Kişi isteğe bağlı: <see cref="ContactId"/> (mevcut) ya da <see cref="CreateContact"/> (yeni). Boş bırakılan fırsat alanları
/// Lead'den doldurulur.
/// </summary>
public sealed record LeadConversionRequest(
    Guid LeadId,
    long Version,
    Guid? CompanyId = null,
    bool CreateCompany = false,
    string? CompanyName = null,
    string? CompanyKind = null,
    Guid? ContactId = null,
    bool CreateContact = false,
    string? ContactFirstName = null,
    string? ContactLastName = null,
    string? OpportunityTitle = null,
    decimal? Amount = null,
    string? Currency = null,
    DateOnly? ExpectedCloseDate = null,
    string? Stage = null);

public sealed record LeadConversionPreview(
    Guid LeadId,
    string Status,
    IReadOnlyList<string> Blockers,
    string? CompanyName,
    string CompanyKind,
    string? ContactFirstName,
    string? ContactLastName,
    string OpportunityTitle,
    decimal? Amount,
    string Currency,
    DateOnly? ExpectedCloseDate,
    Guid? ExistingCompanyId,
    Guid? ExistingContactId);

public sealed record LeadConversionResult(RecordData Lead, Guid CompanyId, Guid? ContactId, Guid OpportunityId, bool CompanyCreated, bool ContactCreated);

internal sealed class LeadConversion(
    IRecordPipeline pipeline,
    ICurrentAccess access,
    LeadWorkflowGate gate,
    IEnumerable<ILeadQualificationRule> rules,
    ITenantContextAccessor tenant,
    IChoiceLists choiceLists,
    TimeProvider time) : ILeadConversion
{
    /// <summary>Dönüşümle açılan fırsatın başlangıç aşaması (firmanın listesinde etkinse); Lead zaten nitelikli olduğu için.</summary>
    private const string QualifiedStage = "Qualified";

    public async Task<RecordResult<LeadConversionPreview>> PreviewAsync(Guid leadId, CancellationToken cancellationToken)
    {
        var read = await pipeline.GetAsync(new RecordRef(CrmModule.Entities.Lead, leadId), cancellationToken);
        if (!read.IsSuccess)
        {
            return RecordResult<LeadConversionPreview>.Fail(read.Error!.Code, read.Error.Message, read.Error.FieldErrors);
        }

        var lead = read.Value!;
        var blockers = new List<string>();
        if (LeadRules.CanConvert(Text(lead, "status") ?? LeadStatus.New) is { } blocked)
        {
            blockers.Add(blocked);
        }

        var companyName = Text(lead, "companyName");
        var organization = companyName is not null;
        var first = Text(lead, "firstName");
        var last = Text(lead, "lastName");
        var personName = string.Join(' ', new[] { first, last }.Where(n => !string.IsNullOrWhiteSpace(n)));
        return RecordResult<LeadConversionPreview>.Ok(new LeadConversionPreview(
            lead.Id,
            Text(lead, "status") ?? LeadStatus.New,
            blockers,
            organization ? companyName : (personName.Length > 0 ? personName : null),
            organization ? "Organization" : "Individual",
            first,
            last,
            Text(lead, "subject") ?? string.Empty,
            lead.Values["estimatedBudget"] is JsonValue budget && budget.TryGetValue<decimal>(out var amount) ? amount : null,
            Text(lead, "currency") ?? Opportunity.DefaultCurrency,
            Text(lead, "targetPurchaseDate") is { } date ? DateOnly.Parse(date, CultureInfo.InvariantCulture) : null,
            Id(lead, "companyId"),
            Id(lead, "contactId")));
    }

    public async Task<RecordResult<LeadConversionResult>> ConvertAsync(LeadConversionRequest request, CancellationToken cancellationToken)
    {
        // Dönüşüm izni olmadan hiçbir kayıt açılmaz (aynı denetim son güncellemede boru hattında da yapılır).
        if (!await access.HasPermissionAsync(CrmModule.Permissions.LeadsConvert, cancellationToken))
        {
            return RecordResult<LeadConversionResult>.Fail(RecordErrorCode.PermissionDenied, "Müşteri adayı dönüştürme izniniz yok.");
        }

        if ((request.CompanyId is not null) == request.CreateCompany)
        {
            return Invalid<LeadConversionResult>("companyId", "Mevcut bir firma seçin ya da yeni firma oluşturmayı seçin (ikisinden tam biri).");
        }

        if (request.ContactId is not null && request.CreateContact)
        {
            return Invalid<LeadConversionResult>("contactId", "Mevcut kişi ile yeni kişi oluşturma birlikte seçilemez.");
        }

        return await pipeline.RunAtomicAsync(ct => ConvertCoreAsync(request, ct), cancellationToken);
    }

    private async Task<RecordResult<LeadConversionResult>> ConvertCoreAsync(LeadConversionRequest request, CancellationToken cancellationToken)
    {
        var read = await pipeline.GetAsync(new RecordRef(CrmModule.Entities.Lead, request.LeadId), cancellationToken);
        if (!read.IsSuccess)
        {
            return RecordResult<LeadConversionResult>.Fail(read.Error!.Code, read.Error.Message, read.Error.FieldErrors);
        }

        var lead = read.Value!;
        if (LeadRules.CanConvert(Text(lead, "status") ?? LeadStatus.New) is { } blocked)
        {
            // İkinci dönüşüm isteği ikinci fırsat açmaz; ilk dönüşümün izini bildirir.
            var already = Text(lead, "status") == LeadStatus.Converted;
            return RecordResult<LeadConversionResult>.Fail(
                already ? RecordErrorCode.Conflict : RecordErrorCode.ValidationFailed,
                already ? $"{blocked} Fırsat: {Text(lead, "convertedOpportunityId")}" : blocked,
                [new RecordFieldError("status", blocked)]);
        }

        if (lead.Version != request.Version)
        {
            return RecordResult<LeadConversionResult>.Fail(RecordErrorCode.Conflict,
                $"Müşteri adayı siz düzenlerken değişti (beklenen sürüm {request.Version}, güncel sürüm {lead.Version}). Güncel halini alıp tekrar deneyin.");
        }

        // Dönüşüm sırasında da nitelendirme koşulları sağlanıyor olmalı (nitelendirmeden sonra veri silinmiş olabilir).
        var missing = rules.SelectMany(r => r.Check(LeadWorkflow.Snapshot(lead))).Distinct().ToList();
        if (missing.Count > 0)
        {
            return RecordResult<LeadConversionResult>.Fail(RecordErrorCode.ValidationFailed, "Müşteri adayı dönüştürülemedi: eksik bilgiler var.",
                [.. missing.Select(m => new RecordFieldError("qualification", m))]);
        }

        // 1) Firma
        Guid companyId;
        var companyCreated = false;
        if (request.CompanyId is { } existingCompany)
        {
            var company = await pipeline.GetAsync(new RecordRef(CrmModule.Entities.Company, existingCompany), cancellationToken);
            if (!company.IsSuccess)
            {
                return Fail<LeadConversionResult>(company.Error!, "companyId");
            }

            companyId = existingCompany;
        }
        else
        {
            var created = await pipeline.CreateAsync(new RecordCommand(CrmModule.Entities.Company, null, CompanyValues(lead, request)), cancellationToken);
            if (!created.IsSuccess)
            {
                return Fail<LeadConversionResult>(created.Error!, "company");
            }

            companyId = created.Value!.Id;
            companyCreated = true;
        }

        // 2) Kişi (isteğe bağlı)
        Guid? contactId = null;
        var contactCreated = false;
        if (request.ContactId is { } existingContact)
        {
            var contact = await pipeline.GetAsync(new RecordRef(CrmModule.Entities.Contact, existingContact), cancellationToken);
            if (!contact.IsSuccess)
            {
                return Fail<LeadConversionResult>(contact.Error!, "contactId");
            }

            // Firma–kişi ilişkisi tutarlılığı: kişi bu firmaya bağlı değilse mevcut kişi kaydı değiştirilmeden yeni bir ilişki açılır.
            var link = await pipeline.QueryAsync(new RecordQuery(CrmModule.Entities.CompanyContact, 1, 1, null,
                new Dictionary<string, string>
                {
                    ["companyId"] = companyId.ToString(), ["contactId"] = existingContact.ToString(), ["active"] = "true",
                }), cancellationToken);
            if (!link.IsSuccess)
            {
                return Fail<LeadConversionResult>(link.Error!, "contactId");
            }

            if (link.Value!.TotalCount == 0)
            {
                var linked = await pipeline.CreateAsync(new RecordCommand(CrmModule.Entities.CompanyContact, null, new JsonObject
                {
                    ["companyId"] = companyId.ToString(),
                    ["contactId"] = existingContact.ToString(),
                    ["jobTitle"] = Text(lead, "jobTitle"),
                }), cancellationToken);
                if (!linked.IsSuccess)
                {
                    return Fail<LeadConversionResult>(linked.Error!, "contactId");
                }
            }

            contactId = existingContact;
        }
        else if (request.CreateContact)
        {
            var values = ContactValues(lead, request, companyId);
            if (values is null)
            {
                return Invalid<LeadConversionResult>("contactFirstName", "Yeni kişi için ad ve soyad gerekir.");
            }

            var created = await pipeline.CreateAsync(new RecordCommand(CrmModule.Entities.Contact, null, values), cancellationToken);
            if (!created.IsSuccess)
            {
                return Fail<LeadConversionResult>(created.Error!, "contact");
            }

            contactId = created.Value!.Id;
            contactCreated = true;
        }

        // 3) Fırsat (kaynak Lead izlenebilir; kapı yalnızca bu oluşturma için açılır)
        RecordResult<RecordData> opportunity;
        using (gate.Open(CrmModule.Permissions.LeadsConvert))
        {
            opportunity = await pipeline.CreateAsync(
                new RecordCommand(CrmModule.Entities.Opportunity, null, await OpportunityValuesAsync(lead, request, companyId, contactId, cancellationToken)), cancellationToken);
        }

        if (!opportunity.IsSuccess)
        {
            return Fail<LeadConversionResult>(opportunity.Error!, "opportunity");
        }

        // 4) Lead'i dönüştürülmüş yap: sürüm denetimi eşzamanlı ikinci dönüşümü durdurur.
        RecordResult<RecordData> converted;
        using (gate.Open(CrmModule.Permissions.LeadsConvert))
        {
            converted = await pipeline.UpdateAsync(new RecordCommand(CrmModule.Entities.Lead, lead.Id, new JsonObject
            {
                ["status"] = LeadStatus.Converted,
                ["statusReason"] = null,
                ["followUpAt"] = null,
                ["convertedAt"] = time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture),
                ["convertedBy"] = tenant.Current?.UserId.ToString(),
                ["convertedCompanyId"] = companyId.ToString(),
                ["convertedContactId"] = contactId?.ToString(),
                ["convertedOpportunityId"] = opportunity.Value!.Id.ToString(),
            }, request.Version) { Reason = LeadEventReasons.Converted }, cancellationToken);
        }

        return converted.IsSuccess
            ? RecordResult<LeadConversionResult>.Ok(new LeadConversionResult(
                converted.Value!, companyId, contactId, opportunity.Value!.Id, companyCreated, contactCreated))
            : Fail<LeadConversionResult>(converted.Error!, "lead");
    }

    private static JsonObject CompanyValues(RecordData lead, LeadConversionRequest request)
    {
        var organization = Text(lead, "companyName") is not null && !string.Equals(request.CompanyKind, "Individual", StringComparison.Ordinal);
        var person = string.Join(' ', new[] { Text(lead, "firstName"), Text(lead, "lastName") }.Where(n => !string.IsNullOrWhiteSpace(n)));
        var values = new JsonObject
        {
            ["name"] = request.CompanyName?.Trim() is { Length: > 0 } name ? name : organization ? Text(lead, "companyName") : person,
            ["kind"] = request.CompanyKind is "Organization" or "Individual" ? request.CompanyKind : organization ? "Organization" : "Individual",
            ["industry"] = Text(lead, "industry"),
            ["city"] = Text(lead, "city"),
            ["website"] = Text(lead, "website"),
        };

        // Gerçek kişi müşteride kişinin e-posta/telefonu, kuruluşta ise talep sahibinin değil firmanın bilgisi kuruma yazılmaz; kişi kaydına gider.
        if (!organization)
        {
            values["email"] = Text(lead, "email");
            values["phone"] = Text(lead, "phone");
        }

        // Firma kaynak listesi Lead kaynağından farklıdır; yalnızca ortak anahtarlar aktarılır.
        values["source"] = Text(lead, "source") switch
        {
            "WebForm" => "Website",
            "Referral" or "Event" or "Advertising" or "Other" => Text(lead, "source"),
            _ => null,
        };
        return values;
    }

    private static JsonObject? ContactValues(RecordData lead, LeadConversionRequest request, Guid companyId)
    {
        var first = request.ContactFirstName?.Trim() is { Length: > 0 } f ? f : Text(lead, "firstName");
        var last = request.ContactLastName?.Trim() is { Length: > 0 } l ? l : Text(lead, "lastName");
        return string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(last)
            ? null
            : new JsonObject
            {
                ["firstName"] = first,
                ["lastName"] = last,
                ["companyId"] = companyId.ToString(),
                ["title"] = Text(lead, "jobTitle"),
                ["email"] = Text(lead, "email"),
                ["phone"] = Text(lead, "phone"),
            };
    }

    private async Task<JsonObject> OpportunityValuesAsync(
        RecordData lead, LeadConversionRequest request, Guid companyId, Guid? contactId, CancellationToken cancellationToken)
    {
        var values = new JsonObject
        {
            ["title"] = request.OpportunityTitle?.Trim() is { Length: > 0 } title ? title : Text(lead, "subject"),
            ["companyId"] = companyId.ToString(),
            ["contactId"] = contactId?.ToString(),
            ["currency"] = request.Currency?.Trim() is { Length: > 0 } currency ? currency : Text(lead, "currency") ?? Opportunity.DefaultCurrency,
            ["notes"] = Text(lead, "needDescription") ?? Text(lead, "description"),
            ["sourceLeadId"] = lead.Id.ToString(),
        };

        // Aşama: istek, yoksa "Qualified" (firmanın listesinde etkinse); aksi halde boru hattı listenin varsayılanını atar.
        if (request.Stage?.Trim() is { Length: > 0 } stage)
        {
            values["stage"] = stage;
        }
        else if ((await choiceLists.GetAsync(CrmModule.Lists.OpportunityStage, cancellationToken)).Find(QualifiedStage) is { Active: true })
        {
            values["stage"] = QualifiedStage;
        }

        if (Text(lead, "ownerId") is { } owner)
        {
            values["ownerId"] = owner;
        }

        // Hizmet kapsamı (ADR-0029 Dilim F2): talebin kapsamı fırsata olduğu gibi geçer; fırsatta sonradan düzenlenebilir.
        if (lead.Values["serviceScope"] is JsonArray { Count: > 0 } serviceScope)
        {
            values["serviceScope"] = serviceScope.DeepClone();
        }

        var expected =request.ExpectedCloseDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? Text(lead, "targetPurchaseDate");
        if (expected is not null)
        {
            values["expectedCloseDate"] = expected;
        }

        // Tutar hassas alandır (boru hattı iznine göre reddeder): açıkça verilen tutar olduğu gibi gönderilir; Lead bütçesi yalnızca
        // kullanıcı tutarı görebiliyorsa varsayılan olarak aktarılır, görmüyorsa sessizce atlanır.
        if (request.Amount is { } amount)
        {
            values["amount"] = amount;
        }
        else if (lead.Values["estimatedBudget"] is JsonValue budget && budget.TryGetValue<decimal>(out var estimated)
            && await access.HasPermissionAsync(CrmModule.Permissions.OpportunityAmountsRead, cancellationToken))
        {
            values["amount"] = estimated;
        }

        return values;
    }

    private static string? Text(RecordData record, string name) =>
        record.Values[name] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text : null;

    private static Guid? Id(RecordData record, string name) => Guid.TryParse(Text(record, name), out var id) ? id : null;

    private static RecordResult<T> Invalid<T>(string field, string message) =>
        RecordResult<T>.Fail(RecordErrorCode.ValidationFailed, message, [new RecordFieldError(field, message)]);

    // Alt işlemin hatası, hangi adımda olduğu belli olacak biçimde taşınır (alan hataları ön ekle).
    private static RecordResult<T> Fail<T>(RecordError error, string step) =>
        RecordResult<T>.Fail(error.Code, error.Message,
            error.FieldErrors?.Select(f => new RecordFieldError($"{step}.{f.Field}", f.Message)).ToList());
}
