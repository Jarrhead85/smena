# Промпт: импорт и GigaChat

Используй `gigachat-recognition`, `nurse-shift-domain` и `clinical-ui-review`. Прочитай документы импорта, GigaChat, UX, данных и безопасности, а также оба файла контрактов.

Реализуй этап небольшими частями:

1. Настройки ключа/scope/model через SecureStorage и проверка подключения без PII.
2. Camera/gallery, несколько страниц, rotate/reorder/delete и app-private temp storage.
3. OAuth, token cache, upload, chat, delete/pending delete, timeout/retry/cancellation без чувствительных логов.
4. Строгий parser по JSON Schema и маппинг только в Draft.
5. Review UI с исходным фото, ошибками, редактированием и unparsed lines.
6. Атомарный идемпотентный Confirm; reminders только после подтверждения; cleanup фото.
7. Восстановление черновика после process death.

Сначала используй фиктивный `IRecognitionService` для UI, затем реальный REST на синтетическом тестовом изображении. Не отключай TLS и не записывай ключ в проект. Запусти contract/integration tests и Android build. Обнови ADR с фактически выбранной моделью только после измерения качества.
