using Bridge.Domain.Exceptions;
using Bridge.Infrastructure.Partner;
using Xunit;

namespace Bridge.Tests;

public class RegionRouterTests
{
    [Theory]
    [InlineData("CZ", "cz")]
    [InlineData("SK", "cz")]
    [InlineData("UA", "cz")]
    [InlineData("AT", "cz")]
    [InlineData("FR", "cz")]
    [InlineData("PL", "pl")]
    [InlineData("LT", "pl")]
    [InlineData("LV", "pl")]
    [InlineData("EE", "pl")]
    [InlineData("HU", "hu")]
    [InlineData("RO", "hu")]
    [InlineData("US", "us")]
    [InlineData("CA", "us")]
    [InlineData("AU", "us")]
    [InlineData("BR", "us")]
    public void ResolveRegion_KnownCountry_ReturnsCorrectRegion(string countryCode, string expectedRegion)
    {
        var region = RegionRouter.ResolveRegion(countryCode);
        Assert.Equal(expectedRegion, region);
    }

    [Theory]
    [InlineData("IT")]
    [InlineData("GB")]
    [InlineData("ES")]
    [InlineData("NL")]
    [InlineData("CH")]
    [InlineData("BE")]
    [InlineData("XX")]
    public void ResolveRegion_UnsupportedCountry_ThrowsUnsupportedRegionException(string countryCode)
    {
        var ex = Assert.Throws<UnsupportedRegionException>(
            () => RegionRouter.ResolveRegion(countryCode));

        Assert.Equal(countryCode, ex.CountryCode);
    }

    [Fact]
    public void ResolveRegion_Germany_ThrowsInvalidOperationException()
    {
        // DE vyžaduje manuální konfiguraci — nemá automatický region
        Assert.Throws<InvalidOperationException>(
            () => RegionRouter.ResolveRegion("DE"));
    }
}
