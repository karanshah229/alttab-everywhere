namespace AltTabEverywhere.Tests;

/// <summary>
/// A test that needs a full Windows desktop (Store apps, a signed-in shell). Skipped on CI, where
/// GitHub Actions sets CI=true and runs a Windows Server image.
/// </summary>
public sealed class DesktopFactAttribute : FactAttribute
{
    public DesktopFactAttribute()
    {
        if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase))
            Skip = "Needs a full Windows desktop";
    }
}
