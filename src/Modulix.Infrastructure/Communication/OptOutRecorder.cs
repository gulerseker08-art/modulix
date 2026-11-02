using Modulix.Core.Communication;
using Modulix.Core.Contracts.Communication;

namespace Modulix.Infrastructure.Communication;

/// <summary>Yanıtla gelen çıkış talebini pazarlama izni reddi olarak kaydeder (<see cref="IOptOutRecorder"/>). Tercih, olay ve denetim kaydı çekirdek servisle yazılır.</summary>
internal sealed class OptOutRecorder(ICommunicationAdministration administration, ICommunicationPreferences preferences) : IOptOutRecorder
{
    public async Task<bool> RecordAsync(CommunicationChannel channel, string address, string reason, CancellationToken cancellationToken)
    {
        if (CommunicationAddress.Normalize(channel, address) is not { } normalized)
        {
            return false;
        }

        if ((await preferences.EvaluateAsync(channel, CommunicationPurpose.Marketing, normalized, cancellationToken)).Code == CommunicationCodes.Denied)
        {
            return false;
        }

        await administration.RecordConsentAsync(
            new ConsentChange(channel, normalized, CommunicationPurpose.Marketing, ConsentStatus.Denied, ConsentSources.Unsubscribe, null, reason), cancellationToken);
        return true;
    }
}
