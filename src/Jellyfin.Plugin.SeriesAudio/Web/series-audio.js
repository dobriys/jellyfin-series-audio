/* Series Audio 0.5.0 — loaded inside Jellyfin Web 12.4. */
(() => {
    'use strict';
    if (window.__seriesAudioLoaded) return;
    window.__seriesAudioLoaded = true;

    let active;
    let timer;
    const languages = new Intl.DisplayNames(['ru'], { type: 'language', fallback: 'none' });
    const titleOf = value => {
        const title = (value || '').normalize('NFKC').trim();
        return /^(unknown|und|неизвестно|без названия)$/i.test(title.replace(/\s+/g, ' ')) ? '' : title;
    };
    const languageOf = value => {
        const code = (value || '').trim().toLowerCase();
        return ({ rus: 'ru', eng: 'en', ukr: 'uk', heb: 'he', und: '', unknown: '', zxx: '' })[code] ?? code;
    };
    const label = track => {
        const code = languageOf(track.language);
        const title = titleOf(track.title);
        let language;
        if (/^[a-z]{2,3}$/i.test(code)) language = languages.of(code);
        if (title) return language ? `${title} (${language})` : title;
        return language ? language[0].toUpperCase() + language.slice(1) + ' (аудиодорожка)' : 'Аудиодорожка';
    };
    const key = p => JSON.stringify([p.title.normalize('NFKC').trim().replace(/\s+/g, ' ').toLowerCase(), p.language]);
    const element = (tag, text, className) => {
        const node = document.createElement(tag);
        if (text) node.textContent = text;
        if (className) node.className = className;
        return node;
    };
    const styles = element('style', `
        .seriesAudioPanel {
            margin: 1.25em 0 1.5em;
            padding: .8em 1em;
            border-inline-start: .2em solid #00a4dc;
            border-radius: .35em;
            background: rgba(128, 128, 128, .12);
            box-sizing: border-box;
            max-width: 56em;
            text-align: start;
        }
        .seriesAudioPanel h2 { font-size: 1.15em; font-weight: 600; margin: 0 0 .55em; }
        .seriesAudioSeason { padding-block: .85em; }
        .seriesAudioSeason + .seriesAudioSeason { border-block-start: 1px solid #80808055; }
        .seriesAudioSeason:last-child { padding-block-end: .2em; }
        .seriesAudioSeason h3 { font-size: 1em; font-weight: 600; margin: 0 0 .5em; }
        .seriesAudioDefault { margin: .4em 0 .65em; overflow-wrap: anywhere; }
        .seriesAudioDefault:empty { display: none; }
        .seriesAudioEpisode { margin: .55em 0 .85em; }
        .seriesAudioControls { display: flex; flex-wrap: wrap; align-items: end; gap: .6em .85em; }
        .seriesAudioField { display: grid; gap: .3em; flex: 0 1 11em; min-width: 0; }
        .seriesAudioTrack { flex: 1 1 15em; }
        .seriesAudioField span { font-size: .86em; }
        .seriesAudioField select {
            box-sizing: border-box; width: 100%; min-height: 2.65em; padding: .35em .6em;
            background: transparent; color: inherit; border: 1px solid; border-radius: .3em;
            font: inherit; text-overflow: ellipsis;
        }
        .seriesAudioField option { background: Canvas; color: CanvasText; }
        .seriesAudioActions { display: flex; flex-wrap: wrap; gap: .4em; }
        .seriesAudioPanel [hidden] { display: none; }
        .seriesAudioActions button { margin: 0; min-height: 2.65em; }
        .seriesAudioPanel :is(select, button):focus-visible { outline: 2px solid #00a4dc; outline-offset: 2px; }
        .seriesAudioStatus { margin: .6em 0 0; font-size: .9em; }
        .seriesAudioStatus:empty { margin: 0; }
    `);
    document.head.append(styles);

    function field(text, select, className = '') {
        const node = element('label', '', `seriesAudioField ${className}`);
        node.append(element('span', text), select);
        select.setAttribute('aria-label', text);
        return node;
    }

    function renderDefault(node, tracks) {
        const labels = [...new Set(tracks.filter(Boolean).map(label))];
        let text = labels.join(', ');
        if (!labels.length) text = 'Дорожка не определена';
        else if (labels.length > 1) text = `Зависит от серии (${text})`;
        if (labels.length && tracks.includes(null)) text += '. Для части серий дорожка не определена.';
        node.replaceChildren(element('strong', 'По умолчанию'), ' — ' + text);
    }

    function episodeDefault(source) {
        const tracks = source?.MediaStreams?.filter(stream => stream.Type === 'Audio') || [];
        const selected = tracks.find(track => track.Index === source.DefaultAudioStreamIndex)
            || tracks.find(track => track.IsDefault) || tracks[0];
        if (!selected) return null;
        const language = languageOf(selected.Language);
        let title = titleOf(selected.Title);
        if (!title && (!language || tracks.some(track => track !== selected && !titleOf(track.Title) && languageOf(track.Language) === language))) {
            title = `Дорожка ${tracks.indexOf(selected) + 1}`;
        }
        return { title, language };
    }

    function setupSeason(container, seasonId, api, user, profiles, isCurrent, initial) {
        const controls = element('div', '', 'seriesAudioControls');
        const profile = element('select');
        for (const account of profiles) profile.add(new Option(account.Name, account.Id));
        if (user.Policy?.IsAdministrator) controls.append(field('Профиль', profile));
        const audio = element('select');
        const audioField = field('Аудиодорожка', audio, 'seriesAudioTrack');
        controls.append(audioField);
        const save = element('button', 'Сохранить', 'raised button-submit emby-button');
        const reset = element('button', 'Сбросить', 'raised emby-button');
        reset.title = 'Использовать настройки Jellyfin';
        save.type = reset.type = 'button';
        const actions = element('div', '', 'seriesAudioActions');
        actions.append(save, reset);
        controls.append(actions);
        const status = element('p', '', 'seriesAudioStatus');
        status.setAttribute('role', 'status');
        const defaultLine = element('p', '', 'seriesAudioDefault');
        defaultLine.setAttribute('aria-live', 'polite');
        container.append(defaultLine, controls, status);

        let choices = [];
        let savedIndex = -1;
        let hasPreference = false;
        const request = (method, data) => api.ajax({
            type: method,
            url: api.getUrl(`SeriesAudio/Season/${seasonId}`, { userId: profile.value }),
            ...(method === 'GET' ? { dataType: 'json' } : {}),
            ...(data ? { data: JSON.stringify(data), contentType: 'application/json' } : {})
        });
        const busy = value => {
            profile.disabled = value;
            audio.disabled = value || choices.length < 2;
            save.disabled = audio.disabled || audio.value === '' || Number(audio.value) === savedIndex;
            reset.disabled = audio.disabled || !hasPreference;
        };
        const render = (response, message = '') => {
            choices = response.choices;
            const defaults = response.defaults || [];
            const canChoose = choices.length >= 2;
            const hasDefault = defaults.some(Boolean);
            // Keep the profile switch available when an admin inspects another profile.
            controls.hidden = !canChoose && profile.value === user.Id;
            container.hidden = controls.hidden && !hasDefault;
            audioField.hidden = actions.hidden = !canChoose;
            renderDefault(defaultLine, defaults);
            if (!canChoose) {
                status.textContent = controls.hidden && hasDefault ? '' : 'Для этого профиля нет выбора озвучки.';
                busy(false);
                return;
            }
            hasPreference = Boolean(response.preference);
            savedIndex = hasPreference ? choices.findIndex(c => key(c.preference) === key(response.preference)) : -1;
            const placeholder = new Option('Выберите дорожку', '');
            placeholder.disabled = true;
            audio.replaceChildren(placeholder);
            for (const [i, choice] of choices.entries()) audio.add(new Option(label(choice.preference), String(i)));
            audio.value = savedIndex < 0 ? '' : String(savedIndex);
            if (message) status.textContent = message;
            else if (savedIndex >= 0) status.textContent = `Сохранено для сезона — ${label(response.preference)}`;
            else if (hasPreference) status.textContent = 'Сохранённая дорожка сейчас недоступна.';
            else status.textContent = 'Выбор по настройкам Jellyfin.';
            busy(false);
        };
        const load = async (message = '') => {
            choices = [];
            hasPreference = false;
            defaultLine.replaceChildren();
            audio.replaceChildren(new Option('Загрузка…', ''));
            busy(true);
            status.textContent = 'Загружаем озвучки…';
            try {
                const response = await request('GET');
                if (!isCurrent()) return;
                render(response, message);
            } catch {
                if (!isCurrent()) return;
                container.hidden = false;
                audio.options[0].textContent = 'Загрузка не удалась';
                busy(false);
                status.textContent = 'Не удалось загрузить озвучки. Обновите страницу и попробуйте снова.';
            }
        };
        profile.addEventListener('change', () => load());
        audio.addEventListener('change', () => busy(false));
        save.addEventListener('click', async () => {
            const choice = choices[Number(audio.value)];
            if (!choice || save.disabled) return;
            busy(true);
            try {
                await request('PUT', { episodeId: choice.episodeId, mediaSourceId: choice.mediaSourceId, audioStreamIndex: choice.audioStreamIndex });
                if (isCurrent()) await load();
            } catch {
                if (!isCurrent()) return;
                busy(false);
                status.textContent = 'Не удалось сохранить озвучку. Повторите попытку.';
            }
        });
        reset.addEventListener('click', async () => {
            busy(true);
            try {
                await request('DELETE');
                if (isCurrent()) await load('Настройка сброшена. Выбор по настройкам Jellyfin.');
            } catch {
                if (!isCurrent()) return;
                busy(false);
                status.textContent = 'Не удалось сбросить настройку. Повторите попытку.';
            }
        });
        if (initial) render(initial);
        else load();
    }

    async function getProfiles(api, user) {
        if (!user.Policy?.IsAdministrator) return [user];
        try {
            return [user, ...(await api.getUsers()).filter(other => other.Id !== user.Id)];
        } catch {
            return [user];
        }
    }

    async function mount() {
        const api = window.ApiClient;
        const userId = api?.getCurrentUserId();
        const route = location.hash;
        const query = new URLSearchParams(route.split('?')[1] || '');
        const itemId = route.includes('/details?') ? query.get('id') : null;
        const page = [...document.querySelectorAll('.itemDetailPage')].find(p => !p.classList.contains('hide') && p.getAttribute('aria-hidden') !== 'true');
        const children = page?.querySelector('#listChildrenCollapsible');
        const pageKey = userId && itemId && children ? `${api.serverId()}/${userId}/${itemId}` : '';
        if (active?.pageKey === pageKey && active.children === children && active.route === route) return;
        const current = { pageKey, children, route };
        active = current;
        const isCurrent = () => active === current && location.hash === route && children.isConnected;
        document.querySelectorAll('[data-series-audio]').forEach(node => node.remove());
        if (!pageKey) return;
        try {
            const item = await api.getItem(userId, itemId);
            if (!isCurrent()) return;
            if (item.Type === 'Episode') {
                const note = element('p', '', 'seriesAudioDefault seriesAudioEpisode');
                note.dataset.seriesAudio = 'true';
                note.setAttribute('aria-live', 'polite');
                const media = item.MediaSources || [];
                const form = page.querySelector('.trackSelections');
                current.refreshEpisode = () => {
                    if (!isCurrent()) return;
                    const id = form?.querySelector('.selectSource')?.value;
                    const source = media.find(version => version.Id === id) || media[0];
                    renderDefault(note, [episodeDefault(source)]);
                };
                if (form) form.after(note);
                else children.before(note);
                current.refreshEpisode();
                return;
            }
            if (!['Series', 'Season'].includes(item.Type)) return;
            const user = await api.getCurrentUser();
            if (!isCurrent()) return;
            const profiles = await getProfiles(api, user);
            if (!isCurrent()) return;

            const panel = element('section', '', 'seriesAudioPanel');
            panel.dataset.seriesAudio = 'true';
            panel.hidden = true;
            const isSeries = item.Type === 'Series';
            panel.append(element('h2', isSeries ? 'Озвучки по сезонам' : 'Озвучка сезона'));
            children.before(panel);
            if (!isSeries) {
                setupSeason(panel, item.Id, api, user, profiles, isCurrent);
                return;
            }

            try {
                const seasons = await api.ajax({
                    type: 'GET',
                    url: api.getUrl(`SeriesAudio/Series/${item.Id}/Seasons`),
                    dataType: 'json'
                });
                if (!isCurrent() || !seasons.length) return;
                for (const season of seasons) {
                    const row = element('section', '', 'seriesAudioSeason');
                    const name = season.name.replace(/^Season (\d+)$/i, 'Сезон $1');
                    row.setAttribute('aria-label', name);
                    row.append(element('h3', name));
                    setupSeason(row, season.id, api, user, profiles, isCurrent, season.audio);
                    panel.append(row);
                }
                panel.hidden = false;
            } catch {
                if (!isCurrent()) return;
                const status = element('p', 'Не удалось загрузить озвучки сезонов. Обновите страницу и попробуйте снова.', 'seriesAudioStatus');
                status.setAttribute('role', 'status');
                panel.append(status);
                panel.hidden = false;
            }
        } catch (error) {
            console.warn('Series Audio: page could not be initialized', error);
        }
    }

    const schedule = () => { clearTimeout(timer); timer = setTimeout(mount, 75); };
    addEventListener('hashchange', schedule);
    document.addEventListener('viewshow', schedule);
    document.addEventListener('change', event => {
        if (event.target.matches?.('.selectSource')) active?.refreshEpisode?.();
    });
    new MutationObserver(records => {
        if (records.some(r => !r.target.closest?.('[data-series-audio]'))) schedule();
    }).observe(document.documentElement, { childList: true, subtree: true });
    schedule();
})();
