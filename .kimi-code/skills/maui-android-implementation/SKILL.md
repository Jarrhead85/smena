---
name: maui-android-implementation
description: Реализация Android-приложения «Смена» на C#/.NET 10 MAUI. Использовать для MVVM/XAML, SQLite/SQLCipher, DI, камеры, защищённого хранения, локальных AlarmManager-напоминаний, receivers, offline-работы и Android device testing.
---

# .NET MAUI Android implementation

## Порядок работы

1. Прочитай `docs/05_Архитектура.md`, `docs/12_Стандарты_кода.md` и документ своей функции.
2. Проверь существующий проект и зависимости; не создавай параллельную архитектуру.
3. Определи тип изменения:
   - домен/БД → Core/use case, миграция, тесты;
   - экран → resources/styles, ViewModel, состояния, accessibility;
   - Android → интерфейс Core + реализация в `Platforms/Android` + device test;
   - внешний API → Infrastructure DTO/client + sanitised errors + contract tests.
4. Покажи небольшой план и реализуй вертикальный срез.
5. Запусти релевантные тесты и Android build. Не называй device-функцию проверенной без фактического запуска.

## Обязательные ограничения

- Только `net10.0-android`, стабильные пакеты; Core не зависит от MAUI/Android/HTTP/EF.
- `CancellationToken` сквозной; нет `.Result`, `.Wait()` и небезопасного `async void`.
- Время через `IClock`; UTC + DueLocal + TimeZoneId.
- Короткие DbContext scopes, явные транзакции и миграции.
- Платформенные receivers загружают актуальные данные по ID; Intent extras без PII.
- PendingIntent явные и Immutable; request codes стабильны.
- Нет TLS bypass, секретов в ресурсах, verbose HTTP logs и preview packages.
- Нет мёртвых UI controls и статичных экранов вместо функций.

## Напоминания

Прочитай `docs/08_Напоминания_и_офлайн.md` до кода. Реализуй единый scheduler, проверку разрешений, cancel/resync и обработку boot/time/timezone/permission changes. WorkManager не заменяет точный alarm. Отказ разрешения — постоянный видимый warning, а не молчаливый inexact fallback.

## UI

Прочитай UI kit до XAML. Используй токены, CollectionView virtualization, русские ресурсы, 48 dp targets, системный font scaling и TalkBack. Добавь empty/loading/error/offline/permission states, где применимо.

## Завершение

Отчёт: изменённые файлы; фактически выполненные команды/сценарии; невыполненные device checks; оставшиеся риски; обновлённые документы.

Чек-лист: [references/implementation-checklist.md](references/implementation-checklist.md).
