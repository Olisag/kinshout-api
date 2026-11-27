using Kinshout.Api.Services;

namespace Kinshout.Api.Tests;

public class DiscoveryCatalogueTests
{
    private static readonly DateTime Now = new(2026, 11, 27, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void SelectProfiles_PutsANeverCheckedAccountAheadOfTheOldWatchlist()
    {
        var sources = new[]
        {
            new ProfileCandidate("werrasonofficiel", 4, 9, Now.AddHours(-2), true),
            new ProfileCandidate("gims_off", 3, 9, Now.AddHours(-2), true),
            new ProfileCandidate("deguempro1", 1, 9, Now.AddHours(-2), true),
            new ProfileCandidate("tiakola_meloo_", 0, 9, Now.AddHours(-2), true),
            new ProfileCandidate("borisbeckertv", 0, 9, Now.AddHours(-2), true),
            new ProfileCandidate("newcreator", 0, 0, default, true),
        };

        var chosen = DiscoveryImportGuard.SelectProfiles(sources, Now, 4);

        Assert.Equal("newcreator", chosen[0]);
        Assert.DoesNotContain("tiakola_meloo_", chosen);
        Assert.DoesNotContain("borisbeckertv", chosen);
    }

    [Fact]
    public void SelectProfiles_LetsAQuietAccountThatHasWaitedPassAFreshProductiveOne()
    {
        var sources = new[]
        {
            new ProfileCandidate("productive", 6, 4, Now.AddHours(-2), true),
            new ProfileCandidate("quiet", 0, 1, Now.AddDays(-10), true),
        };

        var chosen = DiscoveryImportGuard.SelectProfiles(sources, Now, 1);

        Assert.Equal("quiet", Assert.Single(chosen));
    }

    [Fact]
    public void SelectProfiles_IgnoresAnUnverifiedName()
    {
        var sources = new[]
        {
            new ProfileCandidate("only-a-name", 0, 0, default, false),
        };

        Assert.Empty(DiscoveryImportGuard.SelectProfiles(sources, Now, 4));
    }
}
