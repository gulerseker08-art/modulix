namespace Modulix.Modules.Contracts.Domain;

/// <summary>
/// Kullanıcının kaydettiği liste görünümü (ADR-0022, dilim 9): ad ve süzgeç metni. Yalnızca sahibi görür ve değiştirir; yetki taşımaz, liste uç noktası her açılışta
/// kullanıcının yetkisini ve veri kapsamını yeniden uygular.
/// </summary>
public sealed class ContractSavedView : ContractsEntity
{
    public Guid OwnerUserId { get; internal set; }

    public string Name { get; internal set; } = string.Empty;

    /// <summary>Sorgu dizgisi biçiminde süzgeç (<see cref="Lifecycle.ContractViewRules"/>); boş olabilir.</summary>
    public string Filters { get; internal set; } = string.Empty;
}
