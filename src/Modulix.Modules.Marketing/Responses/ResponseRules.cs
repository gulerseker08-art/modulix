using System.Text.RegularExpressions;
using Modulix.Modules.Marketing.Domain;

namespace Modulix.Modules.Marketing.Responses;

public static class ResponseActions
{
    public const string Take = "take";
    public const string ConvertToLead = "convert-to-lead";
    public const string CreateActivity = "create-activity";
    public const string ApplyOptOut = "apply-opt-out";
    public const string Close = "close";
    public const string Reopen = "reopen";

    public static IReadOnlyList<string> All { get; } = [Take, ConvertToLead, CreateActivity, ApplyOptOut, Close, Reopen];
}

/// <summary>Yanıt çalışma kuralları (saf): hangi eylem hangi durumda ve hangi yanıt türünde yapılır. Birim testle sabit girdilerle sınanır.</summary>
public static class ResponseTransitions
{
    /// <summary>Yalnızca ilgi, soru ve görüşme talebi lead'e dönüşür; ret, çıkış ve şikâyet pazarlama fırsatı değildir.</summary>
    public static bool CanBecomeLead(string kind) => kind is ResponseKinds.Interested or ResponseKinds.Question or ResponseKinds.MeetingRequest;

    public static bool Allowed(string action, string status, string kind) => action switch
    {
        ResponseActions.Take => status == ResponseStatuses.New,
        ResponseActions.ConvertToLead => status is ResponseStatuses.New or ResponseStatuses.InProgress && CanBecomeLead(kind),
        ResponseActions.CreateActivity => status != ResponseStatuses.Closed,
        ResponseActions.ApplyOptOut => kind == ResponseKinds.OptOut && status != ResponseStatuses.Closed,
        ResponseActions.Close => status != ResponseStatuses.Closed,
        ResponseActions.Reopen => status == ResponseStatuses.Closed,
        _ => false,
    };

    /// <summary>Eylem sonrası durum. Aktivite açmak yeni yanıtı "işlemde" yapar, diğer durumları değiştirmez.</summary>
    public static string Next(string action, string status) => action switch
    {
        ResponseActions.Take => ResponseStatuses.InProgress,
        ResponseActions.ConvertToLead => ResponseStatuses.Converted,
        ResponseActions.ApplyOptOut or ResponseActions.Close => ResponseStatuses.Closed,
        ResponseActions.Reopen => ResponseStatuses.New,
        ResponseActions.CreateActivity => status == ResponseStatuses.New ? ResponseStatuses.InProgress : status,
        _ => status,
    };

    public static bool NeedsReason(string action) => action is ResponseActions.Close or ResponseActions.Reopen;
}

/// <summary>Kampanya kaynak/UTM kuralları (saf): değer normalizasyonu ve lead'e geçen değerler.</summary>
public static partial class UtmRules
{
    public const int MaxLength = 80;

    public const string DefaultSource = "kampanya";

    /// <summary>Boşsa null; aksi halde kırpılmış ve küçük harfli. Geçersizse <paramref name="valid"/> false olur.</summary>
    public static string? Normalize(string? value, out bool valid)
    {
        valid = true;
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim().ToLowerInvariant();
        valid = text.Length <= MaxLength && Pattern().IsMatch(text);
        return text;
    }

    /// <summary>Lead'e yazılacak değerler: kampanyada boş bırakılan alan varsayılana düşer (kaynak: "kampanya", ortam: kanal, kampanya: kampanya kodu).</summary>
    public static (string Source, string Medium, string Campaign) Resolve(string? source, string? medium, string? campaign, string channel, string campaignCode) =>
        (source ?? DefaultSource, medium ?? channel.ToLowerInvariant(), campaign ?? campaignCode);

    [GeneratedRegex("^[a-z0-9][a-z0-9._~-]*$")]
    private static partial Regex Pattern();
}

/// <summary>Kişi adından lead için ad ve soyad: son sözcük soyad, kalanı ad. Tek sözcük soyad sayılır.</summary>
public static class PersonNames
{
    public static (string? First, string? Last) Split(string? full)
    {
        var parts = (full ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length switch
        {
            0 => (null, null),
            1 => (null, parts[0]),
            _ => (string.Join(' ', parts[..^1]), parts[^1]),
        };
    }
}
