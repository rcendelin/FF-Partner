namespace Bridge.Api.Sagas;

public enum SagaOutcome
{
    /// <summary>Všechny kroky proběhly úspěšně.</summary>
    Success,

    /// <summary>INSERT do cílové DB selhal — žádné změny neproběhly, klient zůstal v původním regionu.</summary>
    FailedAtStep1_NoChanges,

    /// <summary>DISABLE v původní DB selhal — INSERT v cílové byl zkompenzován (DELETE).</summary>
    CompensatedAtStep3,

    /// <summary>UpdateMappingAsync selhal — INSERT v cílové byl zkompenzován (DELETE), klient v původní re-aktivován.</summary>
    CompensatedAtStep4,

    /// <summary>Kompenzace samotná selhala — nutný manuální zásah.</summary>
    CompensationFailed,
}

public sealed class SagaResult
{
    public required SagaOutcome Outcome { get; init; }
    public string? ErrorMessage { get; init; }

    public bool IsSuccess => Outcome == SagaOutcome.Success;

    /// <summary>Saga neprovedla žádné trvalé změny — lze pokračovat s fallback UPDATE v původním regionu.</summary>
    public bool HasNoSideEffects => Outcome == SagaOutcome.FailedAtStep1_NoChanges;
}
