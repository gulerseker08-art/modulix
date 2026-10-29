namespace Modulix.Modules.Crm.Records;

/// <summary>
/// Fırsat iş akışı kapısı (istek ömürlü). Durum, kapanış ve sistem alanları genel kayıt uçlarından yazılamaz; yalnızca
/// <c>OpportunityWorkflow</c> bu kapıyı açarak boru hattı üzerinden yazar. Kapı açıkken fırsat deposu güncelleme için eylemin
/// izinlerinden birini ister. Gerekçe, depo tarafından aynı işlemde tarihçeye yazılır.
/// </summary>
internal sealed class OpportunityWorkflowGate
{
    public IReadOnlyList<string>? Permissions { get; private set; }

    public bool IsOpen => Permissions is not null;

    /// <summary>Tarihçe kaydına yazılacak gerekçe.</summary>
    public string? Reason { get; private set; }

    public IDisposable Open(IReadOnlyList<string> permissions, string? reason = null)
    {
        Permissions = permissions;
        Reason = reason;
        return new Closer(this);
    }

    private sealed class Closer(OpportunityWorkflowGate gate) : IDisposable
    {
        public void Dispose()
        {
            gate.Permissions = null;
            gate.Reason = null;
        }
    }
}
