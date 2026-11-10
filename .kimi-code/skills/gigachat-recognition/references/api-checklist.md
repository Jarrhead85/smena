# GigaChat API checklist

## Конфигурация

- Base URL `https://api.giga.chat/v1`; OAuth endpoint взят из официальной документации.
- Scope PERS/B2B/CORP явно; model выбрана и vision проверен тестом.
- Authorization Key в SecureStorage; access token в памяти с expiry buffer.

## Успешный lifecycle

1. Подготовить JPEG, orientation, target ≤10 МБ, hard limit 15 МБ.
2. OAuth с Basic key, новым RqUID и scope.
3. `POST /files`, `purpose=general`, сохранить remote ID.
4. Chat с одним attachment и низкой temperature, stream=false.
5. Разобрать только JSON по schema 1.0; создать Draft.
6. `POST /files/{id}/delete`; pending при ошибке.

## Ошибки

- Network/timeout/5xx: ≤2 retry с cancellation/jitter.
- 401: один token refresh; далее настройки.
- 403/413/422: не зацикливать; безопасная ошибка.
- 429: учесть Retry-After.
- Invalid JSON: один повтор, затем Failed.
- Cancel: остановить новые страницы и очистить remote file по возможности.

## Security/quality

- TLS всегда включён; нет accept-all и hostname bypass.
- Нет headers/body/PII/secrets в логах.
- Только синтетический contract fixture.
- Метрики на размеченном наборе, не единичное демо.
- Нет Confirm/reminders из результата модели автоматически.
