namespace Incursa.Integrations.WorkOS.Tests;

using Incursa.Integrations.WorkOS.Abstractions.Widgets;
using Incursa.Integrations.WorkOS.AspNetCore.DependencyInjection;
using Incursa.Integrations.WorkOS.AspNetCore.Widgets.Services;
using Microsoft.Extensions.DependencyInjection;

[TestClass]
public sealed class WorkOsWidgetScopeProviderTests
{
    [TestMethod]
    public void UserSessions_UsesUsersTableManageScope()
    {
        using var services = new ServiceCollection()
            .AddLogging()
            .AddWorkOsWidgets(options => options.ApiKey = "sk_test")
            .BuildServiceProvider();

        var provider = services.GetRequiredService<IWorkOsWidgetScopeProvider>();

        CollectionAssert.AreEqual(
            new[] { "widgets:users-table:manage" },
            provider.GetScopes(WorkOsWidgetType.UserSessions).ToArray());
    }

    [TestMethod]
    public void UserSessions_ConfiguredScopeOverridesDefault()
    {
        using var services = new ServiceCollection()
            .AddLogging()
            .AddWorkOsWidgets(options =>
            {
                options.ApiKey = "sk_test";
                options.WidgetScopes[WorkOsWidgetType.UserSessions] = ["custom:scope"];
            })
            .BuildServiceProvider();

        var provider = services.GetRequiredService<IWorkOsWidgetScopeProvider>();

        CollectionAssert.AreEqual(
            new[] { "custom:scope" },
            provider.GetScopes(WorkOsWidgetType.UserSessions).ToArray());
    }
}
