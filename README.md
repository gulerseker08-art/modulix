# Modulix

AI destekli, modüler, multi-tenant kurumsal iş yönetim platformu. Faz 1: Core + CRM + Lojistik, tüm ekranlar mobil uyumlu.

Mimari kararlar `docs/adr` klasöründedir.

## Gereksinimler

| Araç | Neden | Kontrol komutu |
| --- | --- | --- |
| .NET 10 SDK | Derleme ve testler | `dotnet --version` (10. ile başlamalı) |
| Git | Sürüm kontrolü | `git --version` |
| Docker Desktop | SQL Server (Görev 2'den itibaren) | `docker --version` |

## İlk kurulum (yalnızca bir kez)

Bu klasörün içeriğini boş repo klasörünüze kopyalayın, sonra repo kökünde çalıştırın:

**Windows**

```powershell
powershell -ExecutionPolicy Bypass -File scripts\init-solution.ps1
```

**macOS / Linux**

```bash
bash scripts/init-solution.sh
```

Betik solution'ı, 9 kaynak ve 6 test projesini oluşturur, referansları kurar, derler ve mimari testleri çalıştırır. İnternet bağlantısı gerekir (NuGet paketleri iner).

Beklenen son durum: derleme hatasız biter ve `Modulix.ArchitectureTests` içindeki tüm testler başarılı olur.

## Günlük komutlar

```bash
dotnet build
dotnet test tests/Modulix.ArchitectureTests
```

## Proje yapısı ve bağımlılık kuralları

| Proje | Görev | Referans verebileceği projeler |
| --- | --- | --- |
| `Modulix.Core.Contracts` | Modüllerin görebildiği tek Core projesi | — |
| `Modulix.Core` | Tenant, yetki, entitlement, kayıt boru hattı, audit, outbox, metadata | Core.Contracts |
| `Modulix.Infrastructure` | EF Core, SQL Server, RLS, dosya, e-posta | Core, Core.Contracts |
| `Modulix.Modules.Crm` | Firma, kişi, fırsat, aktivite | Core.Contracts |
| `Modulix.Modules.Logistics` | Sevkiyat, taşıyıcı, maliyet | Core.Contracts |
| `Modulix.AI` | LLM katmanı, tool sözleşmesi | Core.Contracts |
| `Modulix.Shared` | API DTO'ları ve doğrulama kuralları | — |
| `Modulix.Api` | Host, endpoint'ler | Hepsi |
| `Modulix.Web` | Blazor WebAssembly, responsive arayüz | Shared |

Bu kurallar `tests/Modulix.ArchitectureTests` ile her test çalışmasında denetlenir. Örneğin CRM projesine Lojistik referansı eklenirse test kırılır. Yeni bir proje eklemeden önce kural tablosunu (`ProjectReferenceRulesTests.AllowedReferences`) güncelleyin.

## Sorun giderme

- **"A compatible .NET SDK was not found"**: `global.json` .NET 10 SDK istiyor. `dotnet --list-sdks` ile kurulu sürümleri kontrol edin.
- **PowerShell betik çalıştırmayı engelliyor**: Komutu yukarıdaki gibi `-ExecutionPolicy Bypass` ile çalıştırın.
- **"Solution zaten var"**: Betik yalnızca boş repoda çalışır. Yeniden denemek için oluşan `src`, `tests`, `Modulix.sln*`, `.gitignore`, `.editorconfig` dosyalarını silin.
