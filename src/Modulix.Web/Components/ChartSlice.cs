namespace Modulix.Web.Components;

/// <summary>Grafik bileşenlerinin ortak veri öğesi: etiket ve değer.</summary>
public sealed record ChartSlice(string Label, decimal Value);

/// <summary>Çizgi grafiğinin bir serisi: ad ve kategori sırasıyla değerler (yoksa null).</summary>
public sealed record LineSeries(string Name, IReadOnlyList<decimal?> Values);
