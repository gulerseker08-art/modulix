using System.Globalization;

namespace Modulix.Web.Services;

/// <summary>
/// Bir sayının bu dönem ve önceki eş uzunluktaki dönemdeki değeri (ör. son 30 günde ve ondan önceki 30 günde eklenen kayıt).
/// Yön yalnızca renkle anlatılmaz: metin işaretlidir ve ok ikonu eşlik eder.
/// </summary>
public sealed record PeriodChange(int Current, int Previous)
{
    /// <summary>Önceki döneme göre yön: artı arttı, eksi azaldı, sıfır aynı.</summary>
    public int Trend => Math.Sign(Current - Previous);

    /// <summary><c>dash-delta</c> rozetinin ek sınıfı; artışta ek sınıf yoktur.</summary>
    public string? CssClass => Trend switch { < 0 => "is-down", 0 => "is-flat", _ => null };

    /// <summary>Ok ikonu; değişim yoksa ikon çizilmez.</summary>
    public string? Icon => Trend switch { < 0 => "arrow-down", 0 => null, _ => "arrow-up" };

    /// <summary>Bu dönemde eklenen kayıt sayısı, işaretli ("+12"); hiç yoksa "0".</summary>
    public string Text(NumberFormatInfo numbers) => Current > 0 ? "+" + Current.ToString("N0", numbers) : "0";
}
