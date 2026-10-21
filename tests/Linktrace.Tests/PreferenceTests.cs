using System.Security.Claims;
using Linktrace.Data;
using Linktrace.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Linktrace.Tests;

public class PreferenceTests
{
    private sealed class FixedAuth(ClaimsPrincipal user) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(user));
    }

    [Fact]
    public async Task Theme_choice_is_saved_on_the_account()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ApplicationDbContext>(o => o.UseSqlite(conn));
        services.AddIdentityCore<ApplicationUser>().AddEntityFrameworkStores<ApplicationDbContext>();
        await using var root = services.BuildServiceProvider();
        using (var s = root.CreateScope()) await s.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.EnsureCreatedAsync();

        var user = new ApplicationUser { UserName = "me@example.com", Email = "me@example.com" };
        using (var s = root.CreateScope()) await s.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().CreateAsync(user);

        var signedIn = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.Id)], "test"));
        var scopes = root.GetRequiredService<IServiceScopeFactory>();

        var session1 = new UserPreferenceService(scopes, new FixedAuth(signedIn));
        Assert.Equal(ThemePreference.System, await session1.GetThemeAsync());
        await session1.SetThemeAsync(ThemePreference.Dark);

        // A later session (e.g. after a restart) reads it back from the database.
        var session2 = new UserPreferenceService(scopes, new FixedAuth(signedIn));
        Assert.Equal(ThemePreference.Dark, await session2.GetThemeAsync());

        // Signed-out visitors get the default and can't change anything.
        var anonymous = new UserPreferenceService(scopes, new FixedAuth(new ClaimsPrincipal(new ClaimsIdentity())));
        Assert.Equal(ThemePreference.System, await anonymous.GetThemeAsync());
        await anonymous.SetThemeAsync(ThemePreference.Light);
        Assert.Equal(ThemePreference.Dark, await session2.GetThemeAsync());
    }
}
