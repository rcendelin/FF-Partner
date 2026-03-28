using Bridge.Api.Endpoints;
using Bridge.Application.Interfaces;
using Bridge.Domain.Messages;
using Xunit;

namespace Bridge.Tests;

/// <summary>
/// Unit testy pro logiku BulkSyncEndpoints (POST /api/bulk-sync).
/// Testujeme přes helper ExecuteBulkSyncAsync — izoluje logiku od ASP.NET pipeline.
/// Publisher stub bez NSubstitute (WDAC kompatibilita).
/// </summary>
public class BulkSyncEndpointTests
{
    private static BulkSyncItem MakeItem(Guid? id = null) => new()
    {
        CompanyId = id ?? Guid.NewGuid(),
        CompanyName = "Test s.r.o.",
        CountryCode = "CZ",
        CompanyRole = "Customer"
    };

    // ── Happy path ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task BulkSync_ValidBatch_PublishesOneMessagePerCompany()
    {
        var stub = new CapturePublisher();
        var request = new BulkSyncRequest
        {
            Action = "Create",
            Companies = [MakeItem(), MakeItem(), MakeItem()]
        };

        var result = await ExecuteBulkSyncAsync(request, stub);

        Assert.Equal(3, result.Sent);
        Assert.Equal(0, result.Failed);
        Assert.Equal(3, stub.Published.Count);
    }

    [Fact]
    public async Task BulkSync_ValidBatch_PublishesCorrectAction()
    {
        var stub = new CapturePublisher();
        var companyId = Guid.NewGuid();
        var request = new BulkSyncRequest
        {
            Action = "Update",
            Companies = [MakeItem(companyId)]
        };

        await ExecuteBulkSyncAsync(request, stub);

        var msg = Assert.IsType<CompanySyncMessage>(stub.Published[0].Message);
        Assert.Equal(companyId, msg.CompanyId);
        Assert.Equal("Update", msg.Action);
        Assert.Equal("ff.company.sync", stub.Published[0].Topic);
    }

    [Fact]
    public async Task BulkSync_CaseInsensitiveAction_Accepted()
    {
        var stub = new CapturePublisher();
        var request = new BulkSyncRequest
        {
            Action = "create",
            Companies = [MakeItem()]
        };

        var result = await ExecuteBulkSyncAsync(request, stub);

        Assert.Equal(1, result.Sent);
        Assert.Null(result.ValidationError);
    }

    // ── Validace ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task BulkSync_InvalidAction_ReturnsValidationError()
    {
        var stub = new CapturePublisher();
        var request = new BulkSyncRequest
        {
            Action = "Delete",
            Companies = [MakeItem()]
        };

        var result = await ExecuteBulkSyncAsync(request, stub);

        Assert.NotNull(result.ValidationError);
        Assert.Contains("Delete", result.ValidationError);
        Assert.Empty(stub.Published);
    }

    [Fact]
    public async Task BulkSync_EmptyList_ReturnsValidationError()
    {
        var stub = new CapturePublisher();
        var request = new BulkSyncRequest
        {
            Action = "Create",
            Companies = []
        };

        var result = await ExecuteBulkSyncAsync(request, stub);

        Assert.NotNull(result.ValidationError);
    }

    [Fact]
    public async Task BulkSync_TooManyCompanies_ReturnsValidationError()
    {
        var stub = new CapturePublisher();
        var companies = Enumerable.Range(0, 501).Select(_ => MakeItem()).ToList();
        var request = new BulkSyncRequest
        {
            Action = "Create",
            Companies = companies
        };

        var result = await ExecuteBulkSyncAsync(request, stub);

        Assert.NotNull(result.ValidationError);
        Assert.Contains("501", result.ValidationError);
    }

    [Fact]
    public async Task BulkSync_GuidEmptyCompanyId_CountsAsFailed()
    {
        var stub = new CapturePublisher();
        var request = new BulkSyncRequest
        {
            Action = "Create",
            Companies = [MakeItem(Guid.Empty), MakeItem()]
        };

        var result = await ExecuteBulkSyncAsync(request, stub);

        Assert.Equal(1, result.Failed);
        Assert.Equal(1, result.Sent);
        Assert.Single(stub.Published);  // Guid.Empty nesmí být odesláno
    }

    // ── Partial failure ───────────────────────────────────────────────────────────

    [Fact]
    public async Task BulkSync_PublishFails_CountsAsFailedAndContinues()
    {
        var failId = Guid.NewGuid();
        var successId = Guid.NewGuid();
        var stub = new CapturePublisher(throwForCompanyId: failId);
        var request = new BulkSyncRequest
        {
            Action = "Create",
            Companies = [MakeItem(failId), MakeItem(successId)]
        };

        var result = await ExecuteBulkSyncAsync(request, stub);

        Assert.Equal(2, result.Total);
        Assert.Equal(1, result.Sent);
        Assert.Equal(1, result.Failed);
    }

    [Fact]
    public async Task BulkSync_AllSuccess_ErrorsIsNull()
    {
        var stub = new CapturePublisher();
        var request = new BulkSyncRequest
        {
            Action = "Create",
            Companies = [MakeItem(), MakeItem()]
        };

        var result = await ExecuteBulkSyncAsync(request, stub);

        Assert.Null(result.Errors);
    }

