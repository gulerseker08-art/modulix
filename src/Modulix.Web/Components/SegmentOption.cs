using Modulix.Web.Services;

namespace Modulix.Web.Components;

/// <summary><see cref="SegmentedChoice{TValue}"/> düğme grubunun bir seçeneği.</summary>
public sealed record SegmentOption<TValue>(TValue Value, string Label);

/// <summary>Birden çok formda aynı olan seçenek listeleri.</summary>
public static class SegmentOptions
{
    public static IReadOnlyList<SegmentOption<bool>> Active => [new(true, Texts.Get("common.segment.active")), new(false, Texts.Get("common.segment.passive"))];
}
