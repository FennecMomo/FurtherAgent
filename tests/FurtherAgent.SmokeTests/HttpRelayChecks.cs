using FurtherAgent.Core;

internal static class HttpRelayChecks
{
    public static void Run(Action<bool, string> check)
    {
        // RFC documentation addresses are used only to validate the configuration.
        using var ipv4 = new RegistryClient("http://192.0.2.15:47320");
        check(ipv4.ServerIdentity == "http://192.0.2.15:47320/",
            "an explicitly configured non-loopback IPv4 relay can use HTTP");

        using var ipv6 = new RegistryClient("http://[2001:db8::15]:47320");
        check(ipv6.ServerIdentity == "http://[2001:db8::15]:47320/",
            "an explicitly configured non-loopback IPv6 relay can use HTTP");

        using var local = new RegistryClient("http://localhost:47320");
        using var https = new RegistryClient("https://relay.example");
        check(local.ServerIdentity == "http://localhost:47320/" && https.ServerIdentity == "https://relay.example/",
            "existing loopback HTTP and hostname HTTPS addresses remain supported");

        check(IsRejected("http://relay.example:47320"), "HTTP addresses using a non-loopback hostname are rejected");
        check(IsRejected("ftp://192.0.2.15:47320"), "non-HTTP relay schemes are rejected");
        check(IsRejected("http://[2001:db8::15:47320"), "malformed numeric relay addresses are rejected");
    }

    private static bool IsRejected(string address)
    {
        try { using var registry = new RegistryClient(address); return false; }
        catch (Exception ex) when (ex is ArgumentException or UriFormatException) { return true; }
    }
}
