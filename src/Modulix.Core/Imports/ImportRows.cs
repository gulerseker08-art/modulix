using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Modulix.Core.Contracts.Modules;
using Modulix.Core.Contracts.Records;

namespace Modulix.Core.Imports;

/// <summary>Satır sorunu. İleti hücre değerini içermez (ADR-0033): günlükte kişisel veri tutulmaz.</summary>
public sealed record ImportRowProblem(string? ColumnKey, string Code, ImportIssueSeverity Severity, string Message);

/// <summary>Dosya satırını kayıt değerlerine çevirir; hücre yorumu burada, iş kuralları kayıt boru hattındadır.</summary>
public static partial class ImportRows
{
    private static readonly string[] DateFormats = ["dd.MM.yyyy", "d.M.yyyy", "yyyy-MM-dd"];

    public static (JsonObject Values, IReadOnlyList<ImportRowProblem> Problems) Build(IReadOnlyList<ImportColumn> columns, ImportFileRow row)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(row);
        var values = new JsonObject();
        var problems = new List<ImportRowProblem>();
        foreach (var column in columns)
        {
            var field = column.Field;
            var text = column.Index < row.Cells.Count ? row.Cells[column.Index] : null;
            if (text is null)
            {
                if (field.Required)
                {
                    problems.Add(Error(field.Key, ImportIssueCodes.RequiredMissing, $"{field.Label} boş olamaz."));
                }

                continue;
            }

            var (value, code, message) = Parse(field, text);
            if (code is not null)
            {
                problems.Add(Error(field.Key, IsCustom(field.Key) && code == ImportIssueCodes.FormatInvalid ? ImportIssueCodes.CustomFieldInvalid : code, message!));
            }
            else
            {
                Assign(values, field.Key, value);
            }
        }

        return (values, problems);
    }

    /// <summary>Boru hattının reddini günlük sorunlarına çevirir; yazılmaya çalışılan değerlere bakarak boş alanı biçim hatasından ayırır.</summary>
    public static IReadOnlyList<ImportRowProblem> FromRecordError(RecordError error, JsonObject values)
    {
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(values);
        return error.Code switch
        {
            RecordErrorCode.ValidationFailed when error.FieldErrors is { Count: > 0 } fields => [.. fields.Select(f => FromField(f, values))],
            RecordErrorCode.PermissionDenied or RecordErrorCode.FeatureNotLicensed => [Error(null, ImportIssueCodes.PermissionDenied, error.Message)],
            RecordErrorCode.Conflict => [Error(null, ImportIssueCodes.DuplicateExact, error.Message)],
            _ => [Error(null, ImportIssueCodes.BusinessRule, error.Message)],
        };
    }

    private static ImportRowProblem FromField(RecordFieldError error, JsonObject values)
    {
        if (IsCustom(error.Field))
        {
            var key = error.Field[RecordFields.CustomPrefix.Length..];
            var empty = values[RecordFields.Custom] is not JsonObject custom || custom[key] is null;
            return Error(error.Field, empty ? ImportIssueCodes.RequiredMissing : ImportIssueCodes.CustomFieldInvalid, error.Message);
        }

        return Error(error.Field, values[error.Field] is null ? ImportIssueCodes.RequiredMissing : ImportIssueCodes.FormatInvalid, error.Message);
    }

    private static (JsonNode? Value, string? Code, string? Message) Parse(ImportField field, string text)
    {
        switch (field.Type)
        {
            case ImportFieldType.Decimal:
                return TryDecimal(text, out var number)
                    ? (JsonValue.Create(number), null, null)
                    : (null, ImportIssueCodes.FormatInvalid, $"{field.Label} sayı olmalı; ondalık için virgül kullanın (ör. 1.234,56).");
            case ImportFieldType.Date:
                return DateOnly.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                    ? (JsonValue.Create(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), null, null)
                    : (null, ImportIssueCodes.FormatInvalid, $"{field.Label} gg.aa.yyyy biçiminde olmalı.");
            case ImportFieldType.YesNo:
                return ImportColumns.Name(text) switch
                {
                    "evet" or "e" or "true" or "1" => (JsonValue.Create(true), null, null),
                    "hayır" or "h" or "false" or "0" => (JsonValue.Create(false), null, null),
                    _ => (null, ImportIssueCodes.FormatInvalid, $"{field.Label} Evet ya da Hayır olmalı."),
                };
            case ImportFieldType.Choice:
                return Option(field, text) is { } key
                    ? (JsonValue.Create(key), null, null)
                    : (null, ImportIssueCodes.LookupUnresolved, $"{field.Label} değeri listede yok.");
            case ImportFieldType.MultiChoice:
                var keys = text.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(t => Option(field, t)).ToList();
                return keys.Any(k => k is null)
                    ? (null, ImportIssueCodes.LookupUnresolved, $"{field.Label} değerlerinden biri listede yok.")
                    : (new JsonArray([.. keys.Select(k => (JsonNode?)JsonValue.Create(k))]), null, null);
            default:
                return (JsonValue.Create(text), null, null);
        }
    }

    // Seçenek anahtarıyla ya da görünen adıyla yazılabilir.
    private static string? Option(ImportField field, string text)
    {
        var name = ImportColumns.Name(text);
        return field.Options.FirstOrDefault(o => ImportColumns.Name(o.Value) == name || ImportColumns.Name(o.Label) == name)?.Value;
    }

    /// <summary>Türkçe biçim: nokta binlik, virgül ondalıktır. "12.5" gibi yanlış yorumlanabilecek yazım reddedilir (Ç7.1).</summary>
    private static bool TryDecimal(string text, out decimal value)
    {
        value = 0;
        return TurkishNumber().IsMatch(text)
            && decimal.TryParse(text.Replace(".", string.Empty, StringComparison.Ordinal).Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out value);
    }

    private static void Assign(JsonObject values, string key, JsonNode? value)
    {
        if (!IsCustom(key))
        {
            values[key] = value;
            return;
        }

        if (values[RecordFields.Custom] is not JsonObject custom)
        {
            values[RecordFields.Custom] = custom = [];
        }

        custom[key[RecordFields.CustomPrefix.Length..]] = value;
    }

    private static bool IsCustom(string key) => key.StartsWith(RecordFields.CustomPrefix, StringComparison.Ordinal);

    private static ImportRowProblem Error(string? column, string code, string message) => new(column, code, ImportIssueSeverity.Error, message);

    [GeneratedRegex(@"^-?(\d{1,3}(\.\d{3})+|\d+)(,\d+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex TurkishNumber();
}
