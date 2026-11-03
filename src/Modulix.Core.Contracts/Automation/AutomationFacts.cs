namespace Modulix.Core.Contracts.Automation;

/// <summary>Para alanının değeri: tutar ve para birimi.</summary>
public readonly record struct AutomationMoney(decimal Amount, string Currency);

public enum AutomationFactState
{
    /// <summary>Değer var.</summary>
    Value = 1,

    /// <summary>Alan boş (null).</summary>
    Null = 2,

    /// <summary>Alan var ama değerlendiren kimlik onu göremez (alan yetkisi).</summary>
    Hidden = 3,

    /// <summary>Bilgi yok (ör. eski değer için olayın farkı yok).</summary>
    Unavailable = 4,
}

/// <summary>
/// Alanın değeri. <see cref="Value"/> tipi alan tipine göre: Text/Choice → <see cref="string"/>, Number → <see cref="decimal"/>,
/// Money → <see cref="AutomationMoney"/>, Boolean → <see cref="bool"/>, Date → <see cref="DateOnly"/>, DateTime → <see cref="DateTimeOffset"/>,
/// Lookup → <see cref="Guid"/>.
/// </summary>
public readonly record struct AutomationFact(AutomationFactState State, object? Value = null)
{
    public static AutomationFact Of(object value) => new(AutomationFactState.Value, value);

    public static AutomationFact Null { get; } = new(AutomationFactState.Null);

    public static AutomationFact Hidden { get; } = new(AutomationFactState.Hidden);

    public static AutomationFact Unavailable { get; } = new(AutomationFactState.Unavailable);
}

/// <summary>
/// Koşulun değerlendirildiği kaydın değerleri. Uygulama alan yetkisini uygular (görülemeyen alan <see cref="AutomationFactState.Hidden"/>)
/// ve ilişki sayımını modülün çözümleyicisine yönlendirir. Değerlendirici yalnızca bunu okur; veri yazmaz.
/// </summary>
public interface IAutomationFacts
{
    AutomationFact Get(string fieldKey, AutomationValueSource source);

    /// <summary>Olayın farkı var mı (eski/yeni değer ve <c>Changed</c> operatörleri için)?</summary>
    bool HasChangeInformation { get; }

    /// <summary>İlişkinin koşula uyan kayıt sayısı; ilişki bu kayıt için çözülemiyorsa <c>null</c>.</summary>
    Task<int?> CountRelatedAsync(ConditionRelation relation, CancellationToken cancellationToken);
}

/// <summary>Koşulun bir düğümünün açıklaması: geçti mi, neden. <see cref="Text"/> görülemeyen alanın değerini içermez.</summary>
public sealed record ConditionExplanation(bool Passed, string Text, IReadOnlyList<ConditionExplanation> Children);
