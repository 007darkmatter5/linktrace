using System.Security.Claims;
using Linktrace.Data;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;

namespace Linktrace.Services;

/// <summary>Per-user settings stored on the account. Anonymous visitors get the defaults.</summary>
public class UserPreferenceService(IServiceScopeFactory scopes, AuthenticationStateProvider authProvider)
{
    private async Task<string?> UserIdAsync()
    {
        var state = await authProvider.GetAuthenticationStateAsync();
        return state.User.FindFirstValue(ClaimTypes.NameIdentifier);
    }

    public async Task<ThemePreference> GetThemeAsync()
    {
        if (await UserIdAsync() is not { } id) return ThemePreference.System;
        // A fresh scope so the long-lived circuit never reads a stale user record.
        using var scope = scopes.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        return (await users.FindByIdAsync(id))?.ThemePreference ?? ThemePreference.System;
    }

    public async Task SetThemeAsync(ThemePreference theme)
    {
        if (await UserIdAsync() is not { } id) return;
        using var scope = scopes.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        if (await users.FindByIdAsync(id) is not { } user || user.ThemePreference == theme) return;
        user.ThemePreference = theme;
        await users.UpdateAsync(user);
    }
}
