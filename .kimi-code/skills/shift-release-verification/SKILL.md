---
name: shift-release-verification
description: Независимая проверка готовности приложения «Смена». Использовать для приёмки, traceability FR→код→тест, Android Release build, device tests напоминаний, офлайн, process death, безопасности, accessibility и честного статуса release gates.
---

# Release verification

## Порядок

1. Прочитай `docs/10_Тестирование_и_приёмка.md` и связанные нормативные документы.
2. Составь traceability: каждый применимый FR → реализация → автоматический/ручной тест → фактический результат.
3. Запусти unit/integration/contract tests и Release build; сохрани коды результата.
4. Проверь AT-001…AT-018, критические notification, offline, process death и security сценарии.
5. Разделяй «пройдено», «не пройдено», «не запускалось». Последнее не считается успехом.
6. Сообщи findings по серьёзности и вынеси однозначный статус готовности.

## Автоматические блокеры

- Подтверждённые назначения или reminders появляются до явного Confirm.
- Потеря/дублирование данных при Confirm, process death или повторном действии.
- План/статусы не работают офлайн.
- Overdue автоматически становится Skipped.
- Есть TLS bypass, PII/секреты в logs/Intent/public notification или реальные данные в fixtures.
- Будущие alarms не отменяются при Done/Skipped/delete/complete.
- Отсутствие permission скрыто; UI заявляет включённые напоминания при Failed.
- Release build или критический тест падает.
- Видимые мёртвые controls или критический accessibility failure.

## Evidence

Для device tests указать модель/API/версию, permissions, battery mode, сценарий и фактическое время. Для GigaChat — только синтетический fixture, статусы lifecycle и delete evidence без секретов/PII. Для UI — состояния, viewport/font/theme и фактические observations.

## Формат отчёта

```text
Статус: готов к техническому MVP / не готов
Пройдено: [ID + evidence]
Провалено: [ID + severity + воспроизведение]
Не проверено: [ID + причина + открытый gate]
Безопасность: [результат]
Документация: [пробелы/обновления]
```

Не исправляй найденные архитектурные дефекты незаметно в процессе ревью. Сначала опиши их и объём исправления.

Справочник: [references/release-gates.md](references/release-gates.md).
