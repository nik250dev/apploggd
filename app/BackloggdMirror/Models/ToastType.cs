namespace BackloggdMirror.Models;

/// <summary>
/// Severity of a transient toast. Drives colour and icon through the Converters; adding a value
/// here means updating all four ToastType converters.
/// </summary>
public enum ToastType
{
    Success,
    Warning,
    Error
}
