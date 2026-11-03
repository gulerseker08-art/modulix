using System.Security.Cryptography;
using System.Text;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Records;

namespace Modulix.Core.Reporting;

/// <summary>
/// Çağıranın veri kümesi için <b>erişim parmak izi</b>: geçerli izin kümesi, kayıt kapsamı ve kapsamdaki kayıt sahipleri (ekip/departman üyeliği
/// değişince sahip kümesi değişir) SHA-256 ile özetlenir. Hazırlanmış dışa aktarma dosyası bu damgayla saklanır; indirme anında damga yeniden
/// hesaplanır ve farklıysa dosya verilmez. Bu, "yetki değişiminden sonra eski hazırlanmış dosya erişilemez" kuralının uygulamasıdır; kasıtlı olarak
/// ilgisiz bir izin değişikliği de dosyayı geçersiz kılar (güvenli taraf: yeniden hazırlamak ucuzdur, sızıntı değil).
/// </summary>
public sealed class ReportAccessStamp(ICurrentAccess access, IRecordAccess records)
{
    public async Task<string> ComputeAsync(string datasetKey, CancellationToken cancellationToken)
    {
        var permissions = await access.GetPermissionsAsync(cancellationToken);
        var scope = await access.GetScopeAsync(cancellationToken);
        var owners = await records.GetOwnersAsync(cancellationToken);

        var text = new StringBuilder(datasetKey).Append('|').Append(scope).Append('|');
        text.AppendJoin(',', permissions.Order(StringComparer.Ordinal)).Append('|');
        text.Append(owners is null ? "*" : string.Join(',', owners.Order()));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
}
