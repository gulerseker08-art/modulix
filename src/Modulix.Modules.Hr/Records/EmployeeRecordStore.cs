using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.SharedRecords;
using Modulix.Modules.Hr.Domain;

namespace Modulix.Modules.Hr.Records;

/// <summary>
/// Çalışan kartları (ADR-0042 K2, dilim 1). Kişi yalnızca oluştururken seçilir ve bir kişinin tek kartı olur; ad, iş e-postası ve iş telefonu
/// kişi kaydından okunur (yalnızca okunur hesaplanan alanlar: rehber ve organizasyon şeması tek istekle çizilir). Sicil numarası, bağlı
/// kullanıcı ve T.C. kimlik numarası verilirse tenant içinde benzersizdir. Yönetici başka bir çalışandır, kendisi ya da astı olamaz.
/// "Ayrıldı" durumunda ayrılış tarihi zorunludur ve işe giriş tarihinden önce olamaz. Kart silinmez (K9).
/// </summary>
internal sealed partial class EmployeeRecordStore(HrStoreContext context) : HrRecordStore<Employee>(context), IRecordLabelProvider
{
    /// <summary>Yönetici zincirinde en çok bu kadar basamak izlenir (döngü denetimi; gerçek şemalarda derinlik çok daha azdır).</summary>
    private const int MaxChainDepth = 100;

    private readonly Dictionary<Guid, PersonInfo> people = [];

    public override string EntityKey => HrModule.Entities.Employee;

    protected override string ReadPermission => HrModule.Permissions.EmployeesRead;

    protected override string WritePermission => HrModule.Permissions.EmployeesWrite;

