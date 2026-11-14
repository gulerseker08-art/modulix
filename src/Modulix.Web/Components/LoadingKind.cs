namespace Modulix.Web.Components;

/// <summary><see cref="LoadingState"/> iskeletinin biçimi.</summary>
public enum LoadingKind
{
    /// <summary>Birkaç satır metin (ayrıntı ve panel).</summary>
    Lines,

    /// <summary>Başlık bandı ve satırlar (liste ekranı).</summary>
    Table,

    /// <summary>Yan yana kutular (özet ve pano).</summary>
    Cards,
}
