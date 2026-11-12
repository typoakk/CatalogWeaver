# CatalogWeaver

Самостоятельный сервис импорта медиакаталогов. Получает карточки и материалы по JSON-инструкциям адаптеров, скачивает выбранные изображения, перекодирует их и сохраняет результат. Целевая среда — Linux с Docker Compose; запуск из исходников поддерживается также в Windows.

В комплекте адаптеры Shikimori и Steam, постоянная очередь PostgreSQL, общие HTTP-лимиты и модули AVIF, WebP, JPEG и PNG. Управление работает через REST API. Графического интерфейса пока нет; API позволяет добавить его отдельно.

## Запуск в Linux

На компьютере нужны только Git, Docker Engine и плагин Docker Compose. Создайте пустую папку и клонируйте репозиторий прямо в неё:

```sh
mkdir CatalogWeaver
cd CatalogWeaver
git clone https://github.com/typoakk/CatalogWeaver.git .
docker compose up --build -d
docker compose ps
```

Отдельно устанавливать .NET, PostgreSQL и библиотеки изображений не нужно. Docker скачает базовые образы и PostgreSQL; при сборке восстановит NuGet-пакеты и включит runtime-зависимости в образ приложения. SDK остаётся в стадии сборки. Первый запуск требует доступа к реестрам образов и репозиториям пакетов; последующие запуски используют уже собранный образ.

Compose сам создаёт пароль служебной БД и ключ управления в томе `configuration`, затем запускает PostgreSQL, применяет служебную схему и поднимает импортер. Значения сохраняются при перезапусках и не печатаются в журналы. Управление доступно на `http://127.0.0.1:8770`. Адаптеры изначально выключены; массовый обход запускается явным запросом.

Получить ключ управления и проверить готовность можно средствами самого контейнера:

```sh
docker compose exec importer cat /data/config/api-key
docker compose exec importer curl -fsS http://127.0.0.1:8080/health/ready
```

`.env` необязателен. Для дополнительных настроек скопируйте `.env.example` в `.env`: можно задать `IMPORTER_PORT`, `STEAM_API_KEY` для обхода списка Steam и подключение целевой БД. Для импорта известного ID Steam ключ источника не требуется. В Windows с Docker Desktop в режиме Linux-контейнеров используются те же команды Git и Compose.

Настройки адаптеров и кодировщиков находятся в отдельных Docker-томах. Папки репозитория `adapters` и `encoders` служат начальными шаблонами: Docker копирует их при создании новых томов. Дальнейшие изменения через API сохраняются независимо от обновлений исходников. Для работы непосредственно с папками хоста используйте [compose.bind.yaml](compose.bind.yaml); порядок и права описаны в [инструкции запуска](docs/deployment.md).

## Что и куда сохраняется

| Назначение | Настройка | В стандартном Compose |
| --- | --- | --- |
| Пароль служебной БД и ключ управления | `Importer:DatabasePasswordFile`, `Importer:ApiKeyFile` | `/data/config`, том `configuration` |
| Рабочие адаптеры и кодировщики | `Importer:AdaptersPath`, `Importer:EncodersPath` | `/app/adapters`, `/app/encoders`, тома `adapters` и `encoders` |
| Очередь, прогресс, лимиты, нормализованные карточки | `ConnectionStrings:ImporterDb` | Отдельная PostgreSQL, том `state` |
| Скачанные изображения | `Importer:MediaRoot` | `/data/media`, том `media` |
| Подпапка материалов источника | `destinations.files.directory` в адаптере | Например, `shikimori` внутри MediaRoot |
| Необязательная запись в БД приложения | `destinations.database` в адаптере | Существующие таблицы именованного подключения |

Служебная БД нужна для работы очереди. БД приложения подключается отдельно: адаптер задаёт таблицы, колонки, связи и ключи обновления. Подключение хранится в конфигурации сервиса, в адаптере указывается только его имя. Поддерживается PostgreSQL; инструмент не создаёт таблицы назначения и не управляет их миграциями.

В `.env` Compose можно задать `CATALOG_DATABASE_CONNECTION`; в адаптере она выбирается как `"connection": "Catalog"`. Без назначения в другую БД результаты доступны через `/items` собственного API.

Файлы сохраняются на диске. В БД записываются URL, относительные пути, хеши и настроенные поля. Изображения раздаёт приложение пользователя или отдельный файловый сервер. [Подключение к своему сервису](docs/service-integration.md), [сопоставление таблиц](docs/database-mapping.md).

## Адаптеры и кодировщики

Адаптер лежит в `adapters/{provider}/adapter.json` и описывает запросы, разбор ответа, роли материалов, лимиты и назначения. Конфигурации автоматически перечитываются. Некорректная правка сохраняет последнюю рабочую версию и появляется в диагностике.

Модули перекодирования лежат отдельно: `encoders/{module}/encoder.json`. Они задают формат, профили качества и параметры кодировщика. Можно добавить модуль без пересборки и перезапуска сервиса, затем выбрать его профиль в адаптере:

```json
"assetPolicies": {
  "poster": { "storage": "local", "imageProfile": "webp80", "maxItems": 1 },
  "screenshot": { "storage": "link" }
}
```

