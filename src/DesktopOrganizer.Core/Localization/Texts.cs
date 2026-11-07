using System.Globalization;

namespace DesktopOrganizer.Core.Localization;

/// <summary>
/// User-visible text in Russian (the source language, used as the key) and English. Every layer calls
/// <see cref="T"/> / <see cref="F"/>; a missing English entry falls back to Russian and is caught by a test.
/// </summary>
public static class Texts
{
    public const string Russian = "ru", English = "en";
    private static volatile bool _english;
    public static string Language => _english ? English : Russian;
    public static event Action? LanguageChanged;

    /// <summary>"ru" or "en"; anything else follows the Windows display language.</summary>
    public static string Normalize(string? language) => language switch
    {
        Russian or English => language,
        _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName is "ru" or "be" or "uk" or "kk" ? Russian : English,
    };
    public static void SetLanguage(string? language)
    {
        var english = Normalize(language) == English;
        if (english == _english) return;
        _english = english;
        LanguageChanged?.Invoke();
    }
    public static string T(string russian) => _english && Table.TryGetValue(russian, out var english) ? english : russian;
    public static string F(string russian, params object?[] args) => string.Format(CultureInfo.CurrentCulture, T(russian), args);
    public static bool HasTranslation(string russian) => Table.ContainsKey(russian);

