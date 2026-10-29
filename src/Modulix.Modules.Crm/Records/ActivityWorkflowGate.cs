namespace Modulix.Modules.Crm.Records;

/// <summary>
/// Aktivite iş akışı kapısı (istek ömürlü), <see cref="LeadWorkflowGate"/> ile aynı kalıp: durum, gerekçe, tamamlanma, sonuç ve
/// sorumlu gibi alanlar genel kayıt uçlarından güncellenemez; yalnızca <c>ActivityWorkflow</c> kapıyı eylemin iziyle açarak
/// boru hattı üzerinden yazar. Kapı açıkken depo güncelleme için yalnızca o izni ister ("ata" izni alan düzenletmez,
/// "yeniden aç" izni atama yaptırmaz). İstemci kapıyı açamaz.
/// </summary>
internal sealed class ActivityWorkflowGate
{
    public string? Permission { get; private set; }

    public bool IsOpen => Permission is not null;

    public IDisposable Open(string permission)
    {
        Permission = permission;
        return new Closer(this);
    }

    private sealed class Closer(ActivityWorkflowGate gate) : IDisposable
    {
        public void Dispose() => gate.Permission = null;
    }
}
