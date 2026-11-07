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
