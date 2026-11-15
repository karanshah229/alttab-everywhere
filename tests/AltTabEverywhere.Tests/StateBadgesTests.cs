using System.Diagnostics;
using AltTabEverywhere.Interop;

namespace AltTabEverywhere.Tests;

public class StateBadgesTests
{
    private static WindowInfo Window(bool minimized = false, bool elevated = false, bool hung = false, bool otherDesktop = false) =>
        new(IntPtr.Zero, "title", null, IntPtr.Zero, minimized, null, otherDesktop, elevated, hung);

    [Fact]
    public void A_plain_window_has_no_badges() =>
        Assert.Empty(StateBadges.For(Window(), new Settings()));

    [Fact]
    public void Every_state_shows_by_default_most_important_first()
    {
        var badges = StateBadges.For(Window(minimized: true, elevated: true, hung: true, otherDesktop: true), new Settings());
        Assert.Equal(new[] { StateBadges.NotResponding, StateBadges.Admin, StateBadges.OtherDesktop, StateBadges.Minimized }, badges);
    }

    [Fact]
    public void Each_badge_can_be_turned_off()
    {
        var all = Window(minimized: true, elevated: true, hung: true, otherDesktop: true);
        Assert.DoesNotContain(StateBadges.Minimized, StateBadges.For(all, new Settings { MinimizedBadge = false }));
        Assert.DoesNotContain(StateBadges.Admin, StateBadges.For(all, new Settings { AdminBadge = false }));
        Assert.DoesNotContain(StateBadges.NotResponding, StateBadges.For(all, new Settings { NotRespondingBadge = false }));
        Assert.DoesNotContain(StateBadges.OtherDesktop, StateBadges.For(all, new Settings { OtherDesktopBadge = false }));
        Assert.Empty(StateBadges.For(all, new Settings
            { MinimizedBadge = false, AdminBadge = false, NotRespondingBadge = false, OtherDesktopBadge = false }));
    }

    [Fact]
    public void Badge_settings_load_from_json()
    {
        var settings = SettingsStore.Deserialize("""{ "minimizedBadge": false, "adminBadge": false }""");
        Assert.False(settings.MinimizedBadge);
        Assert.False(settings.AdminBadge);
        Assert.True(settings.NotRespondingBadge);
        Assert.True(settings.OtherDesktopBadge);
    }

    [Fact]
    public void This_process_is_elevated_exactly_when_the_test_runner_is() =>
        Assert.Equal(ProcessElevation.IsSelfElevated, ProcessElevation.IsElevated((uint)Environment.ProcessId));

    [Fact]
    public void A_process_that_has_exited_is_not_elevated() =>
        Assert.False(ProcessElevation.IsElevated(0xFFFFFFF0));

    [Fact]
    public void Explorer_does_not_run_as_administrator()
    {
        var explorer = Process.GetProcessesByName("explorer").FirstOrDefault();
        if (explorer is null) return; // no shell (e.g. a CI server)
        Assert.False(ProcessElevation.IsElevated((uint)explorer.Id) && !ProcessElevation.IsSelfElevated);
    }
}
