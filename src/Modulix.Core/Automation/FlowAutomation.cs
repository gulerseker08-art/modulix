using System.Globalization;
using System.Text.Json.Nodes;
using Modulix.Core.Contracts.Approvals;
using Modulix.Core.Contracts.Automation;
using Modulix.Core.Modules;

namespace Modulix.Core.Automation;

/// <summary>
/// Kalıcı çok adımlı iş akışı (ADR-0027, Dilim 4c-4). Ayrı bir akış motoru kurulmaz: kuralın aksiyon adımları zaten sıralı, kalıcı ve
/// idempotent çalışır (Dilim 2a). Bu iki Core aksiyonu bir adımı <b>bekleyen</b> adıma çevirir; motor çalışmayı veritabanında bırakır
/// (<see cref="AutomationActionStatus.Waiting"/>) ve zamanı gelince kaldığı adımdan sürdürür. Sunucu yeniden başlasa da akış kaybolmaz.
/// <list type="bullet">
///   <item><b>Bekle</b> (<see cref="WaitAction"/>): adımın ilk denendiği andan itibaren gün + saat; takvim günüdür (iş günü sonraki iş).</item>
///   <item><b>Onay sonucunu bekle</b> (<see cref="AwaitApprovalAction"/>): kaydın onay talebi sonuçlanana kadar bekler. Onaylanırsa akış
///   sürer; reddedilir, geri çekilir ya da geçersizleşirse akış bilerek durur (kalan adımlar atlanır); süre aşımında da durur.</item>
///   <item><b>Akışı bitir</b> (<see cref="StopAction"/>, Dilim 4c-5): akışı bilerek bitirir. Adım koşuluyla (<see cref="AutomationActionStep.When"/>)
///   birlikte dallanma kurar: koşulu sağlamayan adım atlanır, akış sürer.</item>
/// </list>
/// Kural kapanır ya da acil durdurma açılırsa bekleyen akış bir sonraki uyanışta iptal olur (motorun adım sınırı denetimi).
/// </summary>
public static class FlowAutomation
{
    public const string WaitAction = "core.flow.wait";
    public const string AwaitApprovalAction = "core.flow.await-approval";
    public const string StopAction = "core.flow.stop";

    public const int MaxWaitDays = 365;
    public const int DefaultApprovalTimeoutDays = 30;
    public const int MaxApprovalTimeoutDays = 180;

    /// <summary>Onay sonucunun yoklanma aralığı: bekleyen akış bu sıklıkla uyanıp talebe bakar (ucuz, tek sorgu).</summary>
    public static readonly TimeSpan ApprovalPollInterval = TimeSpan.FromMinutes(5);

    public static IReadOnlyList<AutomationActionRegistration> Actions { get; } =
    [
        AutomationActionRegistration.For<FlowWaitAction>(new AutomationActionDefinition(
            WaitAction,
            "Bekle",
            "Akışı belirtilen süre kadar bekletir, sonra sıradaki adımla sürer. Bekleyen akış kaydedilir; sunucu yeniden başlasa da kaldığı yerden devam eder.",
            [
                new AutomationParameter("days", "Gün", AutomationFieldType.Number, Required: false)
                {
                    AllowFieldBinding = false,
                    Description = $"0-{MaxWaitDays} takvim günü.",
                },
                new AutomationParameter("hours", "Saat", AutomationFieldType.Number, Required: false)
                {
                    AllowFieldBinding = false,
                    Description = "0-23 saat; gün ile toplanır. Toplam süre sıfırdan büyük olmalı.",
                },
            ],
            [],
            [],
            AutomationSideEffect.Read,
            SupportsDryRun: true)),
        AutomationActionRegistration.For<FlowAwaitApprovalAction>(new AutomationActionDefinition(
            AwaitApprovalAction,
            "Onay sonucunu bekle",
            "Kaydın onay talebi sonuçlanana kadar akışı bekletir. Onaylanırsa sıradaki adımlar çalışır; reddedilir, geri çekilir ya da "
            + "geçersizleşirse akış burada durur. Genellikle \"Onaya gönder\" adımından sonra kullanılır.",
            [
                new AutomationParameter("timeoutDays", "En çok kaç gün", AutomationFieldType.Number, Required: false)
                {
                    AllowFieldBinding = false,
                    Description = $"Bu sürede sonuçlanmazsa akış durur. 1-{MaxApprovalTimeoutDays}; boşsa {DefaultApprovalTimeoutDays}.",
                },
            ],
            [CoreModule.Permissions.RequestApprovals],
            [],
            AutomationSideEffect.Read,
            SupportsDryRun: true)),
        AutomationActionRegistration.For<FlowStopAction>(new AutomationActionDefinition(
            StopAction,
            "Akışı bitir",
            "Akışı bu adımda bilerek bitirir; kalan adımlar atlanır ve çalışma başarılı sayılır. Adım koşuluyla birlikte kullanılır "
            + "(ör. \"tutar 100.000'den küçükse akışı bitir\").",
            [
                new AutomationParameter("reason", "Neden", AutomationFieldType.Text, Required: false)
                {
                    AllowFieldBinding = false,
                    MaxLength = 200,
                    Description = "Çalışma geçmişinde görünür (ör. \"Küçük tutar, onay gerekmiyor\").",
                },
            ],
            [],
            [],
            AutomationSideEffect.Read,
            SupportsDryRun: true)),
    ];