    private static readonly Dictionary<string, string> Table = new(StringComparer.Ordinal)
    {
        // Main window
        ["Свернуть окно"] = "Minimize window",
        ["Развернуть окно"] = "Maximize window",
        ["Скрыть в трей"] = "Hide to tray",
        ["⚙   Настройки"] = "⚙   Settings",
        ["▦   Fences"] = "▦   Fences",
        ["Место для важного.\nПорядок для остального."] = "Room for what matters.\nOrder for the rest.",
        ["DesktopOrganizer — группы"] = "DesktopOrganizer — groups",
        ["Рабочий стол подключён"] = "Desktop connected",
        ["Подключение к рабочему столу…"] = "Connecting to the desktop…",
        // Groups page
        ["01 / РАБОЧИЙ СТОЛ"] = "01 / DESKTOP",
        ["Группы рабочего стола"] = "Desktop groups",
        ["Всё важное — на своём месте."] = "Everything important in its place.",
        ["＋  Новая группа"] = "＋  New group",
        ["Групп: "] = "Groups: ",
        ["Пока здесь пусто.\nСоздайте первую группу."] = "Nothing here yet.\nCreate your first group.",
        ["Значков: "] = "Icons: ",
        ["РАЗВЁРНУТА"] = "EXPANDED",
        ["СВЁРНУТА"] = "COLLAPSED",
        ["К рабочему столу ↗"] = "Show desktop ↗",
        ["Группа {0}"] = "Group {0}",
        ["Загружено групп: {0}. Изменения сохраняются автоматически."] = "Groups loaded: {0}. Changes are saved automatically.",
        ["Сохранено групп: {0}. {1:T}"] = "Groups saved: {0}. {1:T}",
        ["Изменение на рабочем столе. Сохранение после отпускания мыши."] = "Changing on the desktop. Saved when you release the mouse.",
        ["Изменение отменено."] = "Change cancelled.",
        ["Предпросмотр. Изменения сохраняются автоматически."] = "Preview. Changes are saved automatically.",
        ["Ширина"] = "Width",
        ["Высота"] = "Height",
        ["{0}: введите конечное число."] = "{0}: enter a finite number.",
        // Group appearance
        ["ОФОРМЛЕНИЕ ГРУППЫ"] = "GROUP APPEARANCE",
        ["Выберите группу"] = "Select a group",
        ["Заголовок"] = "Title",
        ["Название"] = "Name",
        ["Шрифт"] = "Font",
        ["Размер"] = "Size",
        ["От 8 до 28; высота заголовка остаётся прежней"] = "8 to 28; the title bar height stays the same",
        ["Жирный"] = "Bold",
        ["Курсив"] = "Italic",
        ["Выравнивание"] = "Alignment",
        ["Слева"] = "Left",
        ["По центру"] = "Center",
        ["Справа"] = "Right",
        ["Цвет текста"] = "Text color",
        ["Панель заголовка"] = "Title bar",
        ["Цвет"] = "Color",
        ["Непрозрачность: "] = "Opacity: ",
        ["Основной фон"] = "Background",
        ["Рамка"] = "Border",
        ["Изменения видны на рабочем столе и сохраняются автоматически."] = "Changes appear on the desktop and are saved automatically.",
        ["Авто-сортировка"] = "Auto-sort",
        ["Новые файлы на рабочем столе с подходящим именем сами попадут в эту группу. Через ; или пробел: *.png; *.lnk; Steam*"] =
            "New desktop files with a matching name join this group automatically. Separate with ; or spaces: *.png; *.lnk; Steam*",
        ["Сохранить правила"] = "Save rules",
        ["Удалить группу"] = "Delete group",
        ["Авто-сортировка выключена."] = "Auto-sort is off.",
        ["Новые подходящие файлы будут попадать в эту группу."] = "New matching files will join this group.",
        ["Размер шрифта: введите число от 8 до 28."] = "Font size: enter a number from 8 to 28.",
        // Color picker
        ["Выбрать цвет"] = "Choose color",
        ["Открыть палитру"] = "Open palette",
        ["ПАЛИТРА"] = "PALETTE",
        ["Оттенок"] = "Hue",
        ["Точный цвет HEX"] = "Exact HEX color",
        ["Используйте #RRGGBB"] = "Use #RRGGBB",
        // Settings page
        ["02 / ПРЕДПОЧТЕНИЯ"] = "02 / PREFERENCES",
        ["Настройки"] = "Settings",
        ["Ваш рабочий стол. Ваш привычный ритм."] = "Your desktop. Your rhythm.",
        ["ЯЗЫК"] = "LANGUAGE",
        ["Язык интерфейса"] = "Interface language",
        ["Применяется сразу: окно, меню в трее и сообщения."] = "Applies immediately: window, tray menu and messages.",
        ["ГРУППЫ НА РАБОЧЕМ СТОЛЕ"] = "DESKTOP GROUPS",
        ["Раскрывать свёрнутый Fence при наведении"] = "Expand a collapsed group on hover",
        ["Раскрытие и сворачивание без дополнительной задержки. После ухода курсора группа вернётся в своё свёрнутое состояние."] =
            "Expands and collapses without extra delay. When the pointer leaves, the group returns to its collapsed state.",
        ["АНИМАЦИЯ ГРУПП"] = "GROUP ANIMATION",
        ["Плавное раскрытие и сворачивание"] = "Smooth expand and collapse",
        ["Раскрытие · секунды"] = "Expand · seconds",
        ["Сворачивание · секунды"] = "Collapse · seconds",
        ["От 0 до 3 секунд. 0 — мгновенно. Изменение сохраняется после выхода из поля и применяется к кнопке в заголовке и hover-раскрытию."] =
            "0 to 3 seconds. 0 is instant. Saved when you leave the field; applies to the title button and hover expand.",
        ["ЗАПУСК И ФОНОВАЯ РАБОТА"] = "STARTUP AND BACKGROUND",
        ["Запуск вместе с Windows"] = "Start with Windows",
        ["Запускать DesktopOrganizer вместе с Windows"] = "Start DesktopOrganizer with Windows",
        ["Автоматически запускать DesktopOrganizer в трее при входе в Windows. Только для вашего пользователя, без прав администратора."] =
            "Start DesktopOrganizer in the tray when you sign in to Windows. Only for your account, no administrator rights.",
        ["ПОВЕДЕНИЕ ОКНА"] = "WINDOW BEHAVIOR",
        ["Порядок остаётся, даже когда окно закрыто."] = "The order stays even when the window is closed.",
        ["Крестик скрывает окно в системный трей. Группы продолжают работать на рабочем столе. Один клик по значку возвращает это окно."] =
            "The close button hides the window to the system tray. Groups keep working on the desktop. One click on the tray icon brings this window back.",
        ["Чтобы полностью завершить приложение, выберите «Выход» в меню значка в трее."] = "To quit completely, choose “Exit” in the tray icon menu.",
        ["Немного порядка.\nБольше пространства."] = "A little order.\nMore space.",
        ["Не удалось прочитать или сохранить настройки. "] = "Could not read or save settings. ",
        ["Не удалось изменить или прочитать автозапуск. "] = "Could not change or read the startup setting. ",
        ["Длительность должна быть от 0 до 3 секунд."] = "Duration must be between 0 and 3 seconds.",
        ["Настройки недоступны или повреждены. Исходный settings.json сохранён; исправьте файл и откройте настройки снова."] =
            "Settings are unavailable or damaged. The original settings.json is kept; fix the file and open Settings again.",
        // Tray and lifetime
        ["Открыть DesktopOrganizer"] = "Open DesktopOrganizer",
        ["Выход"] = "Exit",
        ["Не удалось загрузить иконку трея."] = "Could not load the tray icon.",
        ["Не удалось добавить значок DesktopOrganizer в трей."] = "Could not add the DesktopOrganizer icon to the tray.",
        ["Не удалось настроить события значка DesktopOrganizer."] = "Could not set up the DesktopOrganizer tray icon events.",
        ["Не удалось сохранить оформление группы. Повторите выход после устранения ошибки записи."] =
            "Could not save the group appearance. Fix the write error and exit again.",
        ["Не удалось завершить работу безопасно. Приложение продолжает работать.\n"] = "Could not exit safely. The application keeps running.\n",
        ["Дождитесь завершения перемещения значков и повторите выход."] = "Wait until icons finish moving, then exit again.",
        ["Дождитесь завершения изменения Fence и повторите выход."] = "Wait until the group change finishes, then exit again.",
        ["Сначала закройте DesktopOrganizer через трей → «Выход»: он сам вернёт скрытые ярлыки."] =
            "First close DesktopOrganizer from the tray → “Exit”: it restores hidden icons itself.",
        ["DesktopOrganizer уже запущен для этой папки данных."] = "DesktopOrganizer is already running for this data folder.",
        ["Скрытые ярлыки возвращены: {0}. Отсутствуют на диске: {1}."] = "Hidden icons restored: {0}. Missing on disk: {1}.",
        ["Возвращено: {0}. Не удалось вернуть {1}:\n{2}"] = "Restored: {0}. Could not restore {1}:\n{2}",
        ["Не удалось запустить DesktopOrganizer.\n{0}"] = "DesktopOrganizer could not start.\n{0}",
        // Desktop status
        ["Отключите «Упорядочить значки автоматически» на рабочем столе для группировки иконок."] =
            "Turn off “Auto arrange icons” on the desktop to group icons.",
        ["Не удалось прочитать {0} из {1} иконок: {2}"] = "Could not read {0} of {1} icons: {2}",
        ["Сетка Explorer временно отключена; восстановится при закрытии приложения."] =
            "Explorer's grid is temporarily off; it is restored when the application closes.",
        // Startup registration
        ["Требуется абсолютный путь к исполняемому файлу приложения."] = "An absolute path to the application executable is required.",
        ["Путь слишком длинный для Windows Run. Переместите приложение в папку с более коротким путём."] =
            "The path is too long for Windows Run. Move the application to a folder with a shorter path.",
        ["Исполняемый файл приложения не найден."] = "The application executable was not found.",
        ["Windows не сохранила настройку автозапуска."] = "Windows did not save the startup setting.",
        // Storage
        ["Ожидался массив групп."] = "An array of groups was expected.",
        ["Группа не может быть null."] = "A group cannot be null.",
        ["Идентификаторы групп должны быть уникальными."] = "Group identifiers must be unique.",
        ["Иконка не может принадлежать нескольким группам."] = "An icon cannot belong to several groups.",
        ["Группы должны иметь уникальные идентификаторы."] = "Groups must have unique identifiers.",
        ["Не удалось прочитать группы. Изменения заблокированы, чтобы сохранить исходный файл. Проверьте доступ и перезапустите приложение."] =
            "Could not read the groups. Changes are blocked to keep the original file. Check access and restart the application.",
        ["Не удалось сохранить изменения. Предыдущее состояние сохранено. Проверьте доступ к папке и свободное место, затем повторите действие."] =
            "Could not save changes. The previous state is kept. Check folder access and free space, then try again.",
        ["Файл групп повреждён. Исходный файл сохранён: {0}. Можно создавать новые группы."] =
            "The groups file is damaged. The original file is kept: {0}. You can create new groups.",
        ["Файл групп повреждён, и его резервную копию создать не удалось. Изменения заблокированы. Проверьте доступ и перезапустите приложение."] =
            "The groups file is damaged and could not be backed up. Changes are blocked. Check access and restart the application.",
        // Model validation
        ["Не более {0} правил авто-сортировки."] = "At most {0} auto-sort rules.",
        ["Неверное правило авто-сортировки «{0}». Пример: *.png; *.lnk; Steam*"] = "Invalid auto-sort rule “{0}”. Example: *.png; *.lnk; Steam*",
        ["Слишком много правил авто-сортировки."] = "Too many auto-sort rules.",
        ["Недопустимый идентификатор desktop icon."] = "Invalid desktop icon identifier.",
        ["Недопустимое имя desktop icon."] = "Invalid desktop icon name.",
        ["Идентификатор группы не может быть пустым."] = "The group identifier cannot be empty.",
        ["Название должно содержать от 1 до 100 символов без управляющих символов."] = "The name must be 1 to 100 characters without control characters.",
        ["Координаты должны быть конечными числами."] = "Coordinates must be finite numbers.",
        ["Ширина и высота должны быть конечными числами больше нуля."] = "Width and height must be finite numbers greater than zero.",
        ["Назначения иконок должны иметь уникальные идентификаторы."] = "Icon assignments must have unique identifiers.",
        ["Шрифт: укажите название от 1 до 100 символов."] = "Font: enter a name of 1 to 100 characters.",
        ["Размер шрифта: от 8 до 28. Высота заголовка фиксирована."] = "Font size: 8 to 28. The title bar height is fixed.",
        ["Неизвестное выравнивание текста."] = "Unknown text alignment.",
        ["Цвет: введите HEX в формате #RRGGBB."] = "Color: enter HEX as #RRGGBB.",
        ["Непрозрачность должна быть от 0 до 100%."] = "Opacity must be between 0 and 100%.",
    };
}
