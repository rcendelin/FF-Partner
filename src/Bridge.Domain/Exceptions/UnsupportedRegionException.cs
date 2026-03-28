namespace Bridge.Domain.Exceptions;

public sealed class UnsupportedRegionException : Exception
{
    public string CountryCode { get; }

    public UnsupportedRegionException(string countryCode)
        : base($"Nepodporovaná země '{countryCode}' — žádný region není namapován. Publikovat sync-failed s kódem UNSUPPORTED_REGION.")
    {
        CountryCode = countryCode;
    }
}
