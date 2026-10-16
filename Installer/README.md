# Installer — установщик CheckMail (MSI)

Каталог содержит источник и скрипты для сборки Windows-установщика (MSI) приложения **CheckMail**.

## Содержимое

| Файл/папка | Описание |
|------------|----------|
| `Product.wxs` | Исходник WiX (описание пакета, файлы, ярлыки, UI). |
| `BuildInstaller.ps1` | Скрипт сборки MSI через WiX Toolset v5. |
| `tools/WixToolset.UI.wixext.dll` | Расширение стандартного интерфейса установки (WixUI_InstallDir), версия 5.0.2. |
| `settings.json` | Настройки по умолчанию (без паролей), устанавливаемые вместе с приложением. **В git не хранится** (см. `.gitignore`). |
| `bin/` | Результат сборки: `CheckMail-<версия>.msi`. Также игнорируется git. |

## Требования

- [WiX Toolset v5](https://wixtoolset.org/) (команда `wix`) — устанавливается как .NET-инструмент:
  ```powershell
  dotnet tool install --global wix --version 5.0.2
  ```
- Исходные файлы приложения в папке `..\Programm` (результат `dotnet publish`).

## Сборка

```powershell
cd Installer
pwsh -NoProfile -ExecutionPolicy Bypass -File .\BuildInstaller.ps1
```

Готовый файл появится здесь: `bin\CheckMail-1.0.0.msi`

### Параметры скрипта

- `-Version 1.1.0` — версия пакета (по умолчанию `1.0.0`).
- `-SourceDir <путь>` — папка с собранными файлами приложения (по умолчанию `..\Programm`).
- `-OutputDir <путь>` — куда сложить MSI (по умолчанию `.\bin`).

## Примечание о версии WiX

Используется **WiX v5.0.2** — последняя версия с лицензией MIT (без «Open Source Maintenance Fee», введённой в v6/v7). Если установить более новую `wix`, потребуется принятие платной лицензии OSMF.
