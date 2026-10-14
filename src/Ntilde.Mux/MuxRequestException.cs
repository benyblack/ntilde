namespace Ntilde.Mux;

/// <summary>A per-request failure: answered with an error Response; the connection stays open.</summary>
internal sealed class MuxRequestException : Exception
{
    public MuxRequestException(string code, string message) : base(message) => Code = code;

    public string Code { get; }
}
