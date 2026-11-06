using Modulix.Core.Contracts.Customization;
using Modulix.Core.Contracts.Modules;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Customization;
using Modulix.Core.Records;

namespace Modulix.Core.Imports;

/// <summary>
/// Kullanıcının bu firmada görebileceği içe aktarma sütunları: tanımın alanları, firmanın etkin özel alanları ve firmanın seçim listeleri.
/// Görme yetkisi olmayan hassas alan şablona girmez; dosyada varsa tanınmayan sütun gibi yok sayılır (ADR-0033 bölüm 7).
/// </summary>
public sealed class ImportSchema(ICustomFieldProvider customFields, IFieldAccessService fieldAccess, IChoiceLists choiceLists)
{
    public async Task<IReadOnlyList<ImportField>> FieldsAsync(TenantContext tenant, ImportDefinition definition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var hidden = await fieldAccess.GetHiddenFieldsAsync(tenant, definition.EntityKey, cancellationToken);
        var fields = new List<ImportField>();
        foreach (var field in definition.Fields.Where(f => !hidden.Contains(f.Key)))
        {
            fields.Add(field.ChoiceListKey is { } listKey
                ? field with { Options = [.. (await choiceLists.GetAsync(listKey, cancellationToken)).Values.Where(v => v.Active).Select(v => new SettingOption(v.Key, v.Label))] }
                : field);
        }

        foreach (var custom in await customFields.GetFieldsAsync(tenant, definition.EntityKey, cancellationToken))
        {
            fields.Add(new ImportField(RecordFields.CustomPrefix + custom.Key, custom.Label, TypeOf(custom.Type), custom.Required)
            {
                Options = [.. custom.Options.Where(o => o.Active).Select(o => new SettingOption(o.Key, o.Label))],
            });
        }

        return fields;
    }

    private static ImportFieldType TypeOf(CustomFieldType type) => type switch
    {
        CustomFieldType.Number => ImportFieldType.Decimal,
        CustomFieldType.Date => ImportFieldType.Date,
        CustomFieldType.YesNo => ImportFieldType.YesNo,
        CustomFieldType.Choice => ImportFieldType.Choice,
        CustomFieldType.MultiChoice => ImportFieldType.MultiChoice,
        _ => ImportFieldType.Text,
    };
}
