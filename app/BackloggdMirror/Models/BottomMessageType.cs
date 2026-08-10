namespace BackloggdMirror.Models;

/// <summary>
/// State of the persistent bottom message bar, used mainly by the games database update.
/// Unlike <see cref="ToastType"/> it has a None (bar hidden) and a Loading state, because this bar
/// reports ongoing work rather than a finished outcome.
/// </summary>
public enum BottomMessageType
{
    None,
    Loading,
    Success,
    Warning,
    Error
}
