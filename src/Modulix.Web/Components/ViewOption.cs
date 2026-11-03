namespace Modulix.Web.Components;

/// <summary>Görünüm seçicideki bir görünüm: <see cref="Key"/> seçimi, <see cref="Label"/> adı, <see cref="Group"/> menüdeki başlığı belirler (boşsa başlıksız).</summary>
public sealed record ViewOption(string Key, string Label, string? Group = null);
