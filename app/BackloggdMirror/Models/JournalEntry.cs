using Avalonia.Media.Imaging;

namespace BackloggdMirror.Models
{
    /// <summary>
    /// One row of the "recently played" list, scraped from the user's Backloggd journal.
    /// Every field is display text taken verbatim from the site, not parsed data.
    /// </summary>
    public class JournalEntry
    {
        public string GameName { get; set; } = string.Empty;

        /// <summary>Cover URL as scraped; <see cref="CoverBitmap"/> is filled in afterwards.</summary>
        public string CoverImage { get; set; } = string.Empty;

        public Bitmap? CoverBitmap { get; set; }

        /// <summary>Backloggd's own formatting ("2h 30m"), or "-" when logged without a time.</summary>
        public string PlayTime { get; set; } = string.Empty;

        /// <summary>Already localized and relative ("yesterday"), so it is not a parseable date.</summary>
        public string RegistrationDate { get; set; } = string.Empty;
    }
}
