# Vibe Launcher

<p align="center">
  <img src="Assets/VibeLauncher.png" width="96" alt="Vibe Launcher">
</p>

Русскоязычный лаунчер Minecraft для Windows на WPF (.NET 10). Поддерживает лицензионные
(Microsoft) и офлайн-аккаунты, установку версий и модовых загрузчиков, сборки с Modrinth,
скины (Mojang, Ely.by, TLauncher, локальные файлы) и разбор логов с причинами вылетов.

## Возможности

- **Аккаунты**
  - Вход Microsoft по OAuth device-code (нужен свой Azure Client ID).
  - Офлайн/пиратский аккаунт по никнейму (UUID генерируется детерминированно).
- **Версии и загрузчики**
  - Скачивание ванильных версий Minecraft, ассетов и библиотек.
  - Выбор версии Minecraft из списка релизов Mojang.
  - Автоматическая установка Fabric, Quilt, Forge и NeoForge.
  - Сначала показываются стабильные версии загрузчиков, затем pre-release.
  - Для NeoForge используется официальное зеркало CreeperHost, если основной Maven недоступен.
- **Сборки**
  - Создание отдельных профилей с собственной папкой `mods`, `config`, `saves`.
  - Поиск и установка модов напрямую с Modrinth с учётом версии Minecraft и загрузчика.
  - Импорт сборок в формате `.mrpack` (Modrinth) вместе с overrides.
- **Скины**
  - Загрузка скина по никнейму из Mojang или Ely.by.
  - Импорт скинов и плащей из локальных папок TLauncher.
  - Выбор PNG-файла скина/плаща вручную.
  - Установка `CustomSkinLoader` в сборку и запись скина в
    `CustomSkinLoader/LocalSkin/skins/<ник>.png` и `capes/<ник>.png`.
- **Настройки**
  - Объём оперативной памяти, дополнительные аргументы JVM, размер окна, полноэкранный режим.
  - Выбор и автоматическая загрузка Java (8/17/21).
  - Лаунчер сам подбирает рекомендованную Java для версии игры, даже если в настройках
    указана более новая.
- **Логи и краши**
  - Все запуски сохраняются в `%APPDATA%\VibeLauncher\logs`.
  - Автоматический анализ: нехватка RAM, неподходящая Java, конфликты модов, Mixin,
    ошибки OpenGL, сетевые ошибки, повреждённые библиотеки и т.д.
  - Подхватываются `crash-reports` и `hs_err_pid*.log`.

## Установка

Готовый установщик лежит в [GitHub Releases](https://github.com/S1awwk0/VibeLauncher/releases).
Это self-contained MSI: отдельно ставить .NET Runtime не нужно.

- Устанавливается в `%LOCALAPPDATA%\Programs\VibeLauncher` без прав администратора.
- При установке можно выбрать, создавать ли папку в меню «Пуск» и ярлык на рабочем столе.
- Для тихой установки доступны параметры `STARTMENU_SHORTCUT=0` и `DESKTOP_SHORTCUT=0`.
- Удаляется через «Параметры → Приложения → Vibe Launcher».
- Данные игры и настройки остаются в `%APPDATA%\VibeLauncher` и при удалении не стираются.

## Сборка и запуск

Требования: Windows 10/11, [.NET SDK 10](https://dotnet.microsoft.com/download).

```powershell
cd C:\Users\RobotComp.ru\Documents\Launcherforminecraft
dotnet build -c Release
.\bin\Release\net10.0-windows\VibeLauncher.exe
```

Проект не использует внешние NuGet-пакеты.

## Сборка установщика

Установщик собирается через WiX Toolset 5 (global tool) и требует только .NET SDK:

```powershell
dotnet tool install --global wix --version 5.0.2
powershell -ExecutionPolicy Bypass -File .\installer\build-release.ps1
```

Скрипт публикует self-contained `win-x64` сборку и создаёт
`dist\VibeLauncher-Setup-<версия>-win-x64.msi` вместе с `SHA256SUMS.txt`.

## Ветки

- `main` — стабильные версии и теги релизов.
- `develop` — интеграция изменений.
- `feature/*` — отдельные задачи, после проверки сливаются в `develop`.

## Настройка входа Microsoft

1. Откройте [Azure Portal](https://portal.azure.com) → **Microsoft Entra ID** → **App registrations** → **New registration**.
2. Тип аккаунтов: *Personal Microsoft accounts* (или *Accounts in any organizational directory and personal accounts*).
3. В **Authentication** включите **Allow public client flows**.
4. Скопируйте **Application (client) ID**.
5. В лаунчере откройте **Настройки → Аккаунт Microsoft** и вставьте Client ID, затем нажмите
   **Войти** на главной странице. Лаунчер покажет код и ссылку `microsoft.com/link`.
6. После входа токен обновляется автоматически.

Без Client ID офлайн-режим работает как обычно.

## Где хранятся данные

Всё лежит в `%APPDATA%\VibeLauncher`:

| Папка | Содержимое |
| --- | --- |
| `versions` | Версии Minecraft и профили загрузчиков |
| `libraries` | Библиотеки Java |
| `assets` | Ресурсы и индексы ассетов |
| `instances` | Сборки (`instances.json` + папки сборок) |
| `java` | Скачанные среды Java |
| `logs` | Логи запусков и лог лаунчера |
| `skins` | Кэш скачанных скинов и плащей |
| `cache` | Установщики Forge/NeoForge и прочий кэш |

## Заметки

- **Java.** Для Minecraft 1.20.5+ нужна Java 21, для 1.18–1.20.4 — Java 17, для старых версий — Java 8.
  Лаунчер выбирает рекомендованную версию автоматически; более новая Java (например 25)
  может ломать NeoForge.
- **Forge/NeoForge.** Установка выполняется официальным установщиком в режиме `--installClient`.
  Первый запуск может занять несколько минут.
- **TLauncher.** Публичного API скинов у TLauncher нет, поэтому лаунчер импортирует файлы
  из локальных папок TLauncher (`.minecraft/TLauncher/skin`, `cape`) или позволяет выбрать PNG вручную.
- **Ely.by.** Скин загружается по никнейму через `skinsystem.ely.by`. Если у игрока нет текстур
  в Ely.by, возвращается ошибка «скин не найден» — это нормальное поведение сервиса.
- **Офлайн-скины.** Чтобы скин отображался в офлайн-игре, установите `CustomSkinLoader` в сборку
  (кнопка на вкладке «Скины») и примените скин к этой сборке.
