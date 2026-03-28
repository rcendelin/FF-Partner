namespace Bridge.Domain.Exceptions;

public enum GeoValidationErrorCode
{
    UnknownCountry,
    UnknownZip,
    UnknownState,
    UnknownCounty
}

public sealed class GeoValidationException : Exception
{
    public GeoValidationErrorCode ErrorCode { get; }

    public GeoValidationException(string message, GeoValidationErrorCode errorCode)
        : base(message)
    {
        ErrorCode = errorCode;
    }
}
