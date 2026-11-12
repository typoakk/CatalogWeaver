# Создание адаптера

Адаптер — JSON-инструкция для общих инструментов CatalogWeaver. В нём описываются получение списка ID, запросы карточки, сопоставление полей и материалов, политики хранения и назначения. Для нового источника не нужны отдельный worker, Docker-контейнер или writer.

## Минимальный новый источник

Допустим, источник предоставляет:

```text
GET https://api.example.org/media?page=1&limit=50
{"items":[{"id":101,"title":"Example"}]}

GET https://api.example.org/media/101
{"id":101,"title":"Example","description":"Text","tags":["demo"],"poster_url":"https://cdn.example.org/poster.png","screenshots":["https://cdn.example.org/shot1.png"]}
```

Последняя страница списка возвращает `{"items":[]}`. Создайте `adapters/example-source/adapter.json`:

```json
{
  "configVersion": 1,
  "provider": "example-source",
  "displayName": "Example source",
  "enabled": false,
  "kind": "film",
  "refreshHours": 24,
  "baseUrl": "https://api.example.org",
  "sourceUrlTemplate": "https://api.example.org/media/{{externalId}}",
  "allowedHosts": ["api.example.org", "cdn.example.org"],
  "rateLimit": { "requestsPerSecond": 1, "requestsPerMinute": 30 },
  "destinations": { "files": { "directory": "example-source" } },
  "discovery": {
    "request": "discover",
    "pagination": { "type": "page", "startPage": 1, "pageSize": 50, "stopOnEmpty": true },
    "itemsPath": "$.items[*]",
    "externalIdPath": "$.id",
    "namePath": "$.title"
  },
  "requests": {
    "discover": {
      "method": "GET",
      "path": "/media",
      "query": { "page": "{{page}}", "limit": "{{pageSize}}" },
      "responseKind": "object",
      "requiredPaths": ["$.items"]
    },
    "item": {
      "method": "GET",
      "path": "/media/{{externalId}}",
      "required": true,
      "responseKind": "object",
      "identityPath": "$.id",
      "requiredPaths": ["$.title", "$.poster_url", "$.screenshots"]
    }
  },
  "mapping": {
    "fields": {
      "externalId": { "request": "item", "path": "$.id" },
      "title": { "request": "item", "path": "$.title", "transform": "title" },
      "description": { "request": "item", "path": "$.description", "transform": "plainText" },
      "tags": { "request": "item", "path": "$.tags[*]" }
    },
    "assets": {
      "poster": { "request": "item", "urlPath": "$.poster_url" },
      "screenshot": { "request": "item", "itemsPath": "$.screenshots[*]", "urlPath": "$", "requireUrl": true }
    }
  },
  "assetPolicies": {
    "poster": { "storage": "local", "imageProfile": "avif80", "maxItems": 1 },
    "screenshot": { "storage": "link" }
  }
}
```

`example.org` здесь обозначает вымышленный источник. Запросы к нему не являются проверкой доступного поставщика данных. Нужны реальные адреса и фактическая структура ответов вашего источника.

## Как читать инструкцию

`provider` совпадает с названием папки: от 1 до 24 строчных латинских букв, цифр и дефисов, первый символ — буква. `kind` выбирается из `anime`, `game`, `film`, `series`, `book`, `manga`, `music`. Текущий `externalId` — положительное целое до `2147483647`.

`requests` содержит именованные HTTP-инструкции: `GET` для обычного чтения, `POST` для API, которому нужен JSON в теле, например GraphQL. `baseUrl` запроса может переопределять общий адрес; все адреса должны проходить HTTPS и `allowedHosts`. Автоматические непроверенные перенаправления запрещены.

`responseKind` — `object`, `array` или отсутствие проверки формы. `responseRootPath` выбирает часть ответа перед сопоставлением. `identityPath` или `identityPaths` проверяют соответствие запрошенному ID. `requiredPaths` проверяет наличие ключей: значение `null` при существующем ключе отличается от отсутствующего ключа.

`discovery.request` выбирает запрос списка. Пагинация `page` использует `{{page}}` и `{{pageSize}}`; пагинация `cursor` использует `{{cursor}}`, `nextCursorPath` и при необходимости `hasMorePath`. Без явного признака продолжения непустая страница означает, что нужно запросить следующую. Позиция обхода и остаток ограниченной выборки сохраняются в служебной БД. Preview списка проверяет одну страницу.

`mapping.fields` выбирает данные из результатов запросов. Общий набор полей: `externalId`, `title`, `originalTitle`, `alternativeTitles`, `description`, `genres`, `tags`, `creator`, `year`, `rating`, `episodes`, `releaseStatus`, `country`, `language`. `title` обязателен. Это поля нормализованного результата; названия колонок приложения задаются отдельно в назначении БД.

Для альтернативных путей доступны `paths` и `selection: "firstNonEmpty"` либо `"all"`; `itemsPath` позволяет обработать коллекцию. `constant` задаёт постоянную строку или шаблон. Преобразования: `plainText`, `title`, `year`, `number`, `releaseStatus`.

JSONPath поддерживает `$`, `.property`, `[0]`, `[*]` и имена свойств в кавычках: `$['*']` читает ключ `*`, а `$[*]` перебирает коллекцию. Ключ `#0` также остаётся обычным ключом. Рекурсивный поиск и выражения фильтрации не поддерживаются.

