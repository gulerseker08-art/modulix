using System.Text.Json;
using System.Text.Json.Serialization;
using Modulix.Core.Contracts.Reporting;

namespace Modulix.Core.Reporting;

/// <summary>
/// Rapor tanımının JSON gösterimi (saklama ve API). Sayılar adlarıyla yazılır ("sum", "descending"); bilinmeyen alan reddedilmez ama hiçbir
/// anlamı yoktur. Çözümleme hatası <see cref="JsonException"/> olarak yükselir, arayüze "tanım okunamadı" olarak iner.
/// </summary>
public static class ReportSpecJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize(ReportSpec spec) => JsonSerializer.Serialize(spec, Options);

    public static ReportSpec Deserialize(string json)
    {
        var spec = JsonSerializer.Deserialize<ReportSpec>(json, Options) ?? throw new JsonException("Rapor tanımı boş.");
        return Defaulted(spec);
    }

    /// <summary>Eksik (null) listeleri boş liste yapar: istemci alanı hiç göndermemiş olabilir.</summary>
    public static ReportSpec Defaulted(ReportSpec spec) =>
        spec with
        {
            Dataset = spec.Dataset ?? string.Empty,
            Columns = spec.Columns ?? [],
            Filters = spec.Filters ?? [],
            Groups = spec.Groups ?? [],
            Measures = spec.Measures ?? [],
        };
}
