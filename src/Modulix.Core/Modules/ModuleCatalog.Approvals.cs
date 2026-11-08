using Modulix.Core.Contracts.Approvals;

namespace Modulix.Core.Modules;

public sealed partial class ModuleCatalog
{
    /// <summary>Modüllerin onaya açtığı kayıt türleri (ADR-0027 Dilim 4c).</summary>
    public IEnumerable<(string ModuleKey, ApprovalTargetDefinition Target)> ApprovalTargets =>
        Modules.SelectMany(m => m.ApprovalTargets.Select(t => (m.Key, t)));

    public (string ModuleKey, ApprovalTargetDefinition Target)? FindApprovalTarget(string entityKey) =>
        ApprovalTargets.Cast<(string, ApprovalTargetDefinition)?>().FirstOrDefault(t => t!.Value.Item2.EntityKey == entityKey);
}