`mapping.assets` создаёт материалы с ролями `poster`, `hero`, `logo`, `header`, `background`, `screenshot`, `video`. `assetPolicies` определяет, как обрабатывать каждую роль. `maxItems: 1` ограничивает количество; отсутствие `maxItems` оставляет все найденные уникальные URL. В `storage: "link"` сохраняется исходная ссылка; `storage: "local"` скачивает изображение. `imageProfile: "original"` сохраняет исходный формат, другие профили выбираются из `/image-profiles`. Для видео сейчас поддерживаются ссылки.

При `Importer:MediaRoot=/data/media` файл этого примера окажется в `/data/media/example-source/{SHA-256}`. Папка `.staging` зарезервирована сервисом. Локальные файлы отдаются приложением пользователя или отдельным файловым сервером.

## Переменные и секреты

`variables` объявляет строки, например `{"language":"en"}`. В шаблонах используются `{{language}}`, `{{externalId}}` и преобразования регистра `{{language|upper}}` / `{{language|lower}}`. `externalId` доступен при чтении карточки; `page` или `cursor` и `pageSize` — при discovery. Неизвестная переменная обнаруживается при проверке конфигурации.

Секреты находятся в настройках сервиса: `Importer__Secrets__ExampleKey`. Ссылка `{{secret:ExampleKey}}` разрешена только в параметрах запроса, заголовках, пути запроса или JSON-теле. Значение ключа не включайте в JSON адаптера и Git. Проверка конфигурации допускает объявленную ссылку на ещё не настроенный секрет; выполнение запроса потребует его значение.

Расширенные инструкции `alternativeVariables` и `supplement` позволяют использовать альтернативный параметр запроса и дополнить отсутствующие поля ответом другого варианта того же запроса. Они используют общий runtime. Порядок запросов имеет значение для переменных, полученных предыдущим запросом; не ссылайтесь на ещё недоступную переменную.

## Ошибка запроса и действительно пустое поле

`required: true` означает, что без успешного ответа импорт карточки прерывается. При неудаче необязательного запроса зависимые поля помечаются для повтора, а прежние значения сохраняются. Успешно полученное пустое значение может очистить прежнее значение. Отсутствие сопоставления поля при обновлении также сохраняет ранее полученное поле.

Если исчезновение ключа из успешного ответа должно считаться нарушением контракта источника, перечислите его в `requiredPaths`. Пустая строка, `null` или пустой массив при существующем ключе останутся допустимым явным ответом. Это защищает от незаметного изменения структуры API источника.

Состояния получения полей и материалов формирует сервис. Их не требуется записывать в адаптер. При ошибке исходного запроса корректный старый постер или список скриншотов сохраняется до успешного обновления.

## Добавление, проверка и запуск

В стандартном Compose рабочие адаптеры находятся в постоянном томе, а папка Git содержит исходные шаблоны. Добавляйте конфигурацию через API либо используйте [вариант с bind mount](deployment.md#работа-с-папками-хоста). Реестр обнаруживает новые папки и перечитывает изменения примерно каждые две секунды. Неверная правка оставляет предыдущую рабочую версию и появляется в `/adapters`.

Все пути ниже относительно `/api/v1/importer`, запросы требуют Bearer-ключ сервиса:

1. `POST /adapters/example-source/validate` с полным JSON. Исправьте ошибки с путями вида `$.requests.item.responseKind`.
2. `PUT /adapters/example-source` с полным JSON. Для создания нового адаптера `If-Match` не нужен; одновременное создание не перезаписывает существующий файл.
3. `POST /adapters/example-source/discovery/preview` с `{}`. Проверьте ID и следующий cursor.
4. `POST /adapters/example-source/preview` с `{"externalId":101,"processImages":true,"planDatabase":false}`. Проверьте поля, ссылки, формат изображения и предупреждения. Preview не публикует постоянные файлы и карточки; настоящие запросы расходуют общий лимит источника.
5. Прочитайте `GET /adapters/example-source` и сохраните заголовок `ETag`. Вызовите `POST /adapters/example-source/enabled` с `{"enabled":true}` и `If-Match` из последнего чтения.
6. Выполните `POST /adapters/example-source/jobs` с `{"ids":[101]}`. Проверьте задание и результат через `/jobs/{id}` и `/items/example-source-101`. При настроенном назначении БД идентификатор результата может отличаться.
7. После проверки одной карточки запустите `POST /adapters/example-source/discovery` с `{"maximumItems":10}` и увеличивайте выборку по своей задаче.

Изменение существующей конфигурации, включая переключение `enabled`, требует `If-Match: "<хеш последней версии>"`. При устаревшей версии возвращается HTTP 412; прочитайте конфигурацию заново и объедините изменения. Отсутствующий заголовок при обновлении даёт 428. `If-Match: *` — явное разрешение заменить любую существующую версию; оно полезно для намеренного исправления повреждённого вручную файла, но не защищает от чужих параллельных правок.

Новая конфигурация влияет на новые задания. Уже поставленное задание сохраняет собственный JSON, хеш и параметры кодировщика. Изменение профиля или шаблона не меняет инструкции активного задания.

## Назначение в БД приложения

Для прямой доставки добавьте `destinations.database` по [инструкции сопоставления](database-mapping.md), задайте приватное именованное подключение и проверьте `planDatabase: true`. Используйте существующие таблицы и колонки приложения. Собственные таблицы очереди и состояния принадлежат CatalogWeaver; миграции сервиса не создают структуру целевой БД.

Машиночитаемые схемы: [адаптер](../schemas/adapter.schema.json), [назначение БД](../schemas/database.schema.json), [кодировщик](../schemas/encoder.schema.json). Привязка схем к редактору описана в [schemas/README.md](../schemas/README.md). Доступные операции API описаны в [api.md](api.md) и `/api/v1/importer/openapi.json`.
