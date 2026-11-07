# lumora-auth

Сервер авторизации **Lumora** для Minecraft: аккаунты, скины, сборки и игровые серверы.
ASP.NET Core (.NET 10) + Blazor + PostgreSQL.

- **Сайт** — регистрация, вход, ник, загрузка скина.
- **Админка** — сборки (версия MC + лоадер + моды) и серверы, привязанные к сборкам.
- **Yggdrasil API** (`/api/yggdrasil`) — совместим с [authlib-injector](https://github.com/yushijinhun/authlib-injector):
  клиент и игровой сервер проверяют игроков и берут скины отсюда.
- **Launcher API** (`/api/launcher`) — список серверов и манифесты сборок для Lumora Launcher.

## Структура

| Проект | Назначение |
|---|---|
| `src/Lumora.Auth.Web` | Хост: API, Blazor-сайт и админка |
| `src/Lumora.Auth.Core` | Доменные сущности и логика |
| `src/Lumora.Auth.Data` | EF Core `LumoraDbContext`, миграции PostgreSQL |
| `src/Lumora.Contracts` | DTO Launcher API (используется и лаунчером) |
| `tests/Lumora.Auth.Tests` | Тесты |

## Запуск для разработки

```bash
docker compose up -d db
dotnet run --project src/Lumora.Auth.Web --launch-profile https
```

Сайт: https://localhost:7443. При первом запуске применяются миграции и создаётся администратор
из `appsettings.Development.json` (`admin@lumora.local` / `Admin12345`).

Новая миграция:

```bash
dotnet ef migrations add <Name> -p src/Lumora.Auth.Data -s src/Lumora.Auth.Web -o Migrations
```

## Запуск в Docker

```bash
cp .env.example .env   # задайте пароли и публичный адрес
docker compose up -d --build
```

Сайт: http://localhost:8080 (в продакшене ставится за reverse proxy с HTTPS).
Данные (RSA-ключ подписи, скины, файлы сборок) хранятся в volume `lumora-data`.

## Yggdrasil API

Адрес для authlib-injector: `<PublicUrl>/api/yggdrasil` (сайт также отдаёт заголовок
`X-Authlib-Injector-API-Location`, так что подойдёт и просто адрес сайта).

| Эндпоинт | Назначение |
|---|---|
| `GET /` | Метаданные: имя сервера, домены скинов, публичный ключ подписи |
| `POST /authserver/authenticate`, `refresh`, `validate`, `invalidate`, `signout` | Токены лаунчера. Вход по e-mail **или нику** |
| `POST /sessionserver/session/minecraft/join`, `GET .../hasJoined` | Проверка игрока при входе на сервер |
| `GET /sessionserver/session/minecraft/profile/{uuid}` | Профиль со скином (`?unsigned=false` — с подписью) |
| `POST /api/profiles/minecraft`, `GET /api/users/profiles/minecraft/{name}` | Поиск UUID по нику (без учёта регистра) |
| `PUT`/`DELETE /api/user/profile/{uuid}/skin` | Загрузка и сброс скина из лаунчера (`Authorization: Bearer <accessToken>`, multipart: `file`, `model=slim`) |
| `GET /minecraftservices/...` | Заглушки для клиентов 1.19+ (атрибуты игрока, блок-лист) |

Скины отдаются с `/textures/{sha256}`. Свойство `textures` подписывается ключом RSA-4096 (SHA1withRSA),
ключ создаётся при первом запуске в `<DataPath>/keys/yggdrasil-signing.pem` — его нужно сохранять при переездах,
иначе игровые серверы перестанут принимать подписи до перезапуска.

Настройки (`Lumora` в `appsettings.json`): `TokenLifetime` (15 дней), `TokenRefreshWindow` (+30 дней на обновление),
`MaxTokensPerUser` (10), `AuthAttemptsPerMinute` (10 попыток входа в минуту с одного IP).
Заблокированный игрок (`BanReason` + `LockoutEnd`) не может войти, а его токены перестают работать.

## Тесты

```bash
dotnet test
```

Интеграционные тесты API поднимают временный PostgreSQL через Testcontainers — нужен запущенный Docker.

## Скины

Игрок загружает скин на сайте («Мой аккаунт → Скин», с 3D-превью на [skinview3d](https://github.com/bs-community/skinview3d))
или из лаунчера через API выше. Принимается PNG 64×64 или 64×32 до 256 КБ. Файл перекодируется
(метаданные удаляются, у полностью прозрачных пикселей обнуляется цвет) и сохраняется как
`<DataPath>/textures/<sha256>.png` — одинаковые скины хранятся один раз. Обработка изображений — SkiaSharp.

## Админка (`/admin`)

Доступна пользователям с ролью `Admin` (первый админ создаётся из настроек `Lumora:Admin`).

- **Сборки** — версия Minecraft и NeoForge (подсказки берутся из манифеста Mojang и maven NeoForged), Java, рекомендуемая
  память, обязательные JVM-аргументы, правила синхронизации папок. Файлы загружаются прямо из браузера: отдельные файлы в
  выбранную папку или ZIP целиком с сохранением структуры. Для каждого файла: где нужен (везде / клиент / сервер) и
  обязательный он или опциональный (игрок может выключить в лаунчере).
  Кнопка **«Опубликовать»** замораживает текущее состояние в ревизию — лаунчеры видят только опубликованное.
  - **Импорт готовой сборки:** `.mrpack` (экспорт Modrinth и Prism Launcher) — версии Minecraft/NeoForge берутся из него,
    моды скачиваются с Modrinth с проверкой SHA-1, `overrides/` (конфиги) распаковываются; ZIP экспорта Prism/MultiMC,
    CurseForge или просто папки игры — обёртки `.minecraft/`, `overrides/` срезаются сами.
  - **Проверка модов:** дубликаты одного мода и недостающие обязательные зависимости (по `neoforge.mods.toml`,
    с учётом модов, вложенных в jar).
- **Серверы** — адрес, порт, иконка (любая картинка → 64×64), привязка к сборке, «добавлять в список серверов игры»,
  «сразу подключаться». В лаунчере виден включённый сервер с опубликованной сборкой.
- **Игроки** — поиск, бан на срок или навсегда (сессии в лаунчере и на сайте завершаются), сброс скина, роль админа.
- **Настройки** — название, открыта ли регистрация, подтверждение e-mail, срок входа в лаунчере; почта (SMTP)
  с шаблонами для Яндекса, Mail.ru, Gmail, Brevo, Resend, SendGrid и тестовым письмом. Пароль почты хранится зашифрованным.
- **Обзор** — статистика и версия authlib-injector.

Файлы сборок хранятся в `<DataPath>/files/` по SHA-1 (одинаковые файлы — один раз).

## Launcher API (`/api/launcher`)

| Эндпоинт | Доступ | Назначение |
|---|---|---|
| `GET /servers` | Bearer | Серверы с опубликованными сборками |
| `GET /builds/{id}/manifest` | Bearer | Опубликованная ревизия: версии, файлы со ссылками, правила синхронизации, серверы для `servers.dat` |
| `GET /files/{sha1}` | Bearer | Файл сборки |
| `GET /servers/{id}/icon` | все | Иконка сервера |
| `GET /authlib-injector`, `GET /authlib-injector/download` | все | Версия/SHA-256 и сам jar authlib-injector |

`Bearer` — `accessToken` из `/api/yggdrasil/authserver/authenticate`. DTO описаны в `Lumora.Contracts`.
authlib-injector (AGPL-3.0, [исходники](https://github.com/yushijinhun/authlib-injector)) скачивается без изменений
с официального зеркала при первом запросе и проверяется по SHA-256.

### Как собрать сборку

1. Соберите и отладьте сборку в [Prism Launcher](https://prismlauncher.org) (или приложении Modrinth): NeoForge той же
   версии, моды, запуск, настройка конфигов.
2. Экспортируйте её как **Modrinth (.mrpack)** (в Prism: «Экспорт» → Modrinth) или как ZIP папки экземпляра.
3. В админке: «Сборки» → сборка → загрузите файл → «Проверить» в «Проверке модов» → поправьте «Где нужен»
   (клиентские моды — «Только клиент») → «Опубликовать».

Тест с настоящим скачиванием с Modrinth: `LUMORA_NETWORK_TESTS=1 dotnet test`.

## Развёртывание на сервере (HTTPS)

Нужен VPS с Linux (от 2 ГБ ОЗУ), домен и Docker.

1. **DNS:** A-запись `auth.example.com` → IP сервера. Откройте порты 80 и 443.
2. **Код:** `git clone <репозиторий lumora-auth> && cd lumora-auth/deploy`
3. **Настройки:** `cp .env.example .env` и заполните домен, пароль базы и первого админа.
4. **Запуск:** `docker compose up -d --build` — поднимутся PostgreSQL, сайт и [Caddy](https://caddyserver.com),
   который сам получит и будет продлевать сертификат Let's Encrypt.
5. Откройте `https://auth.example.com`, войдите админом, настройте почту в «Админка → Настройки»,
   уберите `LUMORA_ADMIN_PASSWORD` из `.env`.

Лаунчеру и игровому серверу нужен адрес `https://auth.example.com` (в лаунчере — «Настройки → Лаунчер»,
для сервера — параметр `-AuthUrl` в `lumora-gameserver`).

**Обновление:** `git pull && docker compose up -d --build` (миграции базы применяются сами).

**Бэкапы:** `deploy/backup.sh` — дамп базы и папка данных (ключ подписи скинов, скины, файлы сборок) в
`deploy/backups/`, хранит 14 последних. Поставьте в cron, например `0 4 * * * /opt/lumora-auth/deploy/backup.sh`,
и копируйте бэкапы на другой сервер. Восстановление: `deploy/restore.sh <dump> <tar.gz>`.

Сайт за своим прокси (nginx и т. п.): пробросьте заголовки `X-Forwarded-For/Proto/Host` и задайте
`Lumora__BehindProxy=true`, иначе сайт не поймёт, что он за HTTPS.
