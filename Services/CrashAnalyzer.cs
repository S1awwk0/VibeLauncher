using System.Text.RegularExpressions;
using VibeLauncher.Models;

namespace VibeLauncher.Services;

public static class CrashAnalyzer
{
    public static List<CrashFinding> Analyze(string log, int exitCode, string? gameDir = null,
        DateTime? since = null)
    {
        var findings = new List<CrashFinding>();
        var lower = log.ToLowerInvariant();

        void Add(string severity, string title, string cause, string fix, string? evidence = null)
        {
            if (findings.Any(f => f.Title == title)) return;
            findings.Add(new CrashFinding
            {
                Severity = severity,
                Title = title,
                Cause = cause,
                Fix = fix,
                Evidence = Truncate(evidence)
            });
        }

        var crashDescription = Match(log, @"Description:\s*(.+)");
        var causedBy = Match(log, @"Caused by:\s*(.+)");
        var crashedWhilst = Match(log, @"The game crashed whilst ([^\r\n]+)");

        // ── Нехватка памяти ──
        if (lower.Contains("java.lang.outofmemoryerror") || lower.Contains("java heap space") ||
            lower.Contains("out of memory"))
        {
            Add("critical", "Недостаточно оперативной памяти",
                "Игре или модам не хватило выделенной памяти Java.",
                "Увеличьте объём RAM в настройках лаунчера (для сборок с модами рекомендуется 6–10 ГБ) и закройте лишние программы.",
                FindLine(log, "OutOfMemoryError", "Java heap space"));
        }

        // ── Неподходящая Java ──
        if (lower.Contains("unsupportedclassversionerror") || lower.Contains("class file version") ||
            lower.Contains("has been compiled by a more recent version") ||
            Regex.IsMatch(lower, @"requires (java|jdk) (\d+)"))
        {
            Add("critical", "Неподходящая версия Java",
                "Версия Java не совместима с игрой или модом.",
                "Проверьте версию в настройках: Minecraft 1.20.5+ требует Java 21, 1.18–1.20.4 — Java 17, 1.17 — Java 16, старые версии — Java 8. Установите нужную Java и укажите путь к ней.",
                FindLine(log, "UnsupportedClassVersionError", "class file version", "compiled by a more recent"));
        }

        if (lower.Contains("error: could not create the java virtual machine") ||
            lower.Contains("invalid maximum heap size") || lower.Contains("initial heap size"))
        {
            Add("critical", "Java не может запуститься с такими параметрами",
                "Слишком большой объём RAM или некорректные аргументы JVM.",
                "Уменьшите выделенную память, проверьте параметры запуска в настройках и убедитесь, что установлена 64-битная Java.",
                FindLine(log, "Invalid maximum heap size", "Could not create the Java Virtual Machine"));
        }

        // ── Конфликты модов ──
        if (lower.Contains("mixin apply failed") || lower.Contains("mixintransformererror") ||
            lower.Contains("mixin apply error") || lower.Contains("mixinthrowables"))
        {
            Add("critical", "Ошибка Mixin (конфликт модов)",
                "Один из модов не совместим с версией игры, загрузчиком или другим модом.",
                "Уберите недавно добавленные моды, проверьте совместимость с версией Minecraft и загрузчиком. Часто виноват устаревший OptiFine или мод для другой версии.",
                FindLine(log, "Mixin apply failed", "MixinTransformerError", "MixinApplyError"));
        }

        if (lower.Contains("modresolutionexception") || lower.Contains("incompatible mod set") ||
            lower.Contains("incompatible mods found") || lower.Contains("mod resolution failed") ||
            lower.Contains("hard_dep_incompatible") ||
            lower.Contains("missing or unsupported mandatory dependencies") ||
            lower.Contains("missing mod") || lower.Contains("mod requires") ||
            lower.Contains("wrong version is present"))
        {
            Add("critical", "Несовместимые или отсутствующие моды",
                "Fabric/Forge сообщает о конфликте версий либо отсутствующей зависимости.",
                "Прочитайте список зависимостей в логе. Установите указанные моды (Fabric API, Cloth Config, Architectury и т.п.) и удалите дублирующиеся или несовместимые версии. Убедитесь, что мод собран именно для вашей версии Minecraft и загрузчика.",
                FindLine(log, "ModResolutionException", "Incompatible mods found", "Mod resolution failed",
                    "requires any version between", "requires version", "depends on"));
        }

        if (lower.Contains("java.lang.module.resolutionexception") ||
            (lower.Contains("export package") && lower.Contains("module")))
        {
            Add("critical", "Конфликт модулей Java",
                "Версия Java несовместима с загрузчиком модов либо в classpath попала лишняя копия игры.",
                "Установите Java, рекомендованную для версии Minecraft (1.20.5+ — Java 21), и переустановите версию с загрузчиком модов.",
                FindLine(log, "ResolutionException", "export package"));
        }

        if (lower.Contains("nosuchmethoderror") || lower.Contains("nosuchfielderror") ||
            lower.Contains("abstractmethoderror") || lower.Contains("illegalaccesserror"))
        {
            Add("critical", "Мод собран для другой версии",
                "Мод обращается к методам или полям, которых нет в этой версии Minecraft или загрузчика.",
                "Удалите мод из папки mods либо обновите его до версии, совместимой с вашей версией игры и загрузчиком.",
                FindLine(log, "NoSuchMethodError", "NoSuchFieldError", "AbstractMethodError", "IllegalAccessError"));
        }

        if (lower.Contains("duplicatemodsfoundexception") || lower.Contains("duplicate mod"))
        {
            Add("warning", "Дубликаты модов",
                "В папке mods лежат две версии одного и того же мода.",
                "Удалите старую версию мода из папки mods сборки и перезапустите игру.",
                FindLine(log, "DuplicateModsFoundException", "Duplicate mod"));
        }

        if (lower.Contains("is not a valid mod file") || lower.Contains("invalid mod file"))
        {
            Add("warning", "Повреждённый файл мода",
                "Один из jar-файлов скачан не полностью или не является модом.",
                "Удалите повреждённый мод из папки mods и скачайте его заново через вкладку «Сборки».",
                FindLine(log, "is not a valid mod file", "Invalid mod file"));
        }

        // ── Графика / драйверы ──
        if (lower.Contains("glfw error 65542") || lower.Contains("wgl: the driver does not appear") ||
            lower.Contains("pixel format not accelerated") || lower.Contains("failed to create window") ||
            lower.Contains("failed to create opengl") || lower.Contains("could not create opengl") ||
            lower.Contains("opengl error") || lower.Contains("opengl is not supported") ||
            lower.Contains("requires opengl"))
        {
            Add("critical", "Проблема с графикой (OpenGL)",
                "Драйвер видеокарты не поддерживает нужную версию OpenGL или игра запускается через слабую видеокарту.",
                "Обновите драйверы NVIDIA/AMD/Intel, не запускайте игру через RDP, проверьте, что используется дискретная видеокарта. Для старых ПК попробуйте моды Sodium/Embeddium.",
                FindLine(log, "GLFW error", "Pixel format not accelerated", "OpenGL"));
        }

        // ── Крэш JVM / нативные ошибки ──
        if (lower.Contains("exception_access_violation") || lower.Contains("sigsegv") ||
            lower.Contains("a fatal error has been detected by the java runtime environment") ||
            exitCode == -1073741819)
        {
            Add("critical", "Аварийное завершение Java (access violation)",
                "Нативный сбой: конфликт модов, проблема с видеодрайвером или повреждённая библиотека.",
                "Попробуйте убрать недавно установленные моды, обновить драйвер видеокарты и Java. Файл hs_err_pid*.log в папке сборки содержит точную причину.",
                FindLine(log, "EXCEPTION_ACCESS_VIOLATION", "SIGSEGV", "A fatal error has been detected"));
        }

        if (exitCode == -1073741502)
        {
            Add("critical", "Ошибка инициализации DLL",
                "Системе не удалось загрузить зависимость Java или нативной библиотеки.",
                "Переустановите Java, проверьте антивирус и целостность файлов игры.",
                "Код выхода 0xC0000142");
        }

        // ── Библиотеки / classpath ──
        if (lower.Contains("noclassdeffounderror") || lower.Contains("classnotfoundexception") ||
            lower.Contains("could not find or load main class"))
        {
            Add("critical", "Повреждённая установка или нехватка библиотек",
                "Не найден класс или библиотека: файлы версии загружены не полностью либо мод не совместим с загрузчиком.",
                "Переустановите версию на вкладке «Версии», удалите конфликтующий мод, проверьте, что Fabric API / Forge-библиотеки установлены.",
                FindLine(log, "NoClassDefFoundError", "ClassNotFoundException", "Could not find or load main class"));
        }

        if (lower.Contains("java.lang.verifyerror"))
        {
            Add("warning", "Ошибка проверки байткода (VerifyError)",
                "Мод собран для другой версии Java или повреждён.",
                "Обновите мод или используйте Java, рекомендованную для вашей версии Minecraft.",
                FindLine(log, "VerifyError"));
        }

        // ── Авторизация / сеть ──
        if (lower.Contains("invalid session") || lower.Contains("failed to verify username") ||
            lower.Contains("authentication servers are down"))
        {
            Add("warning", "Проблема с авторизацией",
                "Сессия аккаунта недействительна либо серверы авторизации Mojang недоступны.",
                "Перезайдите в аккаунт Microsoft. Для офлайн-режима используйте сервер с online-mode=false.",
                FindLine(log, "Invalid session", "Failed to verify username", "Authentication servers are down"));
        }

        if (lower.Contains("unknownhostexception") || lower.Contains("connection refused") ||
            lower.Contains("connect timed out"))
        {
            Add("warning", "Сетевая ошибка",
                "Игра не смогла подключиться к серверу или сервису.",
                "Проверьте интернет, VPN/прокси и доступность сервера. Если ошибка при старте — возможно, не скачались ресурсы.",
                FindLine(log, "UnknownHostException", "Connection refused", "Connect timed out"));
        }

        // ── Парсинг crash-report ──
        if (!string.IsNullOrWhiteSpace(crashDescription) || !string.IsNullOrWhiteSpace(crashedWhilst))
        {
            var title = "Краш игры: " + (crashedWhilst ?? crashDescription ?? "").Trim();
            Add("critical", Truncate(title, 120)!,
                causedBy ?? crashDescription ?? "Игра аварийно завершилась.",
                "Изучите полный crash-report в папке сборки (crash-reports). Там указан конкретный мод/класс, вызвавший сбой.",
                causedBy);
        }

        // ── Общие коды выхода ──
        if (findings.Count == 0 && exitCode != 0)
        {
            var hint = exitCode switch
            {
                1 => "Игра завершилась с ошибкой. Причина обычно указана выше в логе — ищите строки «Exception», «Caused by» или «ERROR».",
                255 => "Код 255 часто означает краш при инициализации модов или неверные параметры запуска.",
                _ => "Игра завершилась аварийно. Проверьте лог и crash-report."
            };

            Add("warning", $"Игра вылетела (код {exitCode})",
                "Лаунчер не нашёл известного шаблона ошибки, но игра завершилась с ненулевым кодом.",
                hint + " Попробуйте удалить недавно установленные моды, переустановить версию или увеличить RAM.",
                LastMeaningfulLine(log));
        }

        if (findings.Count == 0 && exitCode == 0)
        {
            Add("info", "Игра завершилась нормально",
                "Код выхода 0 — ошибок при запуске не обнаружено.",
                "Если игра всё равно закрылась, проверьте, не был ли использован Alt+F4 или завершение процесса.");
        }

        if (gameDir != null && since != null)
        {
            var crashDir = Path.Combine(gameDir, "crash-reports");
            if (Directory.Exists(crashDir))
            {
                try
                {
                    var report = Directory.EnumerateFiles(crashDir, "*.txt")
                        .Select(p => new FileInfo(p))
                        .Where(f => f.LastWriteTime >= since.Value.AddMinutes(-2))
                        .OrderByDescending(f => f.LastWriteTime)
                        .FirstOrDefault();
                    if (report != null && findings.All(f => !f.Title.StartsWith("Краш игры")))
                    {
                        var text = File.ReadAllText(report.FullName);
                        Add("critical", "Найден crash-report: " + report.Name,
                            Match(text, @"Description:\s*(.+)") ?? "Игра сохранила отчёт о краше.",
                            "Откройте файл crash-report, чтобы увидеть точный стек вызовов и виновный мод.",
                            Match(text, @"Caused by:\s*(.+)"));
                    }
                }
                catch { }
            }
        }

        return findings;
    }

    private static string? Match(string text, string pattern)
    {
        try
        {
            var m = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value.Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? FindLine(string text, params string[] markers)
    {
        foreach (var line in text.Split('\n'))
        {
            if (markers.Any(m => line.Contains(m, StringComparison.OrdinalIgnoreCase)))
                return Truncate(line.Trim(), 400);
        }
        return null;
    }

    private static string? LastMeaningfulLine(string log)
    {
        var lines = log.Split('\n');
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i].Trim();
            if (line.Length > 3 && !line.StartsWith("#") && !line.StartsWith("="))
                return Truncate(line, 400);
        }
        return null;
    }

    private static string? Truncate(string? text, int max = 600)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        return text.Length <= max ? text : text[..max] + "…";
    }
}
