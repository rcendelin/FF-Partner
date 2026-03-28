using Bridge.Application.Services;
using Xunit;

namespace Bridge.Tests;

public class OwnerMappingServiceTests
{
    private static IOwnerMappingService CreateService(
        Dictionary<string, int>? mappings = null,
        int? defaultOwnerId = null)
    {
        var options = new OwnerMappingOptions
        {
            Mappings = mappings ?? new(),
            DefaultOwnerId = defaultOwnerId
        };
        return new OwnerMappingService(options);
    }

    [Fact]
    public void ResolveOwnerId_NullUserId_ReturnsDefault()
    {
        var svc = CreateService(defaultOwnerId: 99);
        Assert.Equal(99, svc.ResolveOwnerId(null));
    }

    [Fact]
    public void ResolveOwnerId_NullUserId_NoDefault_ReturnsNull()
    {
        var svc = CreateService();
        Assert.Null(svc.ResolveOwnerId(null));
    }

    [Fact]
    public void ResolveOwnerId_KnownUserId_ReturnsMappedId()
    {
        var userId = Guid.NewGuid();
        var svc = CreateService(
            mappings: new() { [userId.ToString()] = 42 });

        Assert.Equal(42, svc.ResolveOwnerId(userId));
    }

    [Fact]
    public void ResolveOwnerId_UnknownUserId_ReturnsDefault()
    {
        var svc = CreateService(defaultOwnerId: 7);
        Assert.Equal(7, svc.ResolveOwnerId(Guid.NewGuid()));
    }

    [Fact]
    public void ResolveOwnerId_UnknownUserId_NoDefault_ReturnsNull()
    {
        var svc = CreateService();
        Assert.Null(svc.ResolveOwnerId(Guid.NewGuid()));
    }

    [Fact]
    public void ResolveOwnerId_InvalidGuidInConfig_IsSkipped()
    {
        // Neplatný Guid v konfiguraci nesmí způsobit crash — pouze se přeskočí
        var options = new OwnerMappingOptions
        {
            Mappings = new()
            {
                ["not-a-guid"] = 1,
                [Guid.NewGuid().ToString()] = 99
            }
        };
        var svc = new OwnerMappingService(options);

        // Nesmí vyhodit výjimku
        var result = svc.ResolveOwnerId(null);
        Assert.Null(result);  // no default
    }

    [Fact]
    public void ResolveOwnerId_MultipleMappings_PicksCorrect()
    {
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();

        var svc = CreateService(mappings: new()
        {
            [userA.ToString()] = 10,
            [userB.ToString()] = 20
        }, defaultOwnerId: 0);

        Assert.Equal(10, svc.ResolveOwnerId(userA));
        Assert.Equal(20, svc.ResolveOwnerId(userB));
        Assert.Equal(0, svc.ResolveOwnerId(Guid.NewGuid()));  // unknown → default
    }
}
