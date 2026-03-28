using Bridge.Api.Pollers;
using Bridge.Domain.Models;
using Xunit;

namespace Bridge.Tests;

/// <summary>
/// Unit testy pro OrderPollerBase logiku — MD5 hash computation.
/// Testujeme interní metodu ComputeStateHash přes internal access.
/// Bez NSubstitute, bez Service Bus — pouze čistá logika.
/// </summary>
public class OrderPollerLogicTests
{
    // ── ComputeStateHash ───────────────────────────────────────────────────────

    private static TblOrderRow MakeOrder(
        short state = 7, short close = 0, short closePay = 0,
        sbyte automatClose = -10, sbyte deactive = 0,
        long idOrder = 1, int idClient = 100) => new()
    {
        IdOrder = idOrder,
        IdClient = idClient,
        OrderDateStart = 1711539600,
        OrderState = state,
        OrderClose = close,
        OrderClosePay = closePay,
        OrderAutomatClose = automatClose,
        OrderDeactive = deactive
    };

    [Fact]
    public void ComputeStateHash_SameState_ReturnsSameHash()
    {
        var order = MakeOrder(state: 7, close: 0, closePay: 0, automatClose: -10, deactive: 0);
        var hash1 = OrderPollerBase.ComputeStateHash(order);
        var hash2 = OrderPollerBase.ComputeStateHash(order);

        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void ComputeStateHash_DifferentState_ReturnsDifferentHash()
    {
        var order1 = MakeOrder(state: 7);   // nová
        var order2 = MakeOrder(state: 20);  // v realizaci

        var hash1 = OrderPollerBase.ComputeStateHash(order1);
        var hash2 = OrderPollerBase.ComputeStateHash(order2);

        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void ComputeStateHash_DifferentClosePay_ReturnsDifferentHash()
    {
        var order1 = MakeOrder(closePay: 0);
        var order2 = MakeOrder(closePay: 1); // zaplaceno

        var hash1 = OrderPollerBase.ComputeStateHash(order1);
        var hash2 = OrderPollerBase.ComputeStateHash(order2);

        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void ComputeStateHash_CancelledOrder_ReturnsDifferentHashThanActive()
    {
        var active = MakeOrder(state: 20, close: 0, closePay: 0, deactive: 0);
        var cancelled = MakeOrder(state: 30, close: 0, closePay: 0, deactive: 0);

        var hashActive = OrderPollerBase.ComputeStateHash(active);
        var hashCancelled = OrderPollerBase.ComputeStateHash(cancelled);

        Assert.NotEqual(hashActive, hashCancelled);
    }

    [Fact]
    public void ComputeStateHash_ReturnsLowercase32CharHex()
    {
        var order = MakeOrder();
        var hash = OrderPollerBase.ComputeStateHash(order);

        Assert.Equal(32, hash.Length);
        Assert.True(hash.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')),
            $"Hash '{hash}' obsahuje neplatné znaky (očekáván lowercase hex)");
    }

    [Fact]
    public void ComputeStateHash_GaiaAutomatClose_AffectsHash()
    {
        var waiting = MakeOrder(automatClose: -10);  // čeká
        var error = MakeOrder(automatClose: -1);     // chyba GAIA
        var done = MakeOrder(automatClose: 0);       // hotovo

        var hashWaiting = OrderPollerBase.ComputeStateHash(waiting);
        var hashError = OrderPollerBase.ComputeStateHash(error);
        var hashDone = OrderPollerBase.ComputeStateHash(done);

        Assert.NotEqual(hashWaiting, hashError);
        Assert.NotEqual(hashError, hashDone);
        Assert.NotEqual(hashWaiting, hashDone);
    }

    [Theory]
    [InlineData(7, 0, 0, -10, 0)]   // Nová objednávka
    [InlineData(20, 0, 0, -10, 0)]  // V realizaci
    [InlineData(20, 1, 0, 0, 0)]    // Uzavřeno
    [InlineData(20, 1, 1, 0, 0)]    // Zaplaceno
    [InlineData(30, 0, 0, -10, 0)]  // Zrušeno
    public void ComputeStateHash_AllKnownStates_ReturnsValid32CharHash(
        short state, short close, short closePay, sbyte automatClose, sbyte deactive)
    {
        var order = MakeOrder(state, close, closePay, automatClose, deactive);
        var hash = OrderPollerBase.ComputeStateHash(order);

        Assert.Equal(32, hash.Length);
        Assert.False(string.IsNullOrEmpty(hash));
    }

    [Fact]
    public void ComputeStateHash_SameValuesRegardlessOfOtherFields_SameHash()
    {
        // Hash závisí POUZE na 5 stavových polích — ostatní (VIN, cena atd.) nemají vliv
        var order1 = new TblOrderRow
        {
            IdOrder = 1, IdClient = 100, OrderDateStart = 1000,
            OrderState = 7, OrderClose = 0, OrderClosePay = 0,
            OrderAutomatClose = -10, OrderDeactive = 0,
            OrderPrice = 10000, OrderCarVin = "ABC123", OrderCarMark = "BMW"
        };
        var order2 = new TblOrderRow
        {
            IdOrder = 9999, IdClient = 9999, OrderDateStart = 9999,
            OrderState = 7, OrderClose = 0, OrderClosePay = 0,
            OrderAutomatClose = -10, OrderDeactive = 0,
            OrderPrice = null, OrderCarVin = null, OrderCarMark = null
        };

        var hash1 = OrderPollerBase.ComputeStateHash(order1);
        var hash2 = OrderPollerBase.ComputeStateHash(order2);

        Assert.Equal(hash1, hash2); // Pouze stavová pole ovlivňují hash
    }
}
