using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace BackloggdMirror.Services;

public class LocalizationService : INotifyPropertyChanged
{
    private static LocalizationService? _instance;
    public static LocalizationService Instance => _instance ??= new LocalizationService();

    private Dictionary<string, string> _currentStrings = new();
    private readonly Dictionary<string, Dictionary<string, string>> _resources = new();

    public LocalizationService()
    {
        InitializeResources();
    }

    private void InitializeResources()
    {
        _resources["en"] = new Dictionary<string, string>
        {
            { "Login_Welcome", "Welcome" },
            { "Login_Username", "Email / Username" },
            { "Login_Password", "Password" },
            { "Login_RememberMe", "Remember me" },
            { "Login_Button", "LOGIN" },
            { "Settings_Language", "Language" },
            { "Settings_SyncSystem", "Sync with system" },
            { "Settings_Spanish", "Español" },
            { "Settings_English", "English" },
            { "Login_Status_Restoring", "Restoring session..." },
            { "Login_Status_SessionExpired", "Session expired. Please log in again." },
            { "Login_Status_EnterCredentials", "Please enter your email / username and password." },
            { "Login_Status_LoggingIn", "Logging in..." },
            { "Login_Status_Success", "Success!" },
            { "Login_Status_Failed", "Login failed. Please check your credentials." },
            { "Login_Status_BrowserClosed", "Login window was closed." },
            { "Login_Status_Timeout", "Login timed out. The website structure may have changed or your connection is unstable." },
            { "Login_Status_NetworkError", "Could not connect to Backloggd. Please check your internet connection and try again." },
            { "Login_Status_BlockedByAntiBot", "Backloggd's anti-bot protection blocked the login. Please wait a few minutes and try again." },
            { "Sidebar_Home", "Home" },
            { "Sidebar_Settings", "Settings" },
            { "Sidebar_Logout", "Logout" },
            { "Home_WaitingForGame", "Waiting for game..." },
            { "Home_PauseSearch", "Pause" },
            { "Home_SearchPaused", "Detection paused" },
            { "Home_ResumeSearch", "Resume" },
            { "Home_PlayingNow", "Now playing" },
            { "Home_RecentlyPlayed", "Recently played" },
            { "Home_ReloadGamesTooltip", "Reload games" },
            { "Home_NoGamesRegistered", "No games registered yet" },
            { "Home_ErrorFetchingGames", "Error fetching recently played games" },
            { "Settings_Section_General", "General" },
            { "Settings_StartWithWindows", "Start AppLoggd with Windows" },
            { "Settings_MinimizeToTray", "Minimize to tray" },
            { "Settings_MinimizeToTrayDesc", "The application will continue running in the background." },
            { "Settings_Section_Appearance", "Appearance" },
            { "Settings_Section_AccountData", "Account & Data" },
            { "Settings_ClearData", "Delete Apploggd data" },
            { "Settings_ClearDataDesc", "Deletes your saved session, credentials and preferences. Log files are kept." },
            { "Settings_ClearDataButton", "Delete data" },
            { "ClearData_ConfirmTitle", "Delete Apploggd data?" },
            { "ClearData_ConfirmBody", "All data Apploggd has stored on this computer (saved session, credentials and settings) will be deleted and you will be logged out. Log files are kept so errors can still be diagnosed." },
            { "ClearData_Cancel", "Cancel" },
            { "ClearData_Confirm", "Delete and log out" },
            { "Toast_ClearDataFailed", "Some data could not be deleted. Close any program using it and try again, or check the logs." },
            { "Toast_StartWithWindowsFailed", "The setting could not be applied and has been left as it was. Check the logs for details." },
            { "Settings_MadeBy", "Made with 🤍 by nik250" },
            { "Settings_Section_About", "About" },
            { "Settings_Version", "Version " },
            { "Settings_ViewChangelog", "View changelog" },
            { "Changelog_Title", "What's new" },
            { "Changelog_Empty", "The changelog could not be loaded." },
            { "Changelog_Close", "Close" },
            // AppUpdate_* = a new Apploggd version. Not to be confused with Update_*, which is the
            // games database update.
            { "AppUpdate_Available", "New version available: {0}" },
            { "AppUpdate_PublishedOn", "Released on {0}" },
            { "AppUpdate_DateFormat", "MMMM d, yyyy" },
            { "AppUpdate_Download", "Download" },
            { "AppUpdate_NoBrowser_Title", "No web browser found" },
            { "AppUpdate_NoBrowser_Body", "Apploggd could not open a web browser to show the downloads page. You can download the new version manually from:" },
            { "AppUpdate_NoBrowser_Close", "Got it" },
            { "Session_SearchGameWatermark", "Search for a game..." },
            { "Session_Cancel", "Cancel" },
            { "Session_ConfirmTitle", "Confirm Session" },
            { "Session_IncorrectGameTooltip", "Click on the image if the identified game is incorrect." },
            { "Session_ChangeGameTooltip", "Change game" },
            { "Session_TotalTime", "Total time: " },
            { "Session_Discard", "Discard" },
            { "Session_Save", "Save" },
            { "Time_Today", "Today" },
            { "Time_Yesterday", "Yesterday" },
            { "Time_DaysAgo", "{0} days ago" },
            { "Time_OneWeekAgo", "1 week ago" },
            { "Time_WeeksAgo", "{0} weeks ago" },
            { "Time_OneMonthAgo", "1 month ago" },
            { "Time_MonthsAgo", "{0} months ago" },
            { "Time_OneYearAgo", "1 year ago" },
            { "Time_YearsAgo", "{0} years ago" },
            { "Toast_SessionSaved", "Session saved successfully" },
            { "Toast_ErrorSaving", "An unexpected error occurred while saving the session." },
            { "Toast_ConnectionError", "Could not connect to Backloggd. Please check your internet connection." },
            { "Toast_TimeoutError", "The operation timed out. Backloggd might be down or your connection is unstable." },
            { "Toast_UnexpectedError", "Unexpected error:\n{0}" },
            { "Toast_SessionTooShort", "Session not saved: duration was less than 1 minute." },
            { "Tray_Exit", "Exit" },
            { "Tray_Playing", "Playing {0} {1}" },
            { "Tray_WaitingConfirmation", "Waiting for session confirmation" },
            { "Session_UnidentifiedGame", "Unidentified game. Click the cover to select it manually." },
            { "Tray_BackgroundRunning", "AppLoggd is still running in the background" },
            { "Update_Checking", "Checking for game database updates..." },
            { "Update_Success", "Game database updated successfully." },
            { "Update_NotModified", "Game database is already up to date." },
            { "Update_NetworkError", "Network error while updating the game database. The local database will be used. Some games might not be detected correctly." },
            { "Update_InvalidContent", "The downloaded game database was invalid. The local database will be used. Some games might not be detected correctly." },
            { "Update_UnexpectedError", "Unexpected error while updating the game database. The local database will be used. Some games might not be detected correctly." },
            { "Update_ConnectingToServer", "Connecting to server..." },
            { "Update_DownloadingDatabase", "Downloading the updated game database..." },
            { "Login_Status_BrowserNotFound", "There are no valid browser components installed" },
            { "Browser_Install_Checking", "Checking browser components installation..." },
            { "Browser_Install_Downloading", "Downloading browser components (this may take a few minutes)..." },
            { "Browser_Install_Failed", "Could not install browser components. Check your connection and the logs, then restart the app." },
            { "Login_Status_BrowserDepsMissing", "The browser components are installed but your system is missing required libraries. See the logs for details." },
            { "Browser_Detect_System", "Looking for an installed browser (Chrome / Edge)..." },
            { "Browser_Prompt_Title", "A browser is required" },
            { "Browser_Prompt_Body", "Apploggd needs Chromium to browse the Backloggd website. Do you want to download it now?" },
            { "Browser_Prompt_Size", "Estimated download: ~400 MB." },
            { "Browser_Prompt_ManualHint", "You can also get Chromium by installing the Google Chrome browser, which already includes it:" },
            { "Browser_Prompt_LinkText", "Download Google Chrome" },
            { "Browser_Prompt_Accept", "Accept" },
            { "Browser_Prompt_Close", "Close" }
        };

        _resources["es"] = new Dictionary<string, string>
        {
            { "Login_Welcome", "Bienvenido" },
            { "Login_Username", "Email / Usuario" },
            { "Login_Password", "Contraseña" },
            { "Login_RememberMe", "Recuérdame" },
            { "Login_Button", "LOGIN" },
            { "Settings_Language", "Idioma" },
            { "Settings_SyncSystem", "Sincronizar con sistema" },
            { "Settings_Spanish", "Español" },
            { "Settings_English", "English" },
            { "Login_Status_Restoring", "Restaurando sesión..." },
            { "Login_Status_SessionExpired", "La sesión ha caducado. Por favor, inicia sesión de nuevo." },
            { "Login_Status_EnterCredentials", "Por favor, introduce usuario y contraseña." },
            { "Login_Status_LoggingIn", "Iniciando sesión..." },
            { "Login_Status_Success", "¡Éxito!" },
            { "Login_Status_Failed", "Inicio de sesión fallido. Comprueba tus credenciales." },
            { "Login_Status_BrowserClosed", "La ventana de inicio de sesión fue cerrada." },
            { "Login_Status_Timeout", "El inicio de sesión ha tardado demasiado. La web puede haber cambiado o tu conexión es inestable." },
            { "Login_Status_NetworkError", "No se pudo conectar con Backloggd. Comprueba tu conexión a internet e inténtalo de nuevo." },
            { "Login_Status_BlockedByAntiBot", "La protección anti-bots de Backloggd ha bloqueado el inicio de sesión. Espera unos minutos e inténtalo de nuevo." },
            { "Sidebar_Home", "Inicio" },
            { "Sidebar_Settings", "Ajustes" },
            { "Sidebar_Logout", "Cerrar sesión" },
            { "Home_WaitingForGame", "Esperando juego..." },
            { "Home_PauseSearch", "Pausar" },
            { "Home_SearchPaused", "Detección pausada" },
            { "Home_ResumeSearch", "Reanudar" },
            { "Home_PlayingNow", "Jugando ahora" },
            { "Home_RecentlyPlayed", "Jugados recientemente" },
            { "Home_ReloadGamesTooltip", "Recargar juegos" },
            { "Home_NoGamesRegistered", "Todavía no se ha registrado ningún juego" },
            { "Home_ErrorFetchingGames", "Error obteniendo los últimos juegos registrados" },
            { "Settings_Section_General", "General" },
            { "Settings_StartWithWindows", "Ejecutar AppLoggd cuando se inicie el equipo" },
            { "Settings_MinimizeToTray", "Minimizar a la bandeja" },
            { "Settings_MinimizeToTrayDesc", "La aplicación seguirá ejecutándose en segundo plano." },
            { "Settings_Section_Appearance", "Apariencia" },
            { "Settings_Section_AccountData", "Cuenta y Datos" },
            { "Settings_ClearData", "Borrar los datos de Apploggd" },
            { "Settings_ClearDataDesc", "Elimina la sesión guardada, las credenciales y tus preferencias. Los logs se conservan." },
            { "Settings_ClearDataButton", "Borrar datos" },
            { "ClearData_ConfirmTitle", "¿Borrar los datos de Apploggd?" },
            { "ClearData_ConfirmBody", "Se eliminarán todos los datos que Apploggd guarda en este equipo (sesión guardada, credenciales y ajustes) y se cerrará la sesión. Los logs se conservan para poder diagnosticar errores." },
            { "ClearData_Cancel", "Cancelar" },
            { "ClearData_Confirm", "Borrar y cerrar sesión" },
            { "Toast_ClearDataFailed", "No se han podido borrar algunos datos. Cierra cualquier programa que los esté usando e inténtalo de nuevo, o consulta los logs." },
            { "Toast_StartWithWindowsFailed", "No se ha podido aplicar el ajuste y se ha dejado como estaba. Consulta los logs para más detalles." },
            { "Settings_MadeBy", "Hecho con 🤍 por nik250" },
            { "Settings_Section_About", "Acerca de" },
            { "Settings_Version", "Versión " },
            { "Settings_ViewChangelog", "Ver novedades" },
            { "Changelog_Title", "Novedades" },
            { "Changelog_Empty", "No se ha podido cargar el changelog." },
            { "Changelog_Close", "Cerrar" },
            // AppUpdate_* = a new Apploggd version. Not to be confused with Update_*, which is the
            // games database update.
            { "AppUpdate_Available", "Nueva versión disponible: {0}" },
            { "AppUpdate_PublishedOn", "Publicada el {0}" },
            { "AppUpdate_DateFormat", "d 'de' MMMM 'de' yyyy" },
            { "AppUpdate_Download", "Descargar" },
            { "AppUpdate_NoBrowser_Title", "No se ha encontrado ningún navegador" },
            { "AppUpdate_NoBrowser_Body", "Apploggd no ha podido abrir un navegador para mostrarte la página de descargas. Puedes descargar la nueva versión manualmente desde:" },
            { "AppUpdate_NoBrowser_Close", "Entendido" },
            { "Session_SearchGameWatermark", "Buscar juego..." },
            { "Session_Cancel", "Cancelar" },
            { "Session_ConfirmTitle", "Confirmar Sesión" },
            { "Session_IncorrectGameTooltip", "Pulsa sobre la imagen si el juego identificado es incorrecto." },
            { "Session_ChangeGameTooltip", "Cambiar juego" },
            { "Session_TotalTime", "Tiempo total: " },
            { "Session_Discard", "Descartar" },
            { "Session_Save", "Guardar" },
            { "Time_Today", "Hoy" },
            { "Time_Yesterday", "Ayer" },
            { "Time_DaysAgo", "Hace {0} días" },
            { "Time_OneWeekAgo", "Hace una semana" },
            { "Time_WeeksAgo", "Hace {0} semanas" },
            { "Time_OneMonthAgo", "Hace un mes" },
            { "Time_MonthsAgo", "Hace {0} meses" },
            { "Time_OneYearAgo", "Hace un año" },
            { "Time_YearsAgo", "Hace {0} años" },
            { "Toast_SessionSaved", "Sesión guardada con éxito" },
            { "Toast_ErrorSaving", "Ha ocurrido un error inesperado al guardar la sesión." },
            { "Toast_ConnectionError", "No se ha podido conectar con Backloggd. Por favor, comprueba tu conexión a internet." },
            { "Toast_TimeoutError", "La operación ha tardado demasiado tiempo. Puede que Backloggd no esté funcionando correctamente o tu conexión sea inestable." },
            { "Toast_UnexpectedError", "Error inesperado:\n{0}" },
            { "Toast_SessionTooShort", "Sesión no registrada: duración inferior a 1 minuto." },
            { "Tray_Exit", "Salir" },
            { "Tray_Playing", "Jugando {0} {1}" },
            { "Tray_WaitingConfirmation", "Esperando confirmar/descartar sesión" },
            { "Session_UnidentifiedGame", "Juego no identificado. Haz click sobre la cover para seleccionarlo manualmente." },
            { "Tray_BackgroundRunning", "AppLoggd sigue ejecutándose en segundo plano" },
            { "Update_Checking", "Comprobando actualizaciones de la base de datos de juegos..." },
            { "Update_Success", "Base de datos de juegos actualizada con éxito." },
            { "Update_NotModified", "La base de datos de juegos ya está actualizada." },
            { "Update_NetworkError", "Error de conexión al actualizar la base de datos de juegos. Se utilizará la base de datos local. Es posible que algunos juegos no se detecten correctamente." },
            { "Update_InvalidContent", "La base de datos de juegos descargada no es válida. Se utilizará la base de datos local. Es posible que algunos juegos no se detecten correctamente." },
            { "Update_UnexpectedError", "Error inesperado al actualizar la base de datos de juegos. Se utilizará la base de datos local. Es posible que algunos juegos no se detecten correctamente." },
            { "Update_ConnectingToServer", "Estableciendo conexión con el servidor..." },
            { "Update_DownloadingDatabase", "Descargando la base de datos de juegos actualizada..." },
            { "Login_Status_BrowserNotFound", "No se encontraron los componentes de navegador instalados" },
            { "Browser_Install_Checking", "Comprobando la instalación de componentes del navegador..." },
            { "Browser_Install_Downloading", "Descargando componentes del navegador (puede tardar unos minutos)..." },
            { "Browser_Install_Failed", "No se pudieron instalar los componentes del navegador. Comprueba tu conexión y los registros, y reinicia la aplicación." },
            { "Login_Status_BrowserDepsMissing", "Los componentes del navegador están instalados pero faltan librerías del sistema necesarias. Consulta los registros para más detalles." },
            { "Browser_Detect_System", "Buscando un navegador instalado (Chrome / Edge)..." },
            { "Browser_Prompt_Title", "Se necesita un navegador" },
            { "Browser_Prompt_Body", "Apploggd necesita Chromium para navegar por la web de Backloggd. ¿Quieres descargarlo ahora?" },
            { "Browser_Prompt_Size", "Descarga estimada: ~400 MB." },
            { "Browser_Prompt_ManualHint", "También puedes obtener Chromium instalando el navegador Google Chrome, que ya lo incluye:" },
            { "Browser_Prompt_LinkText", "Descargar Google Chrome" },
            { "Browser_Prompt_Accept", "Aceptar" },
            { "Browser_Prompt_Close", "Cerrar" }
        };

        // Initial load - default to what matches system or English
        SetLanguage("System");
    }

