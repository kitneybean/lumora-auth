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
