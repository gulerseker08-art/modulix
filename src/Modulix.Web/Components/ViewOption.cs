namespace Modulix.Web.Components;

/// <summary>Liste görünümü seçeneği (ViewSelector / ViewTabs); <c>Icon</c> verilirse sekmede metnin önünde çizilir.</summary>
public sealed record ViewOption(string Key, string Label, string? Group = null, string? Icon = null);
