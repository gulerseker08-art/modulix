using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Modulix.Core.Contracts.Communication;
using Modulix.Modules.Marketing.Domain;

namespace Modulix.Modules.Marketing.Runs;

/// <summary>Çalıştırma eylemleri.</summary>
public static class RunActions
{
    public const string Submit = "submit";
    public const string Approve = "approve";
    public const string Reject = "reject";
    public const string Reopen = "reopen";
    public const string Start = "start";
    public const string Pause = "pause";
    public const string Resume = "resume";
    public const string Cancel = "cancel";
}

/// <summary>Çalıştırma durum makinesi (saf). Geçiş tablosu dışında hiçbir geçiş yapılamaz.</summary>
public static class RunTransitions
{
    private static readonly IReadOnlyDictionary<string, string[]> From = new Dictionary<string, string[]>
    {
        [RunActions.Submit] = [RunStatuses.Draft],
        [RunActions.Approve] = [RunStatuses.PendingApproval],
        [RunActions.Reject] = [RunStatuses.PendingApproval],
        [RunActions.Reopen] = [RunStatuses.PendingApproval, RunStatuses.Approved],
        [RunActions.Start] = [RunStatuses.Approved],
        [RunActions.Pause] = [RunStatuses.Running],
        [RunActions.Resume] = [RunStatuses.Paused],
        [RunActions.Cancel] = [RunStatuses.Draft, RunStatuses.PendingApproval, RunStatuses.Approved, RunStatuses.Running, RunStatuses.Paused],
    };

    public static bool Allowed(string action, string status) => From.TryGetValue(action, out var allowed) && allowed.Contains(status);

    /// <summary>Eylem sonrası durum; gönderimde onay gerekmiyorsa doğrudan onaylanmış olur.</summary>
    public static string Next(string action, bool approvalRequired) => action switch
    {
        RunActions.Submit => approvalRequired ? RunStatuses.PendingApproval : RunStatuses.Approved,
        RunActions.Approve => RunStatuses.Approved,
        RunActions.Reject or RunActions.Reopen => RunStatuses.Draft,
        RunActions.Start or RunActions.Resume => RunStatuses.Running,
        RunActions.Pause => RunStatuses.Paused,
        RunActions.Cancel => RunStatuses.Cancelled,
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    /// <summary>Gerekçe zorunlu eylemler: ret, taslağa alma, iptal.</summary>
    public static bool NeedsReason(string action) => action is RunActions.Reject or RunActions.Reopen or RunActions.Cancel;

    /// <summary>İçerik, anlık görüntü ve zaman yalnızca taslakta düzenlenir; onay sonrası değişiklik için önce taslağa alınır ve onay yeniden alınır.</summary>
    public static bool IsEditable(string status) => status == RunStatuses.Draft;
}

/// <summary>İçerik kuralları (saf): zorunlu alanlar ve güvenli bağlantılar.</summary>
public static partial class ContentRules
{
    /// <summary>İçerikteki bağlantılar yalnızca http/https olabilir; betik ve veri şemaları ve şemasız "www" olmayan başka şemalar reddedilir.</summary>
    public static IReadOnlyList<(string Field, string Message)> Validate(CommunicationChannel channel, string? subject, string? body)
    {
        var errors = new List<(string, string)>();
        if (string.IsNullOrWhiteSpace(body))
        {
            errors.Add(("body", "İçerik metni zorunludur."));
        }
        else if (body.Length > CampaignContent.BodyMaxLength)
        {
            errors.Add(("body", $"İçerik en fazla {CampaignContent.BodyMaxLength} karakter olabilir."));
        }

        if (channel == CommunicationChannel.Email && string.IsNullOrWhiteSpace(subject))
        {
            errors.Add(("subject", "E-posta için konu zorunludur."));
        }

        if (subject?.Length > CampaignContent.SubjectMaxLength)
        {
            errors.Add(("subject", $"Konu en fazla {CampaignContent.SubjectMaxLength} karakter olabilir."));
        }

        if (subject is not null && (subject.Contains('\r', StringComparison.Ordinal) || subject.Contains('\n', StringComparison.Ordinal)))
        {
            errors.Add(("subject", "Konu tek satır olmalıdır."));
        }

        foreach (var text in new[] { subject, body })
        {
            if (text is null)
            {
                continue;
            }

            foreach (Match match in SchemePattern().Matches(text))
            {
                var scheme = match.Groups[1].Value.ToLowerInvariant();
                if (scheme is not ("http" or "https"))
                {
                    errors.Add(("body", $"'{scheme}:' bağlantılarına izin verilmez; yalnızca http ve https."));
                    break;
                }
            }
        }

        return errors;
    }

    /// <summary>Metindeki "şema:" kalıpları (ör. javascript:, data:, mailto:); saatler ("10:30") ve düz yazı bu kalıba girmez (şema harfle başlar ve ardından // ya da betik karakteri gelir).</summary>
    [GeneratedRegex(@"\b(javascript|data|vbscript|file|ftp|http|https|mailto|tel)\s*:", RegexOptions.IgnoreCase)]
    private static partial Regex SchemePattern();
}

/// <summary>Teslimat anahtarı (saf).</summary>
public static class DeliveryKeys
{
    /// <summary>Tenant + çalıştırma + kanal + normalize adres → sabit, geri çevrilemez anahtar (adres saklanmaz).</summary>
    public static string For(Guid tenantId, Guid runId, CommunicationChannel channel, string normalizedAddress)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{tenantId:N}|{runId:N}|{channel}|{normalizedAddress}"));
        return Convert.ToHexString(bytes);
    }
}
