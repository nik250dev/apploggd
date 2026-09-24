using System;
using System.Linq;
using BackloggdMirror.Models;
using BackloggdMirror.Services.Emulation.Cemu;
using BackloggdMirror.Services.Emulation.Dolphin;
using BackloggdMirror.Services.Emulation.RetroArch;

namespace BackloggdMirror.Services.Emulation;

/// <summary>
/// Detection tier 1.5. An emulator is one executable that runs any number of games, so neither the
/// executable database nor the window heuristic can name what is being played.
/// </summary>
internal sealed class EmulatorDetector
{
    private readonly IEmulatorDetector[] _detectors;
    private readonly IAppLogger? _logger;

    public EmulatorDetector(IAppLogger? logger = null, IDetectionBlacklist? blacklist = null)
    {
        _logger = logger;
        EmulatedGamesDatabase.Instance.Logger = logger;

        var resolver = new EmulatedGameResolver(logger);
        _detectors = new IEmulatorDetector[]
        {
            new RetroArchDetector(resolver, logger, blacklist),
            new DolphinDetector(resolver, logger, blacklist),
            new CemuDetector(resolver, logger, blacklist)
        };
    }

    public DetectedGame? Detect()
    {
        foreach (var detector in _detectors)
        {
            try
            {
                var game = detector.Detect();
                if (game != null)
                    return game;
            }
            catch (Exception ex)
            {
                _logger?.Error($"[EmulatorDetector] The {detector.Name} detector failed. Emulated sessions for it are not detected this tick.", ex);
            }
        }

        return null;
    }

    public bool IsStillRunning(DetectedGame game)
    {
        var detector = _detectors.FirstOrDefault(d => d.Name.Equals(game.EmulatorName, StringComparison.OrdinalIgnoreCase));
        if (detector == null)
            return false;

        try
        {
            return detector.IsStillRunning(game);
        }
        catch (Exception ex)
        {
            // Ending the session on an unexpected failure is the safe side: the alternative is a
            // timer that never stops.
            _logger?.Error($"[EmulatorDetector] The {detector.Name} detector failed while checking the running session. Closing it.", ex);
            return false;
        }
    }
}
