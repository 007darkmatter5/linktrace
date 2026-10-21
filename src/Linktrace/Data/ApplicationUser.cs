using Microsoft.AspNetCore.Identity;

namespace Linktrace.Data;

public class ApplicationUser : IdentityUser
{
    /// <summary>Light, dark, or follow the operating system. Saved per user so it sticks across restarts and browsers.</summary>
    public ThemePreference ThemePreference { get; set; } = ThemePreference.System;
}

public enum ThemePreference
{
    System,
    Light,
    Dark
}
