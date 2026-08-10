using System;

namespace BackloggdMirror.Services
{
    /// <summary>
    /// Writes to the on-disk log that survives a data wipe, which makes it the only record left to
    /// diagnose a problem after the fact. Implementations never throw: a failing logger must not
    /// take down the operation it was reporting on.
    /// </summary>
    public interface IAppLogger
    {
        void Info(string message);
        void Warning(string message);
        void Error(string message, Exception? ex = null);
    }
}
