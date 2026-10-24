---
description: Правило выпуска релизов, версионирования и синхронизации документации при любых патчах win_search
---

# Release Management & Search Patching Policy

При пересборке поискового движка, исправлении ранжирования или выпуске любого патча:

1. **Синхронный бамп версии:**
   - `release-version.json`
   - `port/WAI-5c5c1f3ae2b4346353e34704b4783956d1ae8c4f/windows/src/WAI.App/AppConstants.cs`
   - `port/WAI-5c5c1f3ae2b4346353e34704b4783956d1ae8c4f/windows/Directory.Build.props`
   - `port/WAI-5c5c1f3ae2b4346353e34704b4783956d1ae8c4f/windows/scripts/build-package.ps1`
   - `scripts/publish-release.ps1`
   - `port/WAI-5c5c1f3ae2b4346353e34704b4783956d1ae8c4f/windows/docs/Builder-Guide.md`
   - `port/WAI-5c5c1f3ae2b4346353e34704b4783956d1ae8c4f/windows/docs/User-Guide.md`

2. **Обязательная синхронизация документации:**
   - `README.md` — заголовок, бейджи, ссылки на скачивание, highlights и CLI-команды.
   - `CHANGELOG.md` — новый раздел `[X.Y.Z] - YYYY-MM-DD`.
   - `BACKLOG.md` — раздел выпущенных версий.
   - `docs/RELEASE_X.Y.Z.md` — файл описания релиза.

3. **Публикация в GitHub:**
   - Коммит изменений.
   - Создание Git-тега `vX.Y.Z`.
   - Отправка в удалённый репозиторий: `git push origin main` и `git push origin vX.Y.Z`.
