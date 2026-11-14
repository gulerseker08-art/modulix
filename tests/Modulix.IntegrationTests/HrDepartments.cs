using System.Net;
using System.Net.Http.Json;
using Modulix.Shared.Users;

namespace Modulix.IntegrationTests;

/// <summary>İK testleri için Core departmanları (ADR-0026; ADR-0042 bölüm 22): departman açma, kapatma ve kullanıcıyı departmana taşıma (yönetici oturumuyla).</summary>
internal static class HrDepartments
{
    public static async Task<Guid> RootAsync(HttpClient admin) =>
        (await admin.GetFromJsonAsync<List<BusinessUnitDto>>("/api/business-units"))!.Single(u => u.ParentId is null).Id;

    public static async Task<Guid> CreateAsync(HttpClient admin, string name, Guid? parentId = null)
    {
        var response = await admin.PostAsJsonAsync(
            "/api/business-units", new CreateBusinessUnitRequest(parentId ?? await RootAsync(admin), name, null, BusinessUnitKindLabels.Other));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<BusinessUnitDetailDto>())!.Unit.Id;
    }

    public static async Task DeactivateAsync(HttpClient admin, Guid unitId)
    {
        var response = await admin.PutAsJsonAsync($"/api/business-units/{unitId}/active", new SetBusinessUnitActiveRequest(false));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    /// <summary>Kullanıcıyı departmana taşır (Core'un departman değişikliği akışı; kayıtları kullanıcıyla taşınır).</summary>
    public static async Task MoveAsync(HttpClient admin, ModulixApiFactory factory, Guid userId, Guid tenantId, Guid unitId)
    {
        var membership = await factory.GetMembershipIdAsync(userId, tenantId);
        var response = await admin.PutAsJsonAsync(
            $"/api/users/{membership}/business-unit", new SetMembershipBusinessUnitRequest(unitId, "İK testi: departman değişikliği", MoveRecordsWithUser: true));
        Assert.True(response.StatusCode == HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync());
    }
}
