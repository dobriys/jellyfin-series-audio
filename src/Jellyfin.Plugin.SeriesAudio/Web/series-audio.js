/* Series Audio 0.1.0 — loaded inside Jellyfin Web 12.4. */
(() => {
    'use strict';
    if (window.__seriesAudioLoaded) return;
    window.__seriesAudioLoaded = true;

    let generation = 0;
    let pageKey = '';
    let timer;
    const label = p => `${p.title || 'Без названия'} · ${p.language || 'язык не указан'}`;
    const key = p => JSON.stringify([p.title.normalize('NFKC').trim().replace(/\s+/g, ' ').toLowerCase(), p.language]);
    const element = (tag, text, className) => {
        const node = document.createElement(tag);
        if (text) node.textContent = text;
        if (className) node.className = className;
        return node;
    };

    async function mount() {
        const api = window.ApiClient;
        const userId = api?.getCurrentUserId();
        const query = new URLSearchParams(location.hash.split('?')[1] || '');
        const itemId = location.hash.includes('/details?') ? query.get('id') : null;
        const page = [...document.querySelectorAll('.itemDetailPage')].find(p => !p.classList.contains('hide') && p.getAttribute('aria-hidden') !== 'true');
        const container = page?.querySelector('.detailPagePrimaryContainer');
        const nextKey = userId && itemId && container ? `${api.serverId()}/${userId}/${itemId}` : '';
        if (nextKey === pageKey && (!nextKey || container.querySelector('[data-series-audio]'))) return;
        pageKey = nextKey;
        const current = ++generation;
        document.querySelectorAll('[data-series-audio]').forEach(n => n.remove());
        if (!nextKey) return;
        try {
            const [item, user] = await Promise.all([api.getItem(userId, itemId), api.getCurrentUser()]);
            if (current !== generation) return;
            const seriesId = item.Type === 'Series' ? item.Id : item.SeriesId;
            if (!seriesId || !['Series', 'Season', 'Episode'].includes(item.Type)) return;

            const panel = element('section', '', 'detailSection');
            panel.dataset.seriesAudio = 'true';
            panel.style.cssText = 'padding:1em;border:1px solid #7776;border-radius:.5em;margin:1em 0;max-width:42em';
            panel.append(element('h3', 'Озвучка для сериала'));
            panel.append(element('p', 'Сохраняется для выбранного профиля и применяется при следующем запуске серии.'));
            const profileLabel = element('label', 'Профиль ');
            const profile = element('select');
            profile.setAttribute('aria-label', 'Профиль для озвучки');
            profile.add(new Option(user.Name, user.Id));
            profileLabel.append(profile);
            panel.append(profileLabel);
            const audioLabel = element('label', 'Озвучка ');
            audioLabel.style.cssText = 'display:block;margin:1em 0';
            const audio = element('select');
            audio.setAttribute('aria-label', 'Озвучка для всего сериала');
            audio.style.maxWidth = '100%';
            audioLabel.append(audio);
            panel.append(audioLabel);
            const save = element('button', 'Сохранить', 'raised button-submit emby-button');
            const reset = element('button', 'Использовать настройки Jellyfin', 'raised emby-button');
            save.type = reset.type = 'button';
            reset.style.margin = '.5em';
            const status = element('p');
            status.setAttribute('role', 'status');
            panel.append(save, reset, status);
            container.append(panel);

            let choices = [];
            let requestNumber = 0;
            const request = (method, data) => api.ajax({
                type: method,
                url: api.getUrl(`SeriesAudio/Series/${seriesId}`, {
                    userId: profile.value,
                    ...(item.Type === 'Episode' ? { episodeId: item.Id } : {})
                }),
                ...(method === 'GET' ? { dataType: 'json' } : {}),
                ...(data ? { data: JSON.stringify(data), contentType: 'application/json' } : {})
            });
            const busy = value => { profile.disabled = audio.disabled = save.disabled = reset.disabled = value; };
            const load = async () => {
                const number = ++requestNumber;
                busy(true);
                status.textContent = 'Загружаю озвучки…';
                try {
                    const response = await request('GET');
                    if (current !== generation || number !== requestNumber) return;
                    choices = response.choices;
                    audio.replaceChildren();
                    for (const [i, c] of choices.entries()) audio.add(new Option(label(c.preference), String(i)));
                    const selected = response.preference && choices.findIndex(c => key(c.preference) === key(response.preference));
                    audio.selectedIndex = typeof selected === 'number' && selected >= 0 ? selected : -1;
                    status.textContent = response.preference
                        ? `Сохранено: ${label(response.preference)}. Если этой озвучки нет в эпизоде, используется выбор Jellyfin.`
                        : 'Сейчас используются настройки Jellyfin. Выберите озвучку и нажмите «Сохранить».';
                    if (response.hasMoreEpisodes) status.textContent += ' Список собран по первым 100 доступным сериям. Для другой озвучки откройте нужный эпизод.';
                    busy(false);
                    save.disabled = !choices.length || audio.selectedIndex < 0;
                } catch {
                    if (current !== generation) return;
                    busy(false);
                    save.disabled = reset.disabled = true;
                    status.textContent = 'Не удалось загрузить настройки. Проверьте, что плагин Series Audio установлен, затем обновите страницу.';
                }
            };
            profile.addEventListener('change', load);
            audio.addEventListener('change', () => { save.disabled = audio.selectedIndex < 0; });
            save.addEventListener('click', async () => {
                const choice = choices[Number(audio.value)];
                if (!choice || audio.selectedIndex < 0) return;
                busy(true);
                try {
                    await request('PUT', { episodeId: choice.episodeId, mediaSourceId: choice.mediaSourceId, audioStreamIndex: choice.audioStreamIndex });
                    await load();
                } catch {
                    busy(false);
                    status.textContent = 'Не удалось сохранить озвучку. Обновите страницу и повторите выбор.';
                }
            });
            reset.addEventListener('click', async () => {
                busy(true);
                try { await request('DELETE'); await load(); }
                catch { busy(false); status.textContent = 'Не удалось сбросить настройку. Повторите позже.'; }
            });
            if (user.Policy?.IsAdministrator) {
                try {
                    const allUsers = await api.getUsers();
                    if (current !== generation) return;
                    for (const other of allUsers) if (other.Id !== user.Id) profile.add(new Option(other.Name, other.Id));
                } catch { /* Own-profile controls remain usable. */ }
            }
            await load();
        } catch (error) {
            console.warn('Series Audio: page could not be initialized', error);
        }
    }

    const schedule = () => { clearTimeout(timer); timer = setTimeout(mount, 150); };
    addEventListener('hashchange', schedule);
    document.addEventListener('viewshow', schedule);
    new MutationObserver(records => {
        if (records.some(r => !r.target.closest?.('[data-series-audio]'))) schedule();
    }).observe(document.documentElement, { childList: true, subtree: true });
    schedule();
})();
