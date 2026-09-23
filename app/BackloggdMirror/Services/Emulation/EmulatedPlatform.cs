namespace BackloggdMirror.Services.Emulation;

/// <summary>A console an emulator can run; <see cref="Key"/> is the suffix of detectable_emu_&lt;key&gt;.json.</summary>
public sealed record EmulatedPlatform(
    string Key,
    string DisplayName,
    int? IgdbPlatformId,
    string[] Extensions,
    string[] LibretroSystemIds,
    string[] DatabasePrefixes,
    bool HasLocalDatabase);
