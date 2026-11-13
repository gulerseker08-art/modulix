using System.Reflection;
using Modulix.Modules.Logistics.Dashboard;
using Modulix.Shared.Logistics;

namespace Modulix.Modules.Logistics.Tests;

/// <summary>
/// ADR-0038 dilim 9a: pano ucu sunucu kaydını doğrudan JSON'a yazar, arayüz Shared DTO'suyla okur. İki tarafın alan adları ve iç içe yapısı aynı
/// olmalıdır; yoksa alan sessizce boş gelir. Panoyu açan izin kümesi her bölümün okuma iznini kapsar.
/// </summary>
public class LogisticsDashboardModuleTests
{
    [Fact]
    public void Server_record_and_client_dto_have_the_same_shape()
    {
        AssertSameShape(typeof(LogisticsDashboard), typeof(LogisticsDashboardDto));
    }

    [Fact]
    public void Dashboard_permission_set_covers_every_section()
    {
        Assert.Equal(
            [LogisticsModule.Permissions.RequestsRead, LogisticsModule.Permissions.ShipmentsRead, LogisticsModule.Permissions.TripsRead, LogisticsModule.Permissions.DriversRead],
            LogisticsAccess.DashboardAny);
    }

    private static void AssertSameShape(Type server, Type client)
    {
        var serverProps = Props(server).Where(p => p.Name != nameof(LogisticsDashboard.IsEmpty)).ToList();
        var clientProps = Props(client);
        Assert.Equal(serverProps.Select(p => p.Name), clientProps.Select(p => p.Name));
        foreach (var (s, c) in serverProps.Zip(clientProps))
        {
            var (st, ct) = (Element(s.PropertyType), Element(c.PropertyType));
            if (st.Namespace == typeof(LogisticsDashboard).Namespace)
            {
                AssertSameShape(st, ct);
            }
            else
            {
                Assert.Equal(st, ct);
            }
        }
    }

    private static List<PropertyInfo> Props(Type type) =>
        [.. type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.Name != "EqualityContract").OrderBy(p => p.MetadataToken)];

    private static Type Element(Type type)
    {
        var inner = Nullable.GetUnderlyingType(type) ?? type;
        return inner.IsGenericType && inner.GetGenericTypeDefinition() == typeof(IReadOnlyList<>) ? inner.GetGenericArguments()[0] : inner;
    }
}
