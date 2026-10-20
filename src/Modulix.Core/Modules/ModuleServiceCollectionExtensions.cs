using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Contracts.Modules;

namespace Modulix.Core.Modules;

public static class ModuleServiceCollectionExtensions
{
    /// <summary>
    /// Core modülünü ve verilen modülleri kaydeder, her modülün servislerini ekler ve
    /// <see cref="ModuleCatalog"/>'u oluşturur. Katalog geçersizse burada hata verir; uygulama açılmaz.
    /// </summary>
    public static IServiceCollection AddModulixModules(this IServiceCollection services, params IModule[] modules)
    {
        ArgumentNullException.ThrowIfNull(modules);

        var catalog = ModuleCatalog.Create(modules);

        foreach (var module in catalog.Modules)
        {
            services.AddSingleton(module);
            module.ConfigureServices(services);
        }

        services.AddSingleton(catalog);
        return services;
    }
}
