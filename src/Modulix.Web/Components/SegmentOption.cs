namespace Modulix.Web.Components;

/// <summary><see cref="SegmentedChoice{TValue}"/> düğme grubunun bir seçeneği.</summary>
public sealed record SegmentOption<TValue>(TValue Value, string Label);

/// <summary>Birden çok formda aynı olan seçenek listeleri.</summary>
public static class SegmentOptions
{
    public static readonly IReadOnlyList<SegmentOption<bool>> Active = [new(true, "Etkin"), new(false, "Pasif")];
}
