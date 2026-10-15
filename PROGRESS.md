# NOVA TUNE — PROGRESS

## Статус
Этап 1 (окружение и каркас) — каркас создан, работа приостановлена пользователем.

## Сделано
- .NET SDK 10.0.402 (LTS); WPF собирается кросс-компиляцией (`EnableWindowsTargeting`) в self-contained single-file win-x64 EXE.
- Решение: `src/NovaTune.Core` (net10.0, логика), `src/NovaTune.Platform` (Windows-реализации), `src/NovaTune.App` (WPF, пока шаблон), `tests/NovaTune.Core.Tests` (xUnit).

## Решения
- LibreHardwareMonitorLib 0.9.6 (MPL-2.0) использует драйвер PawnIO (не WinRing0); драйвер не входит в пакет — температура CPU только при установленном PawnIO и правах администратора.
- Бинарники в git не коммитятся (>100 МБ); релизная сборка — через CI на Windows.

## Ограничения среды
Разработка идёт в облачном Linux-контейнере: реальные WMI/датчики/UAC/внешний вид на Windows здесь не проверяются.

## Следующий шаг
Дизайн-система и Core-сервисы (бэкап/транзакции, VDF, ISystemSettingsStore).
