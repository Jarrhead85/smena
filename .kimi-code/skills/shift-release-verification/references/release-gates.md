# Release gates

## Gate 1 — сборка и тесты

- Unit/integration/contract passed.
- Android Release build passed.
- Нет непринятых high/critical dependency findings.
- Нет секретов/PII в git, APK/AAB, fixtures и logs.

## Gate 2 — домен и данные

- AT-001…AT-018 passed.
- Confirm atomic/idempotent; no reminders before Confirm.
- Offline, process death, night shift, timezone, status transitions passed.
- Migrations, retention и full delete passed.

## Gate 3 — notifications

- Advance/Due/cancel/resync/deep link/Done passed.
- Reboot/time/timezone/permission/package replaced passed.
- Denied/revoked visible; no silent inexact fallback.
- Контрольный 20/20 run в заявленных условиях; Doze задержки измерены.

## Gate 4 — GigaChat

- OAuth/upload/chat/parse/delete lifecycle passed.
- Timeout/retry/cancel/429/401/TLS/invalid JSON passed.
- Remote pending delete survives restart.
- Качество модели измерено на размеченном обезличенном наборе.

## Gate 5 — UX/security

- Ключевые экраны: 360/412, light/dark, 200%, TalkBack, long text.
- Нет dead controls и blocker accessibility issues.
- SQLCipher/private files/backup/FLAG_SECURE/public notifications/log audit passed.
- Известные ограничения и непроверенное явно записаны.

## Gate 6 — реальный пилот

- Организационное согласование, целевые устройства, установка, поддержка и инструкции утверждены.
- Без Gate 6 статус только технический прототип/MVP, не разрешение на работу с реальными данными.
