namespace FurtherAgent.Core;

public static class RelayEndpoint
{
    public const string Address = "http://175.24.131.154:47320";
    public const string LegacyAddress = "https://furtheragent.fennecmomo.top";

    private static readonly string normalizedAddress = new Uri(Address).AbsoluteUri;
    private static readonly string normalizedLegacyAddress = new Uri(LegacyAddress).AbsoluteUri;

    public static bool IsBuiltIn(string address) => Uri.TryCreate(address, UriKind.Absolute, out var uri) &&
        (uri.AbsoluteUri == normalizedAddress || uri.AbsoluteUri == normalizedLegacyAddress);
}