    /// <summary>"Bekle" süresi; parametreler geçersizse ileti döner.</summary>
    public static (TimeSpan? Delay, string? Error) WaitDuration(JsonObject parameters)
    {
        if (!TryInt(parameters["days"], 0, out var days) || days is < 0 or > MaxWaitDays)
        {
            return (null, $"'Gün' 0-{MaxWaitDays} arasında tam sayı olmalı.");
        }

        if (!TryInt(parameters["hours"], 0, out var hours) || hours is < 0 or > 23)
        {
            return (null, "'Saat' 0-23 arasında tam sayı olmalı.");
        }

        return days == 0 && hours == 0
            ? (null, "Bekleme süresi sıfırdan büyük olmalı (gün ya da saat girin).")
            : (TimeSpan.FromDays(days) + TimeSpan.FromHours(hours), null);
    }

    /// <summary>"Onay sonucunu bekle" en çok bekleme süresi (gün); parametre geçersizse ileti döner.</summary>
    public static (int? Days, string? Error) ApprovalTimeout(JsonObject parameters) =>
        TryInt(parameters["timeoutDays"], DefaultApprovalTimeoutDays, out var days) && days is >= 1 and <= MaxApprovalTimeoutDays
            ? (days, null)
            : (null, $"'En çok kaç gün' 1-{MaxApprovalTimeoutDays} arasında tam sayı olmalı.");

    /// <summary>Süreyi okunur yazar: "3 gün 4 saat", "2 saat".</summary>
    public static string Describe(TimeSpan delay)
    {
        var parts = new List<string>();
        if (delay.Days > 0)
        {
            parts.Add($"{delay.Days} gün");
        }

        if (delay.Hours > 0)
        {
            parts.Add($"{delay.Hours} saat");
        }

        return string.Join(" ", parts);
    }

    private static bool TryInt(JsonNode? node, int fallback, out int value)
    {
        value = fallback;
        if (node is null)
        {
            return true;
        }

        if (node is JsonValue json && json.TryGetValue<int>(out value))
        {
            return true;
        }

        var text = node is JsonValue s && s.TryGetValue<string>(out var str) ? str : node.ToString();
        if (string.IsNullOrWhiteSpace(text))
        {
            value = fallback;
            return true;
        }

        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }
}

/// <summary>"Bekle": adımın ilk denendiği andan itibaren süre dolana kadar <see cref="AutomationActionStatus.Waiting"/> döner.</summary>
internal sealed class FlowWaitAction(TimeProvider time) : IAutomationAction
{
    public Task<AutomationActionResult> ExecuteAsync(AutomationActionRequest request, AutomationCallContext context, CancellationToken cancellationToken)
    {
        var (delay, error) = FlowAutomation.WaitDuration(request.Parameters);
        if (delay is not { } span)
        {
            return Task.FromResult(new AutomationActionResult(AutomationActionStatus.Failed, error!));
        }

        var text = FlowAutomation.Describe(span);
        if (request.DryRun)
        {
            return Task.FromResult(new AutomationActionResult(AutomationActionStatus.Planned, $"Akış {text} bekleyecek."));
        }

        var now = time.GetUtcNow();
        var until = (request.StepStartedAt ?? now) + span;
        return Task.FromResult(now >= until
            ? new AutomationActionResult(AutomationActionStatus.Succeeded, $"{text} beklendi.")
            : new AutomationActionResult(AutomationActionStatus.Waiting, $"{text} bekleniyor.") { ResumeAt = until });
    }
}