| Профиль | Результат |
| --- | --- |
| `original` | Проверенный файл без перекодирования |
| `avif1`–`avif100` | AVIF с выбранным качеством; `avif80` сохранён |
| `webp1`–`webp100` | WebP с выбранным качеством |
| `webp-lossless` | WebP без потерь |
| `jpeg1`–`jpeg100` | JPEG с выбранным качеством |
| `png-lossless` | PNG без потерь |

Реестр проверяет папку модулей каждые две секунды. Обновление зависимого адаптера может занять ещё один цикл. `POST /encoders/reload` перечитывает оба реестра сразу. Список доступных профилей возвращает `GET /image-profiles`. [Формат модуля и добавление профиля](docs/encoders.md).

Все форматы используют согласованный порядок: перекодирование → проверка → повторная проверка того же файла → повторное перекодирование оригинала и до двух проверок нового файла → сохранение оригинала при неудаче. Корректный результат принимается и при большем размере. JPEG не удаляет прозрачность: такие изображения остаются в оригинале. Анимация и изображения глубже 8 бит также сохраняются в оригинале.

Оригинал удаляется из временной папки после фиксации результата в БД. Постоянное имя файла — SHA-256 содержимого. Настройки кодировщика фиксируются в снимке задания вместе с адаптером: правка модуля не меняет уже поставленное задание.

## Первое управление

Все управляющие пути начинаются с `/api/v1/importer` и требуют `Authorization: Bearer <ключ управления>`. Проверки `/health/live` и `/health/ready` доступны без ключа.

Пример проверки одной карточки из Linux:

```sh
export CATALOGWEAVER_API_KEY="$(docker compose exec -T importer cat /data/config/api-key)"
curl -fsS -H "Authorization: Bearer $CATALOGWEAVER_API_KEY" \
  http://127.0.0.1:8770/api/v1/importer/encoders
curl -fsS -H "Authorization: Bearer $CATALOGWEAVER_API_KEY" \
  -H 'Content-Type: application/json' \
  --data '{"externalId":52991,"processImages":true,"planDatabase":true}' \
  http://127.0.0.1:8770/api/v1/importer/adapters/shikimori/preview
```

Preview работает и при выключенном адаптере, выполняет реальные запросы, но не сохраняет постоянные карточки или файлы. План целевой БД строится в read-only транзакции. HTTP-лимиты учитываются и при проверках. Для настоящего импорта включите адаптер и отправьте список ID в `/adapters/{provider}/jobs` либо запустите ограниченный обход через `/adapters/{provider}/discovery`.

[Все маршруты и примеры запросов](docs/api.md).

## Разработка и устройство

Нужны .NET SDK 10 и PostgreSQL. Пример запуска из исходников в Linux:

```sh
export ConnectionStrings__ImporterDb='Host=localhost;Database=media_importer;Username=importer;Password=YOUR_PRIVATE_PASSWORD'
export Importer__ApiKey='YOUR_PRIVATE_RANDOM_KEY_AT_LEAST_32_CHARACTERS'
export Importer__AdaptersPath="$PWD/adapters"
export Importer__EncodersPath="$PWD/encoders"
export Importer__MediaRoot="$PWD/data/media"
dotnet run --project src/MediaImporter -- --migrate
dotnet run --project src/MediaImporter -- --urls http://127.0.0.1:8770
```

`--bootstrap` создаёт секреты в `Importer:ConfigurationPath` без подключения к БД. `--migrate` управляет только служебной схемой. `--inspect-db Catalog` печатает структуру именованного подключения без запуска сервиса. `--help` работает без БД и ключа. В PowerShell переменные задаются через `$env:ИМЯ = 'значение'`.

| Путь | Роль |
| --- | --- |
| `adapters/` | Начальные инструкции источников |
| `encoders/` | Начальные модули перекодирования |
| `examples/` | Универсальные примеры сопоставления с БД приложения |
| `src/MediaImporter/AdapterEngine` | Реестр адаптеров, запросы и нормализация |
| `src/MediaImporter/Queue` | Постоянная очередь, обход, аренды и повторы |
| `src/MediaImporter/Transport` | HTTP, разрешённые хосты и общие лимиты |
| `src/MediaImporter/Images` | Реестр кодировщиков, проверки, перекодирование и файлы |
| `src/MediaImporter/Persistence` | Собственное состояние и доставка результата |
| `src/MediaImporter/Database` | Чтение схемы, планирование и запись в заданные таблицы |
| `tests/` | Проверки сервиса и интеграций |

[Текущая схема](docs/architecture.puml), [инструкция для агентов](agent.md).

```sh
dotnet test tests/MediaImporter.Tests/MediaImporter.Tests.csproj -c Release
```

Для интеграционных тестов задайте `MEDIA_IMPORTER_TEST_CONNECTION` к отдельной PostgreSQL с `test` в имени БД. Без неё проверки БД пропускаются. Docker target `checks` позволяет запускать тесты в Linux. [Как проверяется проект](docs/verification.md), [проверка первого запуска](docs/bootstrap-verification.md), [исторический отчёт модулей](docs/encoder-verification.md).

Известные свободные файлы очищаются автоматически; старые файлы, экспортированные во внешнюю БД, сохраняются до отдельного освобождения ссылок. Подробнее об этом — в [инструкции подключения](docs/service-integration.md).

## Происхождение

CatalogWeaver появился из задачи импорта медиакаталога Nexus и затем был выделен в самостоятельный проект. Исходное приложение, его схема БД и интеграционные пакеты для сборки и работы не требуются.
