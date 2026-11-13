namespace Modulix.Core.Contracts.Sectors;

/// <summary>
/// Sektör kataloğunun öğesi (ADR-0043): "Gıda ve içecek", "Lojistik ve taşımacılık"... Firma (Party) kaydındaki sektör alanı bu
/// listeden seçilir ve <see cref="SharedRecords.Party.IndustryCode"/> ile koda başvurur. Platform geneli üründür (RLS dışı,
/// <c>TenantId</c> yok); platform yöneticisi yönetir, silinmez, pasife alınır (firma kayıtları kodla başvurduğu için).
/// Faaliyet ağacından (ADR-0029) bağımsızdır: ağaç tenant'ın ne iş yaptığını, bu liste firmanın hangi sektörde olduğunu söyler.
/// Entity Core.Contracts'ta durur ki modüller sektör adını sorgu içinde (sıralama, rapor, segment) okuyabilsin.
/// </summary>
public sealed class Industry
{
    public const int CodeMaxLength = ActivityCodeList.CodeMaxLength;
    public const int NameMaxLength = 100;

    private Industry()
    {
        // EF Core için
    }

    public Industry(string code, string name)
    {
        if (!ActivityCodeList.IsValidCode(code))
        {
            throw new ArgumentException($"Geçersiz sektör kodu: '{code}'.", nameof(code));
        }

        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > NameMaxLength)
        {
            throw new ArgumentException("Sektör adı boş olamaz ve en çok 100 karakter olabilir.", nameof(name));
        }

        Id = Guid.CreateVersion7();
        Code = code;
        Name = name.Trim();
        IsActive = true;
    }

    public Guid Id { get; private set; }

    public string Code { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public bool IsActive { get; private set; }

    public void SetActive(bool active) => IsActive = active;
}

/// <summary>Sektör kataloğundaki bir kaydın modüllere açılan özeti: kod, ad ve etkinlik.</summary>
public sealed record IndustryInfo(string Code, string Name, bool IsActive);

/// <summary>
/// Modüllerin sektör kataloğunu okuma sınırı. Modül sektöre ihtiyaç duyduğunda kendi listesini tutmaz, bu kataloğu kullanır.
/// </summary>
public interface IIndustryCatalog
{
    /// <summary>Bütün kayıtlar (pasifler dahil), ada göre sıralı; pasif kod eski kayıtlarda görünmeye devam eder.</summary>
    Task<IReadOnlyList<IndustryInfo>> ListAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Verilen metni katalogdaki kayda çevirir: önce koda (pasif de olsa), yoksa ada göre (büyük/küçük harf duyarsız, tam eşleşme)
    /// <i>etkin</i> kayda bakar; içe aktarılan ya da eski serbest metni koda çevirmek içindir. Bulamazsa null. Dönen entity izlenir
    /// (aynı veri bağlamı), bu yüzden <see cref="SharedRecords.Party.Industry"/>'ye doğrudan atanabilir.
    /// </summary>
    Task<Industry?> ResolveAsync(string value, CancellationToken cancellationToken);
}