    /// <summary>Language actually in use ("es" / "en"), already resolved if it was "System".</summary>
    public string CurrentLanguage { get; private set; } = "en";

    /// <summary>
    /// Culture matching <see cref="CurrentLanguage"/>, for formatting dates and numbers.
    /// <see cref="CultureInfo.CurrentUICulture"/> is deliberately not used, because the user may
    /// have picked a language in Settings that differs from the system one.
    /// </summary>
    public CultureInfo CurrentCulture =>
        CurrentLanguage == "es" ? new CultureInfo("es-ES") : new CultureInfo("en-US");

    public string this[string key]
    {
        get
        {
            if (_currentStrings.TryGetValue(key, out var value))
            {
                return value;
            }
            return $"[{key}]";
        }
    }

    /// <summary>
    /// Switches the active language. Accepts "System", which is resolved against the OS UI culture
    /// here rather than being persisted as a concrete code, so the app follows the system if the
    /// user later changes it.
    /// </summary>
    public void SetLanguage(string languageCode)
    {
        string targetLang = "en"; // Default fallback

        if (languageCode == "System")
        {
            var culture = CultureInfo.CurrentUICulture;
            // Matched on the prefix so every Spanish variant (es-ES, es-MX, ...) counts.
            if (culture.Name.StartsWith("es", StringComparison.OrdinalIgnoreCase))
            {
                targetLang = "es";
            }
            else
            {
                targetLang = "en"; // Fallback for unsupported system languages
            }
        }
        else if (_resources.ContainsKey(languageCode))
        {
            targetLang = languageCode;
        }

        if (_resources.ContainsKey(targetLang))
        {
            _currentStrings = _resources[targetLang];
            CurrentLanguage = targetLang;

            // Every visible string is an indexer binding, so there is nothing more granular to
            // raise: "Item[]" invalidates them all, and the empty name catches the rest.
            OnPropertyChanged("Item[]");
            OnPropertyChanged(string.Empty);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
