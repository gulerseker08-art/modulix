using Microsoft.JSInterop;
using Modulix.Shared.Reporting;

namespace Modulix.Web.Services;

/// <summary>
/// Hazırlanmış rapor dosyasını indirir. Dosya, oturum çerezi ile <see cref="ApiClient"/> üzerinden alınır (hata kodları ayrı okunabilsin diye
/// doğrudan bağlantı kullanılmaz) ve tarayıcıya <c>modulixDownload</c> ile verilir. İndirme anında sunucu yetkiyi yeniden denetler.
/// </summary>
public static class ReportExportDownloads
{
    /// <returns>Hata iletisi; başarılıysa <c>null</c>.</returns>
    public static async Task<string?> DownloadAsync(ApiClient api, IJSRuntime js, ReportExportDto export)
    {
        var result = await api.GetAsync<byte[]>($"/api/reports/exports/{export.Id}/download");
        if (!result.IsSuccess || result.Value is not { } bytes)
        {
            return ReportCenterLabels.DownloadError(result.Status, result.Error);
        }

        try
        {
            await js.InvokeVoidAsync("modulixDownload", export.FileName, "text/csv", Convert.ToBase64String(bytes));
        }
        catch (JSException)
        {
            return Texts.Get("reports.download.browserFailed");
        }

        return null;
    }
}
