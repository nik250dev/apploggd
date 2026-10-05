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
            { "Blacklist_NotAGame", "Not a game?" },
            { "Blacklist_NotWanted", "Don't want to log it?" },
            { "Blacklist_Add", "Add to blacklist" },
            { "Home_RecentlyPlayed", "Recently played" },
            { "Home_ReloadGamesTooltip", "Reload games" },
            { "Home_NoGamesRegistered", "No games registered yet" },
            { "Home_ErrorFetchingGames", "Error fetching recently played games" },
            { "Home_OfflineBadge", "Offline" },
            { "Home_OfflineTooltip", "No connection to Backloggd. Game detection keeps running." },
            { "Home_OfflineRecentlyPlayed", "No connection. The list will load once it's back." },
            { "Session_OfflineSaveTooltip", "No connection to Backloggd. The session can be kept in Pending or discarded." },
            { "Pending_OfflineSaveTooltip", "No connection to Backloggd. It can be saved once the connection is back." },
            { "Settings_Section_General", "General" },
            { "Settings_StartWithWindows", "Start AppLoggd with Windows" },
            { "Settings_StartWithSystem", "Start AppLoggd when the computer starts" },
            { "Settings_MinimizeToTray", "Minimize to tray" },
            { "Settings_MinimizeToTrayDesc", "The application will continue running in the background." },
            { "Settings_Gamepad", "Controller navigation" },
            { "Settings_GamepadDesc", "The whole app can be used with a controller; the top face button minimizes it." },
            { "Gamepad_Select", "Select" },
            { "Gamepad_Choose", "Choose" },
            { "Gamepad_Cancel", "Cancel" },
            { "Gamepad_Back", "Back" },
            { "Gamepad_Minimize", "Minimize" },
            { "Settings_Section_Blacklist", "Blacklist" },
            { "Settings_Blacklist_Title", "Ignored apps and games" },
            { "Settings_Blacklist_Desc", "Apploggd won't detect or time them. Blocking an emulator ignores all of its games." },
            { "Settings_Blacklist_AddExe", "Add executable" },
            { "Settings_Blacklist_Remove", "Remove" },
            { "Settings_Blacklist_EmptyTitle", "Your blacklist is empty" },
            { "Settings_Blacklist_EmptyHint", "If Apploggd times something that isn't a game, press \"Add to blacklist\" under the timer." },
            { "Settings_Blacklist_PickerTitle", "Choose an executable to blacklist" },
            { "Settings_Blacklist_PickerFilter", "Executables" },
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
            { "AppUpdate_Update", "Update" },
            { "AppUpdate_UpdateButton", "Update Apploggd" },
            { "AppUpdate_Progress_Title", "Updating Apploggd" },
            { "AppUpdate_Progress_Downloading", "Downloading version {0}..." },
            { "AppUpdate_Progress_Applying", "Applying the update..." },
            { "AppUpdate_Progress_Restart", "Apploggd will restart on its own when it finishes." },
            { "AppUpdate_Failed", "Apploggd could not be updated. Try again later." },
            { "AppUpdate_SessionPending", "Finish with the session waiting to be confirmed before updating" },
            { "AppUpdate_ReadOnlyFolder", "Apploggd cannot be updated: its folder is read-only.\nPlease move the app to a folder you have write permission for." },
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
            { "Toast_BlacklistAdded", "\"{0}\" added to the blacklist" },
            { "Toast_BlacklistRemoved", "\"{0}\" removed from the blacklist" },
            { "Toast_BlacklistAlreadyListed", "\"{0}\" is already in the blacklist" },
            { "Toast_BlacklistScript", "\"{0}\" is a launch script. Choose the executable it starts" },
            { "Toast_BlacklistNotExecutable", "\"{0}\" is not an executable" },
            { "Toast_Undo", "Undo" },
            { "Toast_UpdateAvailable", "New Apploggd version available: {0}" },
            { "Toast_ErrorSaving", "An unexpected error occurred while saving the session." },
            { "Toast_ConnectionError", "Could not connect to Backloggd. Please check your internet connection." },
            { "Toast_TimeoutError", "The operation timed out. Backloggd might be down or your connection is unstable." },
            { "Toast_UnexpectedError", "Unexpected error:\n{0}" },
            { "Toast_SessionTooShort", "Session not saved: duration was less than 1 minute." },
            { "Sidebar_Pending", "Pending" },
            { "Session_PostponeTooltip", "Review later: the session is kept in Pending, ready to be saved at any time." },
            { "Toast_SessionPostponed", "\"{0}\" added to pending sessions" },
            { "Toast_ViewPending", "View" },
            { "Toast_AddToPending", "Add to pending" },
            { "Toast_PendingDiscarded", "Pending session of \"{0}\" discarded" },
            { "Pending_Title", "Pending sessions" },
            { "Pending_Desc", "Sessions set aside to review later. They stay here until they're saved or discarded." },
            { "Pending_SaveAll", "Save all" },
            { "Pending_Save", "Save" },
            { "Pending_Discard", "Discard" },
            { "Pending_ChangeGame", "Change game" },
            { "Pending_ChooseGame", "Choose game" },
            { "Pending_Unidentified", "Unidentified" },
            { "Pending_SaveFailed", "Couldn't be saved" },
            { "Pending_SavedAll", "{0} sessions saved" },
            { "Pending_SavedSome", "{0} of {1} sessions saved. The rest are still in Pending." },
            { "Pending_UnidentifiedTooltip", "The game has to be chosen before saving it." },
            { "Pending_EmptyTitle", "No pending sessions" },
            { "Pending_EmptyHint", "When confirming a session, the clock button leaves it here to review later." },
            { "Pending_DateFormat", "MMM d" },
            { "Pending_DateFormatWithYear", "MMM d, yyyy" },
            { "Settings_AlwaysPending", "Always add to pending" },
            { "Settings_AlwaysPendingDesc", "When a game closes, its session goes straight to Pending." },
            { "TrayNotice_PlayingBodyPending", "When you close it, the session will be added to Pending." },
            { "Tray_Open", "Open Apploggd" },
            { "Tray_Exit", "Exit" },
            { "Tray_Playing", "Playing {0} {1}" },
            { "Tray_WaitingConfirmation", "Waiting for session confirmation" },
            { "Session_UnidentifiedGame", "Unidentified game. Click the cover to select it manually." },
            { "TrayNotice_BackgroundTitle", "Apploggd is running in the background" },
            { "TrayNotice_IntroBody", "Closing the window doesn't stop detection. The tray icon's menu has an Exit option." },
            { "TrayNotice_DetectingKicker", "Detecting games" },
            { "TrayNotice_DetectingBody", "The next session will be logged automatically." },
            { "TrayNotice_PlayingKicker", "Playing now · {0}" },
            { "TrayNotice_PlayingBody", "The session will be ready for confirmation once the game closes." },
            { "TrayNotice_PausedKicker", "Detection paused" },
            { "TrayNotice_PausedBody", "No sessions will be logged until detection is resumed from the tray icon." },
            { "TrayNotice_PendingKicker", "Session waiting for confirmation" },
            { "TrayNotice_PendingBody", "Detection stays paused until the session is confirmed or discarded." },
            { "TrayNotice_PendingAction", "Review session" },
            { "TrayNotice_UpdateTitle", "New version available" },
            { "TrayNotice_UpdateBody", "Installing takes a few seconds, and the app restarts on its own." },
            { "TrayNotice_UpdateAction", "Update" },
            { "TrayNotice_Close", "Close" },
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
            { "Browser_Prompt_Close", "Close" },
            { "Browser_Deps_Title", "Missing system libraries" },
            { "Browser_Deps_Body", "Chromium is installed, but it cannot start because your system is missing some libraries. Open a terminal and run this command (it will ask for your Linux user password):" },
            { "Browser_Deps_BodyNoApt", "Chromium is installed, but it cannot start because your system is missing some libraries. Install them with your distribution's package manager." },
            { "Browser_Deps_Missing", "Missing: {0}" },
            { "Browser_Deps_Copy", "Copy command" },
            { "Browser_Deps_Copied", "Copied" },
            { "Browser_Deps_Retry", "Retry" }
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
            { "Blacklist_NotAGame", "¿No es un juego?" },
            { "Blacklist_NotWanted", "¿No quieres registrarlo?" },
            { "Blacklist_Add", "Añadir a la lista negra" },
            { "Home_RecentlyPlayed", "Jugados recientemente" },
            { "Home_ReloadGamesTooltip", "Recargar juegos" },
            { "Home_NoGamesRegistered", "Todavía no se ha registrado ningún juego" },
            { "Home_ErrorFetchingGames", "Error obteniendo los últimos juegos registrados" },
            { "Home_OfflineBadge", "Sin conexión" },
            { "Home_OfflineTooltip", "Sin conexión con Backloggd. La detección de juegos sigue funcionando." },
            { "Home_OfflineRecentlyPlayed", "Sin conexión. La lista se cargará al recuperarla." },
            { "Session_OfflineSaveTooltip", "Sin conexión con Backloggd. La sesión puede dejarse en Pendientes o descartarse." },
            { "Pending_OfflineSaveTooltip", "Sin conexión con Backloggd. Podrá guardarse al recuperar la conexión." },
            { "Settings_Section_General", "General" },
            { "Settings_StartWithWindows", "Ejecutar AppLoggd cuando se inicie el equipo" },
            { "Settings_StartWithSystem", "Ejecutar AppLoggd cuando se inicie el equipo" },
            { "Settings_MinimizeToTray", "Minimizar a la bandeja" },
            { "Settings_MinimizeToTrayDesc", "La aplicación seguirá ejecutándose en segundo plano." },
            { "Settings_Gamepad", "Control con mando" },
            { "Settings_GamepadDesc", "Toda la aplicación se puede usar con un mando; el botón superior la minimiza." },
            { "Gamepad_Select", "Seleccionar" },
            { "Gamepad_Choose", "Elegir" },
            { "Gamepad_Cancel", "Cancelar" },
            { "Gamepad_Back", "Volver" },
            { "Gamepad_Minimize", "Minimizar" },
            { "Settings_Section_Blacklist", "Lista negra" },
            { "Settings_Blacklist_Title", "Aplicaciones y juegos ignorados" },
            { "Settings_Blacklist_Desc", "Apploggd no los detectará ni cronometrará. Si bloqueas un emulador, se ignoran todos sus juegos." },
            { "Settings_Blacklist_AddExe", "Añadir ejecutable" },
            { "Settings_Blacklist_Remove", "Quitar" },
            { "Settings_Blacklist_EmptyTitle", "Tu lista negra está vacía" },
            { "Settings_Blacklist_EmptyHint", "Si Apploggd cronometra algo que no es un juego, pulsa «Añadir a la lista negra» bajo el contador." },
            { "Settings_Blacklist_PickerTitle", "Elige un ejecutable para la lista negra" },
            { "Settings_Blacklist_PickerFilter", "Ejecutables" },
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
            { "AppUpdate_Update", "Actualizar" },
            { "AppUpdate_UpdateButton", "Actualizar Apploggd" },
            { "AppUpdate_Progress_Title", "Actualizando Apploggd" },
            { "AppUpdate_Progress_Downloading", "Descargando la versión {0}..." },
            { "AppUpdate_Progress_Applying", "Aplicando la actualización..." },
            { "AppUpdate_Progress_Restart", "Apploggd se reiniciará solo al terminar." },
            { "AppUpdate_Failed", "No se ha podido actualizar Apploggd. Inténtalo más tarde." },
            { "AppUpdate_SessionPending", "Termina con la sesión pendiente de confirmar antes de actualizar" },
            { "AppUpdate_ReadOnlyFolder", "No es posible actualizar, la carpeta es de solo lectura.\nPor favor, mueve la aplicación a otro directorio con permisos de escritura." },
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
            { "Toast_BlacklistAdded", "«{0}» añadido a la lista negra" },
            { "Toast_BlacklistRemoved", "«{0}» quitado de la lista negra" },
            { "Toast_BlacklistAlreadyListed", "«{0}» ya está en la lista negra" },
            { "Toast_BlacklistScript", "«{0}» es un script de arranque. Elige el ejecutable que abre" },
            { "Toast_BlacklistNotExecutable", "«{0}» no es un ejecutable" },
            { "Toast_Undo", "Deshacer" },
            { "Toast_UpdateAvailable", "Nueva versión de Apploggd disponible: {0}" },
            { "Toast_ErrorSaving", "Ha ocurrido un error inesperado al guardar la sesión." },
            { "Toast_ConnectionError", "No se ha podido conectar con Backloggd. Por favor, comprueba tu conexión a internet." },
            { "Toast_TimeoutError", "La operación ha tardado demasiado tiempo. Puede que Backloggd no esté funcionando correctamente o tu conexión sea inestable." },
            { "Toast_UnexpectedError", "Error inesperado:\n{0}" },
            { "Toast_SessionTooShort", "Sesión no registrada: duración inferior a 1 minuto." },
            { "Sidebar_Pending", "Pendientes" },
            { "Session_PostponeTooltip", "Revisar más tarde: la sesión se guarda en Pendientes para que se registre cuando se quiera." },
            { "Toast_SessionPostponed", "«{0}» añadida a sesiones pendientes" },
            { "Toast_ViewPending", "Ver" },
            { "Toast_AddToPending", "Añadir a pendientes" },
            { "Toast_PendingDiscarded", "Sesión pendiente de «{0}» descartada" },
            { "Pending_Title", "Sesiones pendientes" },
            { "Pending_Desc", "Sesiones dejadas para revisar más tarde. Se quedan aquí hasta que se guarden o se descarten." },
            { "Pending_SaveAll", "Guardar todas" },
            { "Pending_Save", "Guardar" },
            { "Pending_Discard", "Descartar" },
            { "Pending_ChangeGame", "Cambiar juego" },
            { "Pending_ChooseGame", "Elegir juego" },
            { "Pending_Unidentified", "Sin identificar" },
            { "Pending_SaveFailed", "No se pudo guardar" },
            { "Pending_SavedAll", "{0} sesiones guardadas" },
            { "Pending_SavedSome", "{0} de {1} sesiones guardadas. El resto sigue en Pendientes." },
            { "Pending_UnidentifiedTooltip", "Hay que elegir el juego antes de guardarla." },
            { "Pending_EmptyTitle", "No hay sesiones pendientes" },
            { "Pending_EmptyHint", "Al confirmar una sesión, el botón del reloj la deja aquí para revisarla más tarde." },
            { "Pending_DateFormat", "d MMM" },
            { "Pending_DateFormatWithYear", "d MMM yyyy" },
            { "Settings_AlwaysPending", "Añadir siempre a pendientes" },
            { "Settings_AlwaysPendingDesc", "Al cerrar un juego, la sesión se guarda directamente en Pendientes." },
            { "TrayNotice_PlayingBodyPending", "Al cerrarlo, la sesión se añadirá a Pendientes." },
            { "Tray_Open", "Abrir Apploggd" },
            { "Tray_Exit", "Salir" },
            { "Tray_Playing", "Jugando {0} {1}" },
            { "Tray_WaitingConfirmation", "Esperando confirmar/descartar sesión" },
            { "Session_UnidentifiedGame", "Juego no identificado. Haz click sobre la cover para seleccionarlo manualmente." },
            { "TrayNotice_BackgroundTitle", "Apploggd se ejecuta en segundo plano" },
            { "TrayNotice_IntroBody", "Cerrar la ventana no detiene la detección. La opción «Salir» del menú del icono cierra la app por completo." },
            { "TrayNotice_DetectingKicker", "Detectando juegos" },
            { "TrayNotice_DetectingBody", "La próxima sesión se registrará automáticamente." },
            { "TrayNotice_PlayingKicker", "Jugando ahora · {0}" },
            { "TrayNotice_PlayingBody", "Al cerrar el juego, la sesión quedará pendiente de confirmar." },
            { "TrayNotice_PausedKicker", "Detección en pausa" },
            { "TrayNotice_PausedBody", "No se registrarán sesiones hasta reanudar la detección desde el icono." },
            { "TrayNotice_PendingKicker", "Sesión pendiente de confirmar" },
            { "TrayNotice_PendingBody", "La detección sigue en pausa hasta confirmar o descartar la sesión." },
            { "TrayNotice_PendingAction", "Revisar sesión" },
            { "TrayNotice_UpdateTitle", "Nueva versión disponible" },
            { "TrayNotice_UpdateBody", "Se instala en unos segundos y vuelve a abrirse sola." },
            { "TrayNotice_UpdateAction", "Actualizar" },
            { "TrayNotice_Close", "Cerrar" },
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
            { "Browser_Prompt_Close", "Cerrar" },
            { "Browser_Deps_Title", "Faltan librerías del sistema" },
            { "Browser_Deps_Body", "Chromium está instalado, pero no puede arrancar porque a tu sistema le faltan librerías. Abre una terminal y ejecuta este comando (te pedirá la contraseña de tu usuario de Linux):" },
            { "Browser_Deps_BodyNoApt", "Chromium está instalado, pero no puede arrancar porque a tu sistema le faltan librerías. Instálalas con el gestor de paquetes de tu distribución." },
            { "Browser_Deps_Missing", "Faltan: {0}" },
            { "Browser_Deps_Copy", "Copiar comando" },
            { "Browser_Deps_Copied", "Copiado" },
            { "Browser_Deps_Retry", "Reintentar" }
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
