namespace Modulix.Modules.Contracts.Lifecycle;

/// <summary>Eylem anahtarları; API yolları ve arayüz etiketleri bunlarla eşleşir.</summary>
public static class ContractAction
{
    public const string SendToReview = "send-to-review";
    public const string MarkReady = "mark-ready";
    public const string ReopenDraft = "reopen-draft";
    public const string SubmitForApproval = "submit-for-approval";
    public const string WithdrawApproval = "withdraw-approval";
    public const string Approve = "approve";
    public const string RejectApproval = "reject-approval";
    public const string Cancel = "cancel";
    public const string Revise = "revise";

    public static IReadOnlyList<string> All { get; } =
        [SendToReview, MarkReady, ReopenDraft, SubmitForApproval, WithdrawApproval, Approve, RejectApproval, Cancel, Revise];
}

/// <summary>Geçiş kararı için sözleşmenin ilgili durumu (veritabanı bilmez).</summary>
public sealed record ContractState(
    string Preparation,
    string Approval,
    string Signature,
    string Effective,
    bool HasStartDate,
    bool HasEndOrIndefinite,
    bool HasParty = true);

/// <summary>
/// Durum geçişi ön koşulları (ADR-0021, dilim 2). Her eylem için tetikleyici kullanıcıdır; yetki iş akışı servisinde, ön koşul burada,
/// yan etkiler (sürüm dondurma, tarihçe) serviste tanımlıdır. Kurallar API seviyesinde uygulanır; arayüz yalnızca aynı sonucu gösterir.
/// </summary>
public static class ContractTransitions
{
    public static ContractRuleResult Can(string action, ContractState s)
    {
        var signing = s.Signature is SignatureStatus.Pending or SignatureStatus.PartiallySigned;
        var signed = s.Signature == SignatureStatus.Completed;
        var closed = s.Effective is EffectiveStatus.Expired or EffectiveStatus.Terminated or EffectiveStatus.Cancelled;

        switch (action)
        {
            case ContractAction.SendToReview:
                if (s.Preparation != PreparationStatus.Draft)
                {
                    return ContractRuleResult.Deny("not-draft", "Yalnızca taslak incelemeye gönderilir.");
                }

                return closed ? Deny("closed") : ContractRuleResult.Ok;

            case ContractAction.MarkReady:
                if (s.Preparation != PreparationStatus.InReview)
                {
                    return ContractRuleResult.Deny("not-in-review", "Yalnızca incelemedeki sözleşme yayıma hazır işaretlenir.");
                }

                return MissingBasics(s) ?? ContractRuleResult.Ok;

            case ContractAction.ReopenDraft:
                if (s.Preparation == PreparationStatus.Draft)
                {
                    return ContractRuleResult.Deny("already-draft", "Sözleşme zaten taslak.");
                }

                if (s.Approval == ApprovalStatus.Pending)
                {
                    return ContractRuleResult.Deny("approval-pending", "Onay beklerken taslağa dönülemez; önce onayı geri çekin.");
                }

                if (signing || signed)
                {
                    return ContractRuleResult.Deny("signature-in-progress", "İmzaya gönderilmiş ya da imzalanmış sürüm taslağa dönemez; yeni revizyon açın.");
                }

                return closed ? Deny("closed") : ContractRuleResult.Ok;

            case ContractAction.SubmitForApproval:
                if (s.Preparation != PreparationStatus.ReadyToPublish)
                {
                    return ContractRuleResult.Deny("not-ready", "Onaya göndermek için sözleşme yayıma hazır olmalıdır.");
                }

                if (s.Approval == ApprovalStatus.Pending)
                {
                    return ContractRuleResult.Deny("approval-pending", "Onay zaten bekliyor.");
                }

                if (s.Approval == ApprovalStatus.Approved)
                {
                    return ContractRuleResult.Deny("already-approved", "Bu sürüm zaten onaylı.");
                }

                if (signing || signed)
                {
                    return ContractRuleResult.Deny("signature-in-progress", "İmza sürecindeki sürüm yeniden onaya gönderilemez.");
                }

                return MissingBasics(s) ?? (closed ? Deny("closed") : ContractRuleResult.Ok);

            case ContractAction.WithdrawApproval:
                return s.Approval == ApprovalStatus.Pending ? ContractRuleResult.Ok : ContractRuleResult.Deny("not-pending", "Bekleyen onay yok.");

            case ContractAction.Approve:
            case ContractAction.RejectApproval:
                return s.Approval == ApprovalStatus.Pending ? ContractRuleResult.Ok : ContractRuleResult.Deny("not-pending", "Onay bekleyen sürüm yok.");

            case ContractAction.Cancel:
                if (s.Effective != EffectiveStatus.NotInForce)
                {
                    return ContractRuleResult.Deny("in-force", "Yürürlük kaydı olan sözleşme iptal edilmez; fesih sürecini kullanın.");
                }

                if (signed || s.Signature == SignatureStatus.PartiallySigned)
                {
                    return ContractRuleResult.Deny("signed", "İmzalı sürüm iptal edilmez; imza iptali ve fesih ayrı süreçlerdir.");
                }

                return ContractRuleResult.Ok;

            case ContractAction.Revise:
                if (s.Approval == ApprovalStatus.Pending || signing)
                {
                    return ContractRuleResult.Deny("in-progress", "Onay ya da imza beklerken revizyon açılmaz; önce süreci sonuçlandırın.");
                }

                if (!(signed || EffectiveStatus.IsBinding(s.Effective) || s.Effective == EffectiveStatus.Expired))
                {
                    return ContractRuleResult.Deny("not-signed", "Revizyon imzalı ya da yürürlükteki sözleşme içindir; imzalanmamış sürüm taslağa döndürülerek düzenlenir.");
                }

                return s.Preparation == PreparationStatus.Draft && !signed
                    ? ContractRuleResult.Deny("draft-open", "Açık bir taslak revizyon zaten var.")
                    : ContractRuleResult.Ok;

            default:
                return ContractRuleResult.Deny("unknown-action", "Bilinmeyen eylem.");
        }
    }

    /// <summary>Eylem gerekçe ister mi? (Ret, iptal ve revizyon gerekçesiz yapılmaz.)</summary>
    public static bool RequiresReason(string action) => action is ContractAction.RejectApproval or ContractAction.Cancel or ContractAction.Revise;

    private static ContractRuleResult? MissingBasics(ContractState s)
    {
        if (!s.HasParty)
        {
            return ContractRuleResult.Deny("missing-party", "Müşteri seçilmeden ilerlenemez.");
        }

        if (!s.HasStartDate)
        {
            return ContractRuleResult.Deny("missing-start", "Başlangıç tarihi girilmeden ilerlenemez.");
        }

        return s.HasEndOrIndefinite ? null : ContractRuleResult.Deny("missing-end", "Bitiş tarihi girilmeli ya da sözleşme belirsiz süreli işaretlenmelidir.");
    }

    private static ContractRuleResult Deny(string code) => ContractRuleResult.Deny(code, "Süresi dolmuş, feshedilmiş ya da iptal edilmiş sözleşmede bu işlem yapılamaz.");
}
