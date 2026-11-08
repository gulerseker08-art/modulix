using Modulix.Core.Contracts.Automation;

namespace Modulix.Core.Automation;

/// <summary>
/// Aksiyona özgü yayın denetimi (ADR-0027, Dilim 4c): parametre şemasının yakalayamadığı, tenant verisine bağlı geçerlilik (ör. onay
/// sürecinin adı bu firmada tanımlı ve etkin mi). Doğrulayıcı, adımın aksiyonuna uyan denetimleri şema denetiminden sonra çalıştırır;
/// sorunlar aynı biçimde (yol + ileti) yayını engeller. Denetim yan etkisizdir ve çağıranın kimliğiyle çalışır.
/// </summary>
public interface IAutomationStepCheck
{
    /// <summary>Denetlenen aksiyonun anahtarı (ör. "core.approval.request").</summary>
    string ActionKey { get; }

    Task<IReadOnlyList<AutomationIssue>> CheckAsync(AutomationActionStep step, string path, CancellationToken cancellationToken);
}
