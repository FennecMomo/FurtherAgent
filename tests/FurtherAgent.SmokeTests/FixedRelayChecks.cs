using FurtherAgent.Core;

internal static class FixedRelayChecks
{
    public static void Run(Action<bool, string> check)
    {
        check(RelayEndpoint.IsBuiltIn(RelayEndpoint.Address) && RelayEndpoint.IsBuiltIn(RelayEndpoint.Address + '/'),
            "the fixed IP is recognized with or without the default path");
        check(RelayEndpoint.IsBuiltIn("HTTPS://FURTHERAGENT.FENNECMOMO.TOP:443/"),
            "legacy Cloudflare addresses normalize to the built-in relay");
        check(!RelayEndpoint.IsBuiltIn("https://relay.example") &&
            !RelayEndpoint.IsBuiltIn(RelayEndpoint.Address + "/other") &&
            !RelayEndpoint.IsBuiltIn(RelayEndpoint.LegacyAddress + "/?other=1"),
            "saved credentials are not matched to unrelated relays or paths");

        var directory = Directory.CreateTempSubdirectory("furtheragent-fixed-relay-");
        const string identityFileKey = "FURTHERAGENT_IDENTITY_FILE";
        const string identityDirectoryKey = "FURTHERAGENT_IDENTITY_DIR";
        const string trustDirectoryKey = "FURTHERAGENT_LAN_TRUST_DIR";
        string? oldIdentity = Environment.GetEnvironmentVariable(identityFileKey);
        string? oldDirectory = Environment.GetEnvironmentVariable(identityDirectoryKey);
        string? oldTrust = Environment.GetEnvironmentVariable(trustDirectoryKey);
        try
        {
            Environment.SetEnvironmentVariable(identityFileKey, null);
            Environment.SetEnvironmentVariable(identityDirectoryKey, directory.FullName);
            Environment.SetEnvironmentVariable(trustDirectoryKey, directory.FullName);

            string directAddress = new Uri(RelayEndpoint.Address).AbsoluteUri;
            string legacyAddress = new Uri(RelayEndpoint.LegacyAddress).AbsoluteUri;
            const string device = "fixture device";
            string directPath = DeviceIdentity.DefaultPath(directAddress, device);
            string legacyPath = DeviceIdentity.DefaultPath(legacyAddress, device);
            check(directPath != legacyPath && !File.Exists(directPath), "a new fixed-relay device has its own default identity path");

            string legacyCode = DeviceIdentity.Code(DeviceIdentity.LoadOrCreate(legacyPath));
            string resolvedPath = DeviceIdentity.DefaultPath(directAddress, device);
            check(resolvedPath == legacyPath && DeviceIdentity.Code(DeviceIdentity.LoadOrCreate(resolvedPath)) == legacyCode && !File.Exists(directPath),
                "switching from HTTPS reuses the original identity without creating another file");
            using var mailbox = new MailboxAgentAccount(device, legacyAddress, legacyPath).Open();
            check(mailbox.Server == directAddress && mailbox.Binding.Code == legacyCode,
                "an existing Agent mailbox account uses the fixed relay and keeps its device identity");

            _ = DeviceIdentity.LoadOrCreate(directPath);
            check(DeviceIdentity.DefaultPath(directAddress, device) == directPath,
                "an already switched device keeps its existing fixed-IP identity");
            string customPath = Path.Combine(directory.FullName, "custom.identity");
            Environment.SetEnvironmentVariable(identityFileKey, customPath);
            check(DeviceIdentity.DefaultPath(directAddress, device) == customPath, "an explicit identity file takes precedence");

            const string pairingCode = "ABCD-1234";
            var legacyPairing = new LanPairing(device, new string('A', 64), new string('B', 64), 45984);
            LanTrustStore.Save(legacyAddress, pairingCode, legacyPairing);
            check(LanTrustStore.Load(directAddress, "abcd1234") == legacyPairing,
                "existing LAN trust is read using the original relay address after switching to the fixed IP");
            var currentPairing = legacyPairing with { Port = 45985 };
            LanTrustStore.Save(directAddress, pairingCode, currentPairing);
            check(LanTrustStore.Load(directAddress, pairingCode) == currentPairing &&
                LanTrustStore.Load("https://relay.example/", pairingCode) is null,
                "fixed-IP LAN trust takes precedence and stays isolated from other relays");
        }
        finally
        {
            Environment.SetEnvironmentVariable(identityFileKey, oldIdentity);
            Environment.SetEnvironmentVariable(identityDirectoryKey, oldDirectory);
            Environment.SetEnvironmentVariable(trustDirectoryKey, oldTrust);
            foreach (var file in directory.EnumerateFiles()) file.Delete();
            directory.Delete();
        }
    }
}
