# CLAUDE.md

Modulix: modüler, multi-tenant kurumsal iş yönetim platformu. .NET 10 (ASP.NET Core minimal API + Blazor WebAssembly), EF Core, SQL Server 2025 (Row-Level Security). Kod, yorumlar, commit mesajları ve kullanıcıya dönük metinler **Türkçe**.

Veritabanı modeli (ADR-0009, Faz 3): ortak **platform** veritabanı (`PlatformDbContext`: kullanıcılar/kimlik, tenant kayıtları + tenant→DB defteri, üyelikler, modül erişimleri, oturumlar, e-posta kuyruğu) + **tenant başına ayrı iş** veritabanı (`ModulixDbContext`: şirket/şube, roller, ayarlar, CRM ve diğer modüller, iş audit'i, outbox). Tenant bağlantısı aktif tenant'tan `ITenantConnectionResolver` ile çözülür; platform ve tenant DB arasında cross-database FK/join yoktur. İşletim: `docs/tenant-isletim.md`.

Ayrıntılı davranış ve uç nokta belgeleri `README.md`'de, mimari kararlar `docs/adr/` altında. Gerekmedikçe README'nin tamamını okuma; ilgili bölümü ara.

## Komutlar

```bash
dotnet build
dotnet test tests/Modulix.ArchitectureTests      # veritabanı gerekmez, hızlı
dotnet test tests/Modulix.Core.Tests             # birim
dotnet test tests/Modulix.Modules.Crm.Tests      # birim
dotnet test tests/Modulix.IntegrationTests       # SQL Server + Mailpit gerekir
dotnet run --project src/Modulix.Api             # API + arayüz; /health
dotnet run --project src/Modulix.Api -- migrate          # platform DB migration'ları (db-up.ps1 bunu çağırır)
dotnet run --project src/Modulix.Api -- migrate-tenants  # tenant iş DB'leri migration'ları (ADR-0009); slug'larla süzülür
```

- Yerel veritabanı: `docker compose up -d --wait sqlserver mailpit && docker compose run --rm db-init` (şifreler `.env`'de; Windows'ta `scripts/db-up.ps1`). Port 14333.
- Migration (ADR-0009, iki bağlam): platform için `--context PlatformDbContext --output-dir Persistence/Migrations/Platform`, tenant için `--context ModulixDbContext --output-dir Persistence/Migrations/Tenant`; her ikisi de `--project src/Modulix.Infrastructure --startup-project src/Modulix.Api`. `dotnet ef database update` kullanma. Yeni tenant tablosu RLS politikasına da eklenir (`TenantIsolationSql.AddTable`, tenant baseline'ında).
- CI (`.github/workflows/ci.yml`) Release modunda derler, migration uygular, mimari + Core + CRM + entegrasyon testlerini çalıştırır. CI'da yüksek/kritik açıklı NuGet paketi derlemeyi durdurur.
- Değişiklikten sonra en az `dotnet build` ve mimari testleri çalıştır; veritabanı yoksa entegrasyon testlerini çalıştıramadığını açıkça söyle.

## Mimari kurallar (testlerle denetlenir)

- Proje referansları `tests/Modulix.ArchitectureTests` (`ProjectReferenceRulesTests.AllowedReferences`) ile sabit. Modüller (`Modulix.Modules.*`, `Modulix.AI`) yalnızca `Modulix.Core.Contracts`'a başvurur; `Modulix.Web` yalnızca `Modulix.Shared`'a. Yeni proje eklemeden önce kural tablosunu güncelle.
- Her modül tek bir `IModule` uygulamasıyla kaydolur (`Program.cs` → `ModulixModules.Create()`); izin anahtarları modül anahtarıyla başlar (`crm.companies.read`).
- Tenant'a ait her entity `ITenantEntity` uygular; tablo şeması = modül anahtarı.  Her tenant entity ayrıca `ICompanyShared` ya da `ICompanyScoped` uygular (ADR-0008; yoksa `TableInventoryTests` kırılır). Her yeni tenant tablosu için migration'da `TenantIsolationSql.AddTable` ekle (yoksa RLS testi kırılır).
- Kayıt işlemleri kayıt boru hattından geçer (izin, kapsam, hassas alan, özel alan, audit, outbox). Uç noktada boru hattını atlayıp doğrudan `DbContext`'e yazma.
- Tenant istemciden gelen başlık/parametreden değil, oturumdan belirlenir ve her istekte doğrulanır.
- Uç noktalar `.RequirePermission(...)` / `.RequireModule(...)` ile korunur. Yeni yönetim işlemleri denetim kaydına yazılmalı.
- E-posta `IEmailQueue.Enqueue` ile kuyruğa yazılır; HTML'e giren kullanıcı girdisi `WebUtility.HtmlEncode` ile kodlanır.
- Yeni sektör modülü için CRM'i örnek al (`src/Modulix.Modules.Crm`: `Domain`, `Persistence`, `Records`).

## Kod stili

- `.editorconfig`: file-scoped namespace, primary constructor, `var` yerine açık tip, Allman süslü parantez, 4 boşluk.
- Nullable açık; null uyarıları derleme hatasıdır.
- Çevredeki kodun yorum yoğunluğuna ve adlandırmasına uy. README'de belgelenen bir davranışı değiştirirsen README'yi de güncelle.
