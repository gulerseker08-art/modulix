using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Crm.Domain;

namespace Modulix.Modules.Crm.Records;

/// <summary>
/// Lead formu kayıt deposu (<c>crm.lead-forms.manage</c>). Form kimliği oluşturmada üretilir ve değişmez; entegrasyon sırrının özeti
/// yazma-only'dir (hiçbir yanıtta, audit'te ya da olayda görünmez), yalnızca <c>LeadFormAdmin</c> sır yenilerken iş akışı kapısı
/// açıkken yazılır ve yalnızca "özet eşleşiyor mu" süzgeciyle sorgulanabilir.
/// </summary>
internal sealed class LeadFormRecordStore(CrmStoreContext context) : CrmRecordStore<LeadForm>(context)
{
    private const string KeyAlphabet = "abcdefghjkmnpqrstuvwxyz23456789";

    public override string EntityKey => CrmModule.Entities.LeadForm;

    protected override string ReadPermission => CrmModule.Permissions.LeadFormsManage;

    protected override string WritePermission => CrmModule.Permissions.LeadFormsManage;

    protected override IReadOnlyList<RecordField<LeadForm>> Fields { get; } =
    [
        Field.Text<LeadForm>("name", LeadForm.NameMaxLength, f => f.Name, (f, v) => f.Name = v!, required: true),
        Field.Text<LeadForm>("formKey", LeadForm.KeyLength, f => f.FormKey, (f, v) => f.FormKey = v!).AsSystem(),
        Field.Flag<LeadForm>("isActive", f => f.IsActive, (f, v) => f.IsActive = v),
        Field.Owner<LeadForm>("defaultOwnerId", f => f.DefaultOwnerId, (f, v) => f.DefaultOwnerId = v),
        Field.Text<LeadForm>("allowedOrigins", LeadForm.OriginsMaxLength, f => f.AllowedOrigins, (f, v) => f.AllowedOrigins = v),
        Field.Flag<LeadForm>("serverOnly", f => f.ServerOnly, (f, v) => f.ServerOnly = v),
        Field.Text<LeadForm>("secretHash", 64, f => f.SecretHash, (f, v) => f.SecretHash = v).AsSystem().AsWriteOnly(),
        Field.Time<LeadForm>("secretRotatedAt", f => f.SecretRotatedAt, (f, v) => f.SecretRotatedAt = v).AsSystem(),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<LeadForm>> Filters { get; } = new Dictionary<string, RecordFilter<LeadForm>>
    {
        ["formKey"] = (q, v, _) => v.Trim() is { Length: > 0 and <= LeadForm.KeyLength } key ? q.Where(f => f.FormKey == key) : null,
        ["active"] = (q, v, _) => FilterValue.Bool(v) is { } active ? q.Where(f => f.IsActive == active) : null,
        // Sır doğrulaması: sunulan sırrın özeti kayıtlı özetle eşleşiyor mu. Özet hiçbir zaman okunmaz.
        ["secretHash"] = (q, v, _) => v.Length == 64 && v.All(char.IsAsciiHexDigit) ? q.Where(f => f.SecretHash == v) : null,
    };

    protected override bool UsesScope => false;

    protected override IOrderedQueryable<LeadForm> Order(IQueryable<LeadForm> source) => source.OrderBy(f => f.Name);

    protected override IQueryable<LeadForm> Search(IQueryable<LeadForm> source, string term) => source.Where(f => f.Name.Contains(term));

    public override IReadOnlyList<RecordFieldError> Validate(RecordOperation operation, JsonObject values)
    {
        var errors = base.Validate(operation, values).ToList();
        if (errors.All(e => e.Field != "allowedOrigins")
            && values["allowedOrigins"] is JsonValue value && value.TryGetValue<string>(out var origins)
            && LeadFormOrigins.Parse(origins) is null)
        {
            errors.Add(new RecordFieldError("allowedOrigins", "Her kaynak https://alanadi[:port] biçiminde olmalı (yol, joker ya da boşluk içermez); virgül ya da satırla ayırın."));
        }

        return errors;
    }

    protected override Task<(JsonObject Values, bool Linked)> PrepareNewAsync(LeadForm entity, JsonObject values, CancellationToken cancellationToken)
    {
        Span<char> key = stackalloc char[LeadForm.KeyLength];
        for (var i = 0; i < key.Length; i++)
        {
            key[i] = KeyAlphabet[RandomNumberGenerator.GetInt32(KeyAlphabet.Length)];
        }

        entity.FormKey = new string(key);
        return Task.FromResult((values, false));
    }
}

/// <summary>İzinli kaynak listesi ayrıştırma (kimlik doğrulama değil, yalnızca tarayıcı kaynağı denetimidir).</summary>
internal static class LeadFormOrigins
{
    /// <summary>Geçerli kaynaklar (küçük harf, yolsuz); biçim bozuksa null. Boş metin boş liste verir.</summary>
    public static IReadOnlyList<string>? Parse(string? text)
    {
        var result = new List<string>();
        foreach (var token in (text ?? string.Empty).Split([',', '\n', '\r', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (token.Contains('*', StringComparison.Ordinal)
                || !Uri.TryCreate(token, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("https" or "http")
                || uri.PathAndQuery != "/"
                || !string.IsNullOrEmpty(uri.UserInfo)
                || token.TrimEnd('/').Count(c => c == '/') != 2
                || !string.IsNullOrEmpty(uri.Fragment))
            {
                return null;
            }

            result.Add($"{uri.Scheme}://{uri.Authority}".ToLowerInvariant());
        }

        return result;
    }
}
