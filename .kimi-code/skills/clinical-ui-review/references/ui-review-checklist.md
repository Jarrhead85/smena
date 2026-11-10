# UI review checklist

## Сценарий

- Основная задача и CTA очевидны; нет конкурирующих primary buttons.
- Back, cancel, retry, offline, denied permission и process return не теряют данные молча.
- Все controls работают; нет placeholder actions.

## Матрица

- 360 и 412 dp; portrait; safe area; keyboard.
- Light/dark; font 100/150/200%; reduced motion.
- Длинные ФИО/процедура/комментарий; пустые optional fields.
- Empty/loading/error/offline/permission states.

## Accessibility

- Touch ≥48 dp; body 17 sp; secondary ≥15 sp.
- Contrast ≥4.5:1 text и ≥3:1 UI/large text.
- TalkBack labels/order/state; decorative images excluded.
- Статус текстом + значком; ошибка не только цветом.
- Нет gesture-only действий.

## Визуальное качество

- Крупное время, спокойная типографика и dividers вместо nested cards.
- Нет gradients, AI sparkles, emoji icons, decorative empty illustrations, random colors.
- Системный font и согласованные Material icons.
- Один primary CTA, опасные действия отделены.

## Приватность

- Нет PII в public lock-screen content, screenshots/recents и внешнем viewer.
- Consent на внешнюю отправку понятен до импорта.