/// <summary>
/// "Onay sonucunu bekle": kaydın son onay talebine bakar (<see cref="IApprovalStatusReader"/>). Talep bekliyorsa yoklama aralığıyla
/// uyur; akış başlamadan önce sonuçlanmış bir talep bu akışın sonucu sayılmaz (eski onay yeni akışı geçirmez).
/// </summary>
internal sealed class FlowAwaitApprovalAction(IApprovalStatusReader approvals, TimeProvider time) : IAutomationAction
{
    public async Task<AutomationActionResult> ExecuteAsync(AutomationActionRequest request, AutomationCallContext context, CancellationToken cancellationToken)
    {
        if (request.Target is not { } target)
        {
            return Failed("Hedef kayıt yok.");
        }

        var (days, error) = FlowAutomation.ApprovalTimeout(request.Parameters);
        if (days is not { } timeoutDays)
        {
            return Failed(error!);
        }

        if (request.DryRun)
        {
            return new AutomationActionResult(AutomationActionStatus.Planned, $"Onay sonucu beklenecek (en çok {timeoutDays} gün).");
        }

        var now = time.GetUtcNow();
        var flowStart = request.FlowStartedAt ?? DateTimeOffset.MinValue;
        var latest = await approvals.LatestAsync(target, cancellationToken);
        if (latest is null || (latest.Status != "Pending" && latest.CompletedAt < flowStart))
        {
            return Failed("Bu akışta açılmış bir onay talebi yok; bu adımdan önce \"Onaya gönder\" adımı ekleyin ya da kaydı onaya gönderin.");
        }

        switch (latest.Status)
        {
            case "Approved":
                return new AutomationActionResult(AutomationActionStatus.Succeeded, $"\"{latest.Process}\" onaylandı; akış sürüyor.");
            case "Pending":
                var deadline = (request.StepStartedAt ?? now).AddDays(timeoutDays);
                if (now >= deadline)
                {
                    return new AutomationActionResult(
                        AutomationActionStatus.Skipped, $"\"{latest.Process}\" {timeoutDays} gün içinde sonuçlanmadı; akış durdu.")
                    {
                        StopFlow = true,
                    };
                }

                var next = now + FlowAutomation.ApprovalPollInterval;
                return new AutomationActionResult(AutomationActionStatus.Waiting, $"\"{latest.Process}\" onayı bekleniyor.")
                {
                    ResumeAt = next < deadline ? next : deadline,
                };
            default:
                var outcome = latest.Status switch
                {
                    "Rejected" => "reddedildi",
                    "Withdrawn" => "geri çekildi",
                    "Invalidated" => "geçersizleşti",
                    _ => "onaylanmadı",
                };
                return new AutomationActionResult(AutomationActionStatus.Skipped, $"\"{latest.Process}\" {outcome}; akış durdu.") { StopFlow = true };
        }
    }

    private static AutomationActionResult Failed(string summary) => new(AutomationActionStatus.Failed, summary);
}

/// <summary>"Akışı bitir" (Dilim 4c-5): akışı bilerek bitirir; kalan adımlar atlanır, çalışma başarılı biter.</summary>
internal sealed class FlowStopAction : IAutomationAction
{
    public Task<AutomationActionResult> ExecuteAsync(AutomationActionRequest request, AutomationCallContext context, CancellationToken cancellationToken)
    {
        var reason = request.Parameters["reason"] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
            ? text.Trim()
            : null;
        var summary = reason is null ? "Akış bu adımda bitirildi." : $"Akış bu adımda bitirildi: {reason}";
        return Task.FromResult(request.DryRun
            ? new AutomationActionResult(AutomationActionStatus.Planned, reason is null ? "Akış burada bitecek." : $"Akış burada bitecek: {reason}")
            : new AutomationActionResult(AutomationActionStatus.Succeeded, summary) { StopFlow = true });
    }
}

/// <summary>Yayında "Bekle" süresini denetler (çalışmada da aynı kural uygulanır).</summary>
internal sealed class FlowWaitCheck : IAutomationStepCheck
{
    public string ActionKey => FlowAutomation.WaitAction;

    public Task<IReadOnlyList<AutomationIssue>> CheckAsync(string entityKey, AutomationActionStep step, string path, CancellationToken cancellationToken)
    {
        var (_, error) = FlowAutomation.WaitDuration(step.Parameters);
        return Task.FromResult<IReadOnlyList<AutomationIssue>>(error is null ? [] : [new AutomationIssue($"{path}.parameters", error)]);
    }
}

/// <summary>Yayında "Onay sonucunu bekle" adımını denetler: süre aralığı ve kayıt türünün onaya açık olması.</summary>
internal sealed class FlowAwaitApprovalCheck(ModuleCatalog modules) : IAutomationStepCheck
{
    public string ActionKey => FlowAutomation.AwaitApprovalAction;

    public Task<IReadOnlyList<AutomationIssue>> CheckAsync(string entityKey, AutomationActionStep step, string path, CancellationToken cancellationToken)
    {
        var issues = new List<AutomationIssue>();
        if (modules.FindApprovalTarget(entityKey) is null)
        {
            issues.Add(new AutomationIssue(path, "Bu kayıt türü onaya gönderilemediği için onay sonucu beklenemez."));
        }

        if (FlowAutomation.ApprovalTimeout(step.Parameters).Error is { } error)
        {
            issues.Add(new AutomationIssue($"{path}.parameters.timeoutDays", error));
        }

        return Task.FromResult<IReadOnlyList<AutomationIssue>>(issues);
    }
}
