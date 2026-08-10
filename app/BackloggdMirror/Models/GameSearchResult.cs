using Avalonia.Media.Imaging;

namespace BackloggdMirror.Models
{
    /// <summary>
    /// A candidate in the manual game picker, shown when detection could not identify the game.
    /// </summary>
    public class GameSearchResult
    {
        public string Title { get; set; } = string.Empty;

        public string CoverUrl { get; set; } = string.Empty;

        /// <summary>
        /// Backloggd href for the game ("/games/slug/"). Picking this result is what supplies
        /// RegisterGame with a slug, turning the search fallback into a direct navigation.
        /// </summary>
        public string RedirectLink { get; set; } = string.Empty;

        public Bitmap? CoverBitmap { get; set; }
    }
}
