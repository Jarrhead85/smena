---
name: gigachat-recognition
description: Интеграция GigaChat REST API для распознавания листов назначений. Использовать для OAuth, Authorization Key, token cache, upload/attachments/chat/delete, image preprocessing, JSON Schema, prompt, parsing, retry, cancellation, оценки качества и безопасности медицинских данных.
---

# GigaChat recognition

## Порядок работы

1. Прочитай `docs/07_GigaChat_и_распознавание.md`, `docs/09_Безопасность_и_данные.md` и текущие контракты проекта.
2. Сверь актуальную официальную документацию перед изменением endpoint, полей, лимитов или модели.
3. Сначала реализуй/проверь parser и client на синтетических fixtures, затем выполни реальный contract test без медицинских данных.
4. Проведи каждую страницу через полный lifecycle: prepare → OAuth → upload → chat → validate → Draft → delete.
5. Проверь ошибку и отмену, не только успешный путь.

## Жёсткие правила

- Authorization Key только из SecureStorage; access token только в памяти.
- `RqUID` — новый GUID; scope явный; токен кэшируется с запасом истечения.
- `purpose="general"`; один лист на операцию; один image attachment на message.
- Целевой JPEG ≤10 МБ; файл >15 МБ не отправлять.
- TLS verification никогда не отключать.
- Не логировать headers/body/JSON/PII/ключ/токен.
- Remote delete выполнять при успехе, ошибке и отмене; failure сохраняется как pending.
- Retry ограничен и учитывает cancellation; 4xx валидации/авторизации не повторять бесконечно.
- Не полагаться на неподтверждённый `response_format`. JSON проверяется схемой и доменными правилами.
- Ответ всегда Draft; никаких reminders до Confirm.
- Нечитаемое = null + needs_review, не выдуманное значение.

## Parser

Разреши только чистый JSON или один внешний code fence. Не извлекай JSON regex из произвольного текста. Проверь schema_version, additionalProperties, лимиты, время и критические null. После невалидного ответа допустим один повтор; затем страница Failed без потери других страниц.

## Ресурсы

- [Контрольный список API](references/api-checklist.md).
- [JSON Schema](assets/recognition-response.schema.json) — копия контракта 1.0 для standalone-навыка.
- [System prompt](assets/recognition-system-prompt.txt) — копия промпта 1.0.

При работе в репозитории источником истины являются файлы `contracts/`; при расхождении остановись и обнови навык, не выбирай версию молча.

## Завершение

Сообщи модель/версии, фактические тесты, качество/ошибки, remote delete evidence и невыполненные проверки. Не заявляй качество распознавания по одному удачному фото.
