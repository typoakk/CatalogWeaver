# Пример назначения PostgreSQL

`postgres-destination.json` — универсальный пример тела `destinations.database`, также пригодный для `POST /api/v1/importer/database/validate`. Это не полный адаптер и не схема, которую сервис создаёт автоматически.

Пример предполагает существующие таблицы `public.media` и `public.materials`, генерируемый `media.id`, уникальные ключи `(provider, external_id)` и `(media_id, role, position)`, а также связь `materials.media_id` с `media.id`. Имена и колонки заменяются по фактической структуре приложения.

Сначала прочитайте `/database/Catalog/schema`, адаптируйте пример и проверьте его. Данные подключения находятся в настройках сервиса. `localUrlTemplate` формирует ссылки и не запускает файловый сервер. Подробности в [database-mapping.md](../docs/database-mapping.md).
