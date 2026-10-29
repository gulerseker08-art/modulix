namespace Modulix.Modules.Crm.Records;

/// <summary>
/// Lead iş akışı kapısı (istek ömürlü). Durum, sahiplik ve dönüşüm alanları genel kayıt uçlarından yazılamaz; yalnızca
/// <c>LeadWorkflow</c> bu kapıyı belirli bir izinle açarak boru hattı üzerinden yazar. Kapı açıkken Lead deposu güncelleme için
/// yalnızca o eylemin iznini ister (boru hattı yetki adımı), böylece "ata" iznine sahip biri alan düzenleyemez, "nitelendir"
/// iznine sahip biri atama yapamaz. İstemci bu kapıyı açamaz: servis kodu dışında hiçbir yerden erişilmez.
/// </summary>
internal sealed class LeadWorkflowGate
{
    /// <summary>Kapı açıksa güncellemenin gerektirdiği izin; kapalıysa null.</summary>
    public string? Permission { get; private set; }

    public bool IsOpen => Permission is not null;

    public IDisposable Open(string permission)
    {
        Permission = permission;
        return new Closer(this);
    }

    private sealed class Closer(LeadWorkflowGate gate) : IDisposable
    {
        public void Dispose() => gate.Permission = null;
    }
}