    /// <summary>Çalışanı başka listelerde (süreli belge, izin, zimmet) kişinin adı soyadıyla tanıtır.</summary>
    public async Task<IReadOnlyDictionary<Guid, string>> LabelsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        Guid[] wanted = [.. ids];
        return await Context.Data.Set<Employee>().AsNoTracking().Where(e => wanted.Contains(e.Id))
            .Join(People, e => e.PersonId, p => p.Id, (e, p) => new { e.Id, Name = p.FirstName + " " + p.LastName })
            .ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken);
    }

    protected override IReadOnlyList<RecordField<Employee>> Fields => fields ??=
    [
        Field.PersonRef<Employee>("personId", "Kişi bulunamadı.", e => e.PersonId, (e, v) => e.PersonId = v!.Value, required: true, createOnly: true),
        Field.Text<Employee>("employeeNumber", Employee.EmployeeNumberMaxLength, e => e.EmployeeNumber, (e, v) => e.EmployeeNumber = v, upperCase: true),
        Field.Member<Employee>("userId", e => e.UserId, (e, v) => e.UserId = v),
        Field.Text<Employee>("jobTitle", Employee.JobTitleMaxLength, e => e.JobTitle, (e, v) => e.JobTitle = v),
        Field.ListChoice<Employee>("departmentKey", HrModule.Lists.Department, e => e.DepartmentKey, (e, v) => e.DepartmentKey = v, required: false),
        Field.Reference<Employee, Employee>("managerEmployeeId", "Yönetici bulunamadı.", e => e.ManagerEmployeeId, (e, v) => e.ManagerEmployeeId = v),
        Field.Choice<Employee>("employmentType", Employee.EmploymentTypes.All, e => e.EmploymentType, (e, v) => e.EmploymentType = v),
        Field.Text<Employee>("workLocation", Employee.WorkLocationMaxLength, e => e.WorkLocation, (e, v) => e.WorkLocation = v),
        Field.Date<Employee>("hireDate", e => e.HireDate, (e, v) => e.HireDate = v!.Value, required: true),
        Field.Date<Employee>("terminationDate", e => e.TerminationDate, (e, v) => e.TerminationDate = v),
        Field.Choice<Employee>("status", Employee.Statuses.All, e => e.Status, (e, v) => e.Status = v),
        Field.Text<Employee>("nationalId", Employee.NationalIdLength, e => e.NationalId, (e, v) => e.NationalId = v),
        Field.Date<Employee>("birthDate", e => e.BirthDate, (e, v) => e.BirthDate = v),
        Field.Text<Employee>("homeAddress", Employee.AddressMaxLength, e => e.HomeAddress, (e, v) => e.HomeAddress = v),
        Field.Text<Employee>("emergencyContactName", Employee.EmergencyNameMaxLength, e => e.EmergencyContactName, (e, v) => e.EmergencyContactName = v),
        Field.Text<Employee>("emergencyContactPhone", Employee.PhoneMaxLength, e => e.EmergencyContactPhone, (e, v) => e.EmergencyContactPhone = v),
        Field.Text<Employee>("iban", 34, e => e.Iban, (e, v) => e.Iban = v, upperCase: true),
        Field.Text<Employee>("notes", Employee.NotesMaxLength, e => e.Notes, (e, v) => e.Notes = v),

        // Kişi kaydından okunur (PrepareAsync ile toplu yüklenir); genel uçtan gelen değer yok sayılır. Kişi kaydına buradan yazılmaz.
        Field.Computed<Employee>("firstName", e => Person(e)?.FirstName),
        Field.Computed<Employee>("lastName", e => Person(e)?.LastName),
        Field.Computed<Employee>("fullName", e => Person(e) is { } p ? $"{p.FirstName} {p.LastName}".Trim() : null),
        Field.Computed<Employee>("email", e => Person(e)?.Email),
        Field.Computed<Employee>("phone", e => Person(e)?.Phone),
    ];

    private IReadOnlyList<RecordField<Employee>>? fields;

    protected override IReadOnlyDictionary<string, RecordFilter<Employee>> Filters { get; } = new Dictionary<string, RecordFilter<Employee>>
    {
        ["status"] = (q, v) => Employee.Statuses.All.Contains(v) ? q.Where(e => e.Status == v) : null,
        ["employmentType"] = (q, v) => Employee.EmploymentTypes.All.Contains(v) ? q.Where(e => e.EmploymentType == v) : null,
        ["departmentKey"] = (q, v) => v.Length is > 0 and <= 100 ? q.Where(e => e.DepartmentKey == v) : null,
        ["managerEmployeeId"] = (q, v) => FilterValue.Id(v) is { } id ? q.Where(e => e.ManagerEmployeeId == id) : null,
        ["personId"] = (q, v) => FilterValue.Id(v) is { } id ? q.Where(e => e.PersonId == id) : null,
        ["userId"] = (q, v) => FilterValue.Id(v) is { } id ? q.Where(e => e.UserId == id) : null,

        // "Çalışanlar" ve rehber görünümü: ayrılmamış (etkin ya da izinde) çalışanlar.
        ["current"] = (q, v) => v switch { "true" => q.Where(e => e.Status != Employee.Statuses.Terminated), "false" => q, _ => null },
    };

    private IQueryable<Person> People => Context.Data.Set<Person>().AsNoTracking();

    protected override IOrderedQueryable<Employee> Order(IQueryable<Employee> source) =>
        source.OrderBy(e => People.Where(p => p.Id == e.PersonId).Select(p => p.FirstName).FirstOrDefault())
            .ThenBy(e => People.Where(p => p.Id == e.PersonId).Select(p => p.LastName).FirstOrDefault());

    // Başlığa göre sıralama: ad soyad, sicil no, unvan, departman, işe giriş, durum, kayıt zamanı; eşitler ada göre.
    protected override IReadOnlyCollection<string>? SortKeys { get; } =
        WithDescending("name", "employeeNumber", "jobTitle", "departmentKey", "hireDate", "status", "createdAt");

    protected override IOrderedQueryable<Employee> Order(IQueryable<Employee> source, string? sort)
    {
        var desc = sort?.StartsWith('-') == true;
        return sort?.TrimStart('-') switch
        {
            "name" => By(source, e => People.Where(p => p.Id == e.PersonId).Select(p => p.FirstName + " " + p.LastName).FirstOrDefault(), desc),
            "employeeNumber" => ByNullsLast(source, e => e.EmployeeNumber, desc).ThenBy(e => e.CreatedAt),
            "jobTitle" => ByNullsLast(source, e => e.JobTitle, desc).ThenBy(e => e.CreatedAt),
            "departmentKey" => ByNullsLast(source, e => e.DepartmentKey, desc).ThenBy(e => e.CreatedAt),
            "hireDate" => By(source, e => e.HireDate, desc).ThenBy(e => e.CreatedAt),
            "status" => By(source, e => e.Status, desc).ThenBy(e => e.CreatedAt),
            "createdAt" => By(source, e => e.CreatedAt, desc),
            _ => Order(source),
        };
    }

    /// <summary>Ad, soyad, iş e-postası, iş telefonu, sicil numarası ve unvanla arar (hassas alanlarla aranmaz).</summary>
    protected override IQueryable<Employee> Search(IQueryable<Employee> source, string term)
    {
        var matches = People.Where(p => p.FirstName.Contains(term) || p.LastName.Contains(term) || (p.FirstName + " " + p.LastName).Contains(term)
            || (p.Phone != null && p.Phone.Contains(term)) || (p.Email != null && p.Email.Contains(term))).Select(p => p.Id);
        var number = term.Trim().ToUpperInvariant();
        return source.Where(e => matches.Contains(e.PersonId) || (e.EmployeeNumber != null && e.EmployeeNumber.Contains(number))
            || (e.JobTitle != null && e.JobTitle.Contains(term)));
    }

    protected override async Task PrepareAsync(IReadOnlyCollection<Employee> entities, CancellationToken cancellationToken)
    {
        Guid[] missing = [.. entities.Select(e => e.PersonId).Where(id => id != Guid.Empty && !people.ContainsKey(id)).Distinct()];
        if (missing.Length == 0)
        {
            return;
        }

        foreach (var person in await People.Where(p => missing.Contains(p.Id))
                     .Select(p => new PersonInfo(p.Id, p.FirstName, p.LastName, p.Email, p.Phone)).ToListAsync(cancellationToken))
        {
            people[person.Id] = person;
        }
    }

    protected override async Task AppliedAsync(Employee entity, JsonObject values, bool isNew, CancellationToken cancellationToken)
    {
        var errors = new List<RecordFieldError>();
        var employees = Context.Data.Set<Employee>().AsNoTracking();

        if (isNew && await employees.AnyAsync(e => e.PersonId == entity.PersonId && e.Id != entity.Id, cancellationToken))
        {
            errors.Add(new RecordFieldError("personId", "Bu kişinin zaten bir çalışan kartı var."));
        }

        if (entity.EmployeeNumber is { } number && await employees.AnyAsync(e => e.EmployeeNumber == number && e.Id != entity.Id, cancellationToken))
        {
            errors.Add(new RecordFieldError("employeeNumber", "Bu sicil numarası başka bir çalışanda kayıtlı."));
        }

        if (entity.UserId is { } user && await employees.AnyAsync(e => e.UserId == user && e.Id != entity.Id, cancellationToken))
        {
            errors.Add(new RecordFieldError("userId", "Bu kullanıcı başka bir çalışan kartına bağlı."));
        }

        if (entity.NationalId is { } nationalId)
        {
            if (!IsValidNationalId(nationalId))
            {
                errors.Add(new RecordFieldError("nationalId", "T.C. kimlik numarası 11 haneli olmalı ve denetim haneleri tutmalıdır."));
            }
            else if (await employees.AnyAsync(e => e.NationalId == nationalId && e.Id != entity.Id, cancellationToken))
            {
                errors.Add(new RecordFieldError("nationalId", "Bu T.C. kimlik numarası başka bir çalışanda kayıtlı."));
            }
        }

        if (entity.Iban is { } iban)
        {
            entity.Iban = string.Concat(iban.Where(c => !char.IsWhiteSpace(c)));
            if (!IsValidIban(entity.Iban))
            {
                errors.Add(new RecordFieldError("iban", "IBAN TR ile başlayan 26 karakter olmalı ve denetim haneleri tutmalıdır."));
            }
        }

        if (entity.Status == Employee.Statuses.Terminated && entity.TerminationDate is null)
        {
            errors.Add(new RecordFieldError("terminationDate", "\"Ayrıldı\" durumundaki çalışanda ayrılış tarihi girilmelidir."));
        }

        if (entity.TerminationDate is { } left && left < entity.HireDate)
        {
            errors.Add(new RecordFieldError("terminationDate", "Ayrılış tarihi işe giriş tarihinden önce olamaz."));
        }

        if (entity.BirthDate is { } born && born >= entity.HireDate)
        {
            errors.Add(new RecordFieldError("birthDate", "Doğum tarihi işe giriş tarihinden önce olmalıdır."));
        }

        if (entity.ManagerEmployeeId is { } manager && (isNew || values.ContainsKey("managerEmployeeId")))
        {
            if (manager == entity.Id)
            {
                errors.Add(new RecordFieldError("managerEmployeeId", "Çalışan kendi yöneticisi olamaz."));
            }
            else if (await ReportsToAsync(manager, entity.Id, cancellationToken))
            {
                errors.Add(new RecordFieldError("managerEmployeeId", "Seçilen yönetici bu çalışanın astı; organizasyon şemasında döngü oluşur."));
            }
        }

        if (errors.Count > 0)
        {
            throw new RecordValidationException(errors);
        }
    }

    protected override Task BeforeDeleteAsync(Employee entity, CancellationToken cancellationToken) =>
        throw new RecordConflictException("Çalışan kartları silinmez; ayrılan çalışanın durumunu \"Ayrıldı\" yapın ve ayrılış tarihini girin.");

    /// <summary>T.C. kimlik numarası: 11 hane, ilk hane 0 değil; 10. hane = ((tek sıradakiler × 7) − çift sıradakiler) mod 10, 11. hane = ilk 10'un toplamı mod 10.</summary>
    internal static bool IsValidNationalId(string value)
    {
        if (!NationalIdPattern().IsMatch(value))
        {
            return false;
        }

        var d = value.Select(c => c - '0').ToArray();
        var odd = d[0] + d[2] + d[4] + d[6] + d[8];
        var even = d[1] + d[3] + d[5] + d[7];
        var tenth = (((odd * 7) - even) % 10 + 10) % 10;
        return d[9] == tenth && d[10] == d.Take(10).Sum() % 10;
    }

    /// <summary>Türkiye IBAN'ı: "TR" + 24 karakter (26), ISO 13616 mod 97 denetimi.</summary>
    internal static bool IsValidIban(string value)
    {
        if (!IbanPattern().IsMatch(value))
        {
            return false;
        }

        var rearranged = value[4..] + value[..4];
        var digits = string.Concat(rearranged.Select(c => char.IsLetter(c) ? (c - 'A' + 10).ToString(CultureInfo.InvariantCulture) : c.ToString()));
        return BigInteger.Parse(digits, CultureInfo.InvariantCulture) % 97 == 1;
    }

    /// <summary><paramref name="employeeId"/>, yönetici zincirinde (üste doğru) <paramref name="ancestorId"/>'ye ulaşıyor mu?</summary>
    private async Task<bool> ReportsToAsync(Guid employeeId, Guid ancestorId, CancellationToken cancellationToken)
    {
        Guid? current = employeeId;
        var seen = new HashSet<Guid>();
        for (var depth = 0; current is { } id && depth < MaxChainDepth && seen.Add(id); depth++)
        {
            if (id == ancestorId)
            {
                return true;
            }

            current = await Context.Data.Set<Employee>().AsNoTracking().Where(e => e.Id == id).Select(e => e.ManagerEmployeeId).SingleOrDefaultAsync(cancellationToken);
        }

        return false;
    }

    private PersonInfo? Person(Employee employee) => people.GetValueOrDefault(employee.PersonId);

    [GeneratedRegex("^[1-9][0-9]{10}$")]
    private static partial Regex NationalIdPattern();

    [GeneratedRegex("^TR[0-9]{24}$")]
    private static partial Regex IbanPattern();

    private sealed record PersonInfo(Guid Id, string FirstName, string LastName, string? Email, string? Phone);
}
