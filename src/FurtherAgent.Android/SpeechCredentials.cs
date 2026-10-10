namespace FurtherAgent.AndroidApp;

internal static class SpeechCredentials
{
    private const string DisabledName = "speech-bundled-key-disabled";
    private static string BundledKey
    {
        get
        {
            using var stream = typeof(SpeechCredentials).Assembly.GetManifestResourceStream("FurtherAgent.PersonalSpeechKey");
            if (stream is null) return "";
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd().Trim();
        }
    }
    public static bool HasBundledKey => BundledKey.Length > 0;
    public static async Task<string> GetAsync()
    {
        string saved = await SecureStorage.GetAsync(SpeechSettingsPage.KeyName) ?? "";
        if (!string.IsNullOrWhiteSpace(saved)) return saved;
        return Preferences.Get(DisabledName, false) ? "" : BundledKey;
    }
    public static void Delete()
    {
        SecureStorage.Remove(SpeechSettingsPage.KeyName);
        Preferences.Set(DisabledName, true);
    }
    public static void UseBundled()
    {
        SecureStorage.Remove(SpeechSettingsPage.KeyName);
        Preferences.Remove(DisabledName);
    }
}
