using Bridge.Domain.Enums;

namespace Bridge.Domain.Models;

/// <summary>
/// Doménový model mapující tbl_client v Partner3 DB.
/// Pouze sloupce relevantní pro Bridge — nemodifikovat pipe_id, pipeType, int_client.
/// </summary>
public sealed class PartnerClient
{
    // PK
    public int IdClient { get; set; }

    // Firma
    public string ClientFirm { get; set; } = string.Empty;
    public string? ClientIc { get; set; }
    public string? ClientDic { get; set; }

    // Adresa
    public string? ClientStreet { get; set; }
    public string? ClientCity { get; set; }
    public string? ClientPsc { get; set; }
    public int ClientCountryId { get; set; }
    public string? ClientCountryShort { get; set; }
    public string? ClientState { get; set; }
    public int? ClientStateId { get; set; }
    public string? ClientCounty { get; set; }
    public int? ClientCountyId { get; set; }
    public int? ClientZipId { get; set; }

    // Kontakt
    public string? ClientPhone { get; set; }
    public string? ClientMail { get; set; }

    // Role a stav
    public int ClientRight { get; set; }
    public DateTime? ClientDate { get; set; }
    public byte ClientDisable { get; set; }

    // Owner
    public int? IdOwner { get; set; }

    // FieldForce sync sloupce (přidané DDL migrací F0-02)
    public Guid? FfCompanyId { get; set; }
    public string? FfSyncSource { get; set; }
    public DataOwner DataOwner { get; set; } = DataOwner.Pipedrive;
    public DateTime? LastFfSyncAt { get; set; }

    // NEMODIFIKOVAT — historické Pipedrive hodnoty
    // pipe_id, pipeType, int_client jsou záměrně vynechány z modelu
}