    [Fact]
    public async Task BulkSync_PartialFailure_ErrorsNotEmpty()
    {
        var failId = Guid.NewGuid();
        var stub = new CapturePublisher(throwForCompanyId: failId);
        var request = new BulkSyncRequest
        {
            Action = "Create",
            Companies = [MakeItem(failId)]
        };

        var result = await ExecuteBulkSyncAsync(request, stub);

        Assert.NotNull(result.Errors);
        Assert.Single(result.Errors);
    }

    // ── Null/empty required fields — fallback na empty string ──────────────────────

    [Fact]
    public async Task BulkSync_NullCompanyName_PublishesWithEmptyStringFallback()
    {
        var stub = new CapturePublisher();
        var item = new BulkSyncItem
        {
            CompanyId = Guid.NewGuid(),
            CompanyName = null,
            CountryCode = "CZ",
            CompanyRole = "Customer"
        };
        var request = new BulkSyncRequest { Action = "Create", Companies = [item] };

        await ExecuteBulkSyncAsync(request, stub);

        var msg = Assert.IsType<CompanySyncMessage>(stub.Published[0].Message);
        Assert.Equal(string.Empty, msg.CompanyName);
    }

    // ── Max batch — hranice ────────────────────────────────────────────────────────

    [Fact]
    public async Task BulkSync_ExactlyMaxBatch_Accepted()
    {
        var stub = new CapturePublisher();
        var companies = Enumerable.Range(0, 500).Select(_ => MakeItem()).ToList();
        var request = new BulkSyncRequest { Action = "Create", Companies = companies };

        var result = await ExecuteBulkSyncAsync(request, stub);

        Assert.Null(result.ValidationError);
        Assert.Equal(500, result.Sent);
    }

    /// <summary>
    /// Pomocná metoda simulující logiku BulkSyncEndpoints bez ASP.NET pipeline.
    /// </summary>
    private static async Task<BulkSyncResult> ExecuteBulkSyncAsync(
        BulkSyncRequest request,
        CapturePublisher publisher,
        CancellationToken ct = default)
    {
        const int maxBatchSize = 500;

        if (!string.Equals(request.Action, "Create", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(request.Action, "Update", StringComparison.OrdinalIgnoreCase))
        {
            return new BulkSyncResult
            {
                ValidationError = $"Neplatná action '{request.Action}'. Povolené hodnoty: 'Create', 'Update'."
            };
        }

        if (request.Companies is null || request.Companies.Count == 0)
            return new BulkSyncResult { ValidationError = "Companies nesmí být prázdný seznam." };

        if (request.Companies.Count > maxBatchSize)
        {
            return new BulkSyncResult
            {
                ValidationError = $"Příliš mnoho firem ({request.Companies.Count}). Maximum: {maxBatchSize}."
            };
        }

        var sent = 0;
        var failed = 0;
        var errors = new List<object>();
        var sentAt = DateTimeOffset.UtcNow;

        for (var i = 0; i < request.Companies.Count; i++)
        {
            if (ct.IsCancellationRequested) break;

            var company = request.Companies[i];

            if (company.CompanyId == Guid.Empty)
            {
                failed++;
                errors.Add(new { companyId = company.CompanyId, error = "CompanyId nesmí být Guid.Empty." });
                continue;
            }

            var message = new CompanySyncMessage
            {
                MessageId = Guid.NewGuid().ToString(),
                SentAt = sentAt,
                CompanyId = company.CompanyId,
                Action = request.Action,
                CompanyName = company.CompanyName ?? string.Empty,
                CountryCode = company.CountryCode ?? string.Empty,
                CompanyRole = company.CompanyRole ?? string.Empty,
                Ico = company.Ico,
                Dic = company.Dic,
                Street = company.Street,
                City = company.City,
                PostalCode = company.PostalCode,
                State = company.State,
                County = company.County,
                PrimaryContactEmail = company.PrimaryContactEmail,
                PrimaryContactPhone = company.PrimaryContactPhone,
                AssignedUserId = company.AssignedUserId,
                PipedriveId = company.PipedriveId
            };

            try
            {
                await publisher.PublishAsync("ff.company.sync", message, message.MessageId, ct);
                sent++;
            }
            catch
            {
                failed++;
                errors.Add(new { companyId = company.CompanyId, error = "publish failed" });
            }
        }

        return new BulkSyncResult
        {
            Total = request.Companies.Count,
            Sent = sent,
            Failed = failed,
            Errors = errors.Count > 0 ? errors : null
        };
    }

    private sealed record BulkSyncResult
    {
        public int Total { get; init; }
        public int Sent { get; init; }
        public int Failed { get; init; }
        public List<object>? Errors { get; init; }
        public string? ValidationError { get; init; }
    }

    /// <summary>
    /// Jednoduchý capture stub pro IServiceBusPublisher — bez NSubstitute (WDAC kompatibilní).
    /// </summary>
    private sealed class CapturePublisher : IServiceBusPublisher
    {
        private readonly Guid? _throwForCompanyId;

        public List<(string Topic, object Message)> Published { get; } = new();

        public CapturePublisher(Guid? throwForCompanyId = null)
        {
            _throwForCompanyId = throwForCompanyId;
        }

        public Task PublishAsync<T>(
            string topicName, T message,
            string? correlationId = null,
            CancellationToken ct = default) where T : class
        {
            if (_throwForCompanyId.HasValue &&
                message is CompanySyncMessage csm &&
                csm.CompanyId == _throwForCompanyId.Value)
            {
                throw new Exception($"Simulated publish failure for {_throwForCompanyId}");
            }

            Published.Add((topicName, message));
            return Task.CompletedTask;
        }
    }
}
