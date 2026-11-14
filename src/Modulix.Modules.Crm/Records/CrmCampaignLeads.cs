using System.Text.Json.Nodes;
using Modulix.Modules.Marketing.Contracts;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Crm.Domain;

namespace Modulix.Modules.Crm.Records;

/// <summary>
/// Kampanya yanıtından lead açma sözleşmesinin CRM uygulaması (ADR-0025, dilim 7): lead kayıt boru hattından, çağıranın kimliği ve yetkisiyle açılır (lead oluşturma izni,
/// veri kapsamı, audit, olay). Kaynak bilgisi kaynak ayrıntısı ve UTM alanlarına yazılır; kaynak listesi firmaya özgü olduğundan "kaynak" seçimi yapılmaz.
/// </summary>
internal sealed class CrmCampaignLeads(IRecordPipeline pipeline) : ICampaignLeads
{
    public async Task<CampaignLeadResult> CreateAsync(CampaignLeadRequest request, CancellationToken cancellationToken)
    {
        var values = new JsonObject
        {
            ["subject"] = Cut(request.Subject, Lead.SubjectMaxLength),
            ["description"] = Cut(request.Description, Lead.TextMaxLength),
            ["firstName"] = Cut(request.FirstName, Lead.NameMaxLength),
            ["lastName"] = Cut(request.LastName, Lead.NameMaxLength),
            ["companyName"] = Cut(request.CompanyName, Lead.ShortMaxLength),
            ["email"] = Cut(request.Email, Lead.EmailMaxLength),
            ["phone"] = Cut(request.Phone, Lead.PhoneMaxLength),
            ["sourceDetail"] = Cut(request.SourceDetail, Lead.ShortMaxLength),
            ["utmSource"] = Cut(request.UtmSource, Lead.UtmMaxLength),
            ["utmMedium"] = Cut(request.UtmMedium, Lead.UtmMaxLength),
            ["utmCampaign"] = Cut(request.UtmCampaign, Lead.UtmMaxLength),
        };
        if (request.OwnerId is { } owner)
        {
            values["ownerId"] = owner.ToString();
        }

        var created = await pipeline.CreateAsync(new RecordCommand(CrmModule.Entities.Lead, null, values), cancellationToken);
        if (created.IsSuccess)
        {
            return new CampaignLeadResult(created.Value!.Id, null);
        }

        var error = created.Error!;
        return new CampaignLeadResult(
            null,
            error.Message,
            Unavailable: error.Code is RecordErrorCode.FeatureNotLicensed or RecordErrorCode.NoActiveTenant,
            Forbidden: error.Code == RecordErrorCode.PermissionDenied);
    }

    private static string? Cut(string? value, int max) => string.IsNullOrWhiteSpace(value) ? null : value.Trim() is { } text && text.Length > max ? text[..max] : value.Trim();
}
