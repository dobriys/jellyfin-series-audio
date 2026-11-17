# Series Audio for Jellyfin

[Русский](#русский) · [English](#english)

## Русский

**Series Audio** запоминает предпочитаемую аудиодорожку для каждого сезона и профиля Jellyfin. Меню добавляется на страницы сериала и сезона в Jellyfin Web. Подпись «По умолчанию» показывает, какую дорожку сервер предлагает при запуске серии.

Сопоставление идёт по названию и языку, поэтому порядок дорожек в разных сериях может отличаться. Если нужной дорожки в серии нет, Jellyfin использует обычный выбор. Администратор может настраивать озвучку для других профилей.

### Установка из каталога

Требуется **Jellyfin Server 12.4.0** со встроенным Jellyfin Web.

1. Откройте **Панель управления → Плагины → Репозитории** и добавьте репозиторий **Series Audio**.

   ```text
   https://github.com/dobriys/jellyfin-series-audio/releases/latest/download/manifest.json
   ```

2. Откройте **Каталог**, найдите **Series Audio** и нажмите **Установить**.
3. Перезапустите сервер Jellyfin, обновите страницу в браузере и откройте сериал. Настройки находятся в блоке **«Озвучки по сезонам»**.

**Переход с ручной установки 0.4.0 и более ранних версий.** Установите новую версию из каталога. Если раньше подключали скрипт `/custom-cont-init.d/99-series-audio`, удалите его подключение и один раз пересоздайте контейнер с тем же образом Jellyfin. Настройки сезонов из 0.2.0–0.4.0 хранятся в каталоге данных Jellyfin и сохраняются при обновлении.

## English

**Series Audio** remembers the preferred audio track for each TV season and Jellyfin profile. Jellyfin Web adds controls to series and season pages and shows the server's default audio track on episode pages. The plugin matches tracks by title and language, so their order can vary between episodes. Jellyfin uses its usual selection when the preferred track is missing. Administrators can manage preferences for other profiles. The plugin controls currently use Russian labels.

### Install from the catalog

Requires **Jellyfin Server 12.4.0** with its built-in Jellyfin Web client.

1. Open **Dashboard → Plugins → Repositories** and add the **Series Audio** repository.

   ```text
   https://github.com/dobriys/jellyfin-series-audio/releases/latest/download/manifest.json
   ```

2. Open **Catalog**, select **Series Audio**, then click **Install**.
3. Restart Jellyfin, refresh the browser, and open a series. Look for **«Озвучки по сезонам»** (Audio by season).

**Migrating from a manual installation of 0.4.0 or earlier.** Install the new version from the catalog. If you mounted `/custom-cont-init.d/99-series-audio`, remove that mount and recreate the container once with the same Jellyfin image. Season preferences from 0.2.0–0.4.0 are stored in Jellyfin's data directory and carry over.
