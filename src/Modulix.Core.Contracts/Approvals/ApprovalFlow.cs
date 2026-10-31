namespace Modulix.Core.Contracts.Approvals;

/// <summary>Bir onay adımının gereksinimi: hangi adımda kim (boşsa onay izni olan herkes) onaylamalı. Kural başına bir satır; aynı adımdaki satırlar paralel çalışır.</summary>
public sealed record ApprovalStepSpec(int Step, Guid? ApproverId);

/// <summary>Verilmiş bir onay: kim verdi ve kimler adına sayılır (kendisi + o an vekili olduğu kişiler).</summary>
public sealed record ApprovalGiven(Guid Actor, IReadOnlySet<Guid> OnBehalfOf);

/// <summary>Bir adımın durumu: adlı onaylayanlar (boşsa onay izni olan herkes), tamamlandı mı ve onaylayanlar (vekil onayı adına onayladığı kişiyle sayılır).</summary>
public sealed record ApprovalStepState(int Step, IReadOnlyList<Guid> Approvers, bool Completed, IReadOnlyList<Guid> ApprovedBy);

/// <summary>
/// Genel çok adımlı onay akışı (saf; veritabanını ve modülü bilmez). Modüller kendi kural eşleştirmesini yapıp sonucu <see cref="ApprovalStepSpec"/>
/// listesi olarak verir: adımlar küçükten büyüğe sırayla tamamlanır (sıralı), aynı adımdaki adlı onaylayanların hepsi onaylamalıdır (paralel).
/// Adımda adlı onaylayan yoksa onay iznine sahip herhangi birinin tek onayı yeter. Hiç adım verilmezse tek, adsız adım vardır.
/// Teklif (CRM) ve sözleşme onayı aynı hesabı kullanır.
/// </summary>
public static class ApprovalFlow
{
    /// <summary>
    /// Adımları ve ilerlemeyi hesaplar. Onaylar zaman sırasıyla o anki açık adıma sayılır: adlı adımda yalnızca adı geçen (ya da adına vekâlet edilen)
    /// kişiler ilerletir; adsız adımı herhangi bir onay tamamlar.
    /// </summary>
    public static IReadOnlyList<ApprovalStepState> Steps(IEnumerable<ApprovalStepSpec> specs, IEnumerable<ApprovalGiven> approvals)
    {
        var groups = specs.GroupBy(s => s.Step).OrderBy(g => g.Key)
            .Select(g => (Step: g.Key, Approvers: (IReadOnlyList<Guid>)[.. g.Where(s => s.ApproverId is not null).Select(s => s.ApproverId!.Value).Distinct()]))
            .ToList();
        if (groups.Count == 0)
        {
            groups.Add((1, []));
        }

        var approvedBy = groups.Select(_ => new HashSet<Guid>()).ToList();
        var index = 0;
        foreach (var approval in approvals)
        {
            if (index >= groups.Count)
            {
                break;
            }

            var approvers = groups[index].Approvers;
            if (approvers.Count == 0)
            {
                approvedBy[index].Add(approval.Actor);
            }
            else
            {
                foreach (var principal in approval.OnBehalfOf.Where(approvers.Contains))
                {
                    approvedBy[index].Add(principal);
                }
            }

            if (IsComplete(approvers, approvedBy[index]))
            {
                index++;
            }
        }

        return [.. groups.Select((g, i) => new ApprovalStepState(g.Step, g.Approvers, IsComplete(g.Approvers, approvedBy[i]), [.. approvedBy[i]]))];
    }

    /// <summary>Şu anki (ilk tamamlanmamış) adım; hepsi tamamsa null.</summary>
    public static ApprovalStepState? Current(IReadOnlyList<ApprovalStepState> steps) => steps.FirstOrDefault(s => !s.Completed);

    /// <summary>Kullanıcı şu anki adımda karar verebilir mi? <paramref name="onBehalfOf"/>: kendisi + vekili olduğu kişiler. Açık adım yoksa (hepsi tamam) true döner.</summary>
    public static bool CanDecide(ApprovalStepState? current, IReadOnlySet<Guid> onBehalfOf) =>
        current is null || current.Approvers.Count == 0 || current.Approvers.Any(onBehalfOf.Contains);

    /// <summary>Bu onay verilirse bütün adımlar tamamlanır mı?</summary>
    public static bool CompletesAll(IEnumerable<ApprovalStepSpec> specs, IEnumerable<ApprovalGiven> approvals, ApprovalGiven next) =>
        Steps(specs, approvals.Append(next)).All(s => s.Completed);

    private static bool IsComplete(IReadOnlyList<Guid> approvers, HashSet<Guid> approvedBy) =>
        approvers.Count == 0 ? approvedBy.Count > 0 : approvers.All(approvedBy.Contains);
}
