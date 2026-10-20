using Microsoft.Extensions.DependencyInjection;

namespace Modulix.Core.Contracts.Modules;

/// <summary>
/// Bir modülün platforma kendini tanıttığı sözleşme. Her modül projesinde tek bir uygulaması
/// bulunur ve API host'u açılışta kaydeder (<c>AddModulixModules</c>).
/// <list type="bullet">
///   <item><see cref="Key"/> değişmez; tenant entitlement'ları ve izin anahtarları buna bağlıdır.</item>
///   <item>Her izin anahtarı modül anahtarı ve nokta ile başlar (ör. "crm.companies.read").</item>
///   <item>Bir tenant için kapalı olan modülün izinleri, rolde tanımlı olsa bile geçersizdir.</item>
/// </list>
/// </summary>
public interface IModule
{
    /// <summary>Kısa, küçük harfli anahtar (ör. "crm"). Değişmez.</summary>
    string Key { get; }

    /// <summary>Arayüzde gösterilen ad (ör. "CRM").</summary>
    string DisplayName { get; }

    /// <summary>Modülün tanımladığı izinler. Rollere yalnızca bu listedeki izinler atanabilir.</summary>
    IReadOnlyList<PermissionDefinition> Permissions { get; }

    /// <summary>Modülün kendi servislerini kaydeder. Modül başka modülün servisine erişmez.</summary>
    void ConfigureServices(IServiceCollection services)
    {
    }
}

/// <summary>Bir izin. <see cref="Key"/> biçimi: "modül.kaynak.işlem" (ör. "crm.companies.read").</summary>
public sealed record PermissionDefinition(string Key, string DisplayName);
