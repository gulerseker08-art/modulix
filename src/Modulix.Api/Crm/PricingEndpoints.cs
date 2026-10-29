using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Api.Access;
using Modulix.Modules.Crm;
using Modulix.Modules.Crm.Pricing;
using Modulix.Shared.Crm;

namespace Modulix.Api.Crm;

/// <summary>
/// Fiyatlandırma uç noktaları (ADR-0018): fiyat listesi, satır, vergi, kur, paket bileşeni ve indirim politikası CRUD'u ortak kayıt
/// boru hattından geçer. <c>POST /api/crm/pricing/calculate</c> tek sunucu tarafı fiyat hesabıdır (fiyat seçimi, birim dönüşümü,
/// indirim, vergi, yuvarlama, isteğe bağlı kur); hata kararlı bir <c>code</c> ile 422 döner. Hesap veri değiştirmez, maliyet dönmez.
/// </summary>
internal static class PricingEndpoints
{
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;

    public static void Map(RouteGroupBuilder crm)
    {
        CrmEndpoints.MapResource<TaxRateDto, SaveTaxRateRequest>(crm, "tax-rates", CrmModule.Entities.TaxRate,
            CrmModule.Permissions.PricesRead, CrmModule.Permissions.PricesWrite);
        CrmEndpoints.MapResource<ExchangeRateDto, SaveExchangeRateRequest>(crm, "exchange-rates", CrmModule.Entities.ExchangeRate,
            CrmModule.Permissions.PricesRead, CrmModule.Permissions.PricesWrite);
        CrmEndpoints.MapResource<PriceListDto, SavePriceListRequest>(crm, "price-lists", CrmModule.Entities.PriceList,
            CrmModule.Permissions.PricesRead, CrmModule.Permissions.PricesWrite);
        CrmEndpoints.MapResource<PriceListItemDto, SavePriceListItemRequest>(crm, "price-list-items", CrmModule.Entities.PriceListItem,
            CrmModule.Permissions.PricesRead, CrmModule.Permissions.PricesWrite);
        CrmEndpoints.MapResource<ProductComponentDto, SaveProductComponentRequest>(crm, "product-components", CrmModule.Entities.ProductComponent,
            CrmModule.Permissions.ProductsRead, CrmModule.Permissions.ProductsWrite);
        CrmEndpoints.MapResource<DiscountPolicyDto, SaveDiscountPolicyRequest>(crm, "discount-policies", CrmModule.Entities.DiscountPolicy,
            CrmModule.Permissions.PricesRead, CrmModule.Permissions.PricesWrite);

        crm.MapPost("/pricing/calculate", CalculateAsync).RequirePermission(CrmModule.Permissions.PricesRead);
    }

    private static async Task<IResult> CalculateAsync(PriceCalculationRequestDto request, IPriceCalculator calculator, CancellationToken ct)
    {
        var outcome = await calculator.CalculateAsync(
            new PriceCalculationRequest(
                request.ProductId, request.Quantity, request.Currency, request.UnitId, request.At, request.PriceListId, request.CompanyId,
                request.Segment, request.DiscountPolicyIds, request.ManualDiscountPercent, request.DisplayCurrency),
            ct);

        if (!outcome.IsSuccess)
        {
            var status = outcome.Code == PriceCalculationErrors.ProductNotFound ? StatusCodes.Status404NotFound : StatusCodes.Status422UnprocessableEntity;
            return Results.Json(new PriceCalculationErrorDto(outcome.Code!, outcome.Message!), Json, statusCode: status);
        }

        return Results.Ok(JsonSerializer.Deserialize<PriceCalculationDto>(JsonSerializer.Serialize(outcome.Line, Json), Json));
    }
}
