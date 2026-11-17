const { test } = require('node:test');
const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { JSDOM } = require('jsdom');
const script = readFileSync('src/Jellyfin.Plugin.SeriesAudio/Web/series-audio.js', 'utf8');

const choice = { preference: { title: 'HDRezka Studio 18+', language: 'rus' }, episodeId: 'episode', mediaSourceId: 'source', audioStreamIndex: 6 };
const english = { ...choice, preference: { title: '', language: 'eng' }, audioStreamIndex: 7 };
function fixture(admin = false, type = 'Season') {
    const dom = new JSDOM('<div class="itemDetailPage"><div class="detailPagePrimaryContainer"><div id="listChildrenCollapsible"></div></div></div>', {
        url: 'http://localhost/web/#/details?id=season', runScripts: 'outside-only'
    });
    const requests = [];
    let preference = null;
    const api = {
        getCurrentUserId: () => 'self', serverId: () => 'server',
        getItem: async (_, id) => ({ Id: id, Type: id === 'next-episode' ? 'Episode' : type, SeriesId: 'series' }),
        getCurrentUser: async () => ({ Id: 'self', Name: 'User', Policy: { IsAdministrator: admin } }),
        getUsers: async () => [{ Id: 'self', Name: 'User' }, { Id: 'other', Name: 'Other' }],
        getUrl: (path, params) => path + '?' + new URLSearchParams(params),
        ajax: async options => {
            requests.push(options);
            if (options.type === 'PUT') preference = choice.preference;
            if (options.type === 'DELETE') preference = null;
            return { preference, choices: [choice, english], defaults: [preference || english.preference] };
        }
    };
    dom.window.ApiClient = api;
    dom.window.eval(script);
    return { dom, api, requests, document: dom.window.document };
}
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
async function ready(f) {
    for (let i = 0; i < 40; i++) {
        if (f.document.querySelector('select[aria-label="Аудиодорожка"]')?.options.length > 1) return;
        await delay(25);
    }
    assert.fail('Panel did not load');
}

test('saves the chosen track, resets it and never renders duplicate panels', async () => {
    const f = fixture();
    try {
        await ready(f);
        const audio = f.document.querySelector('select[aria-label="Аудиодорожка"]');
        const buttons = f.document.querySelectorAll('[data-series-audio] button');
        const defaultLine = f.document.querySelector('.seriesAudioDefault');
        assert.match(defaultLine.textContent, /По умолчанию.*Английский/);
        assert.equal(buttons[0].disabled, true);
        assert.match(f.requests.at(-1).url, /^SeriesAudio\/Season\/season\?/);
        assert.equal(f.document.querySelector('#listChildrenCollapsible').previousElementSibling.dataset.seriesAudio, 'true');
        assert.equal(audio.closest('section').hidden, false);
        audio.value = '0';
        audio.dispatchEvent(new f.dom.window.Event('change'));
        assert.match(defaultLine.textContent, /Английский/);
        buttons[0].click();
        await delay(20);
        assert.deepEqual(JSON.parse(f.requests.find(r => r.type === 'PUT').data), { episodeId: 'episode', mediaSourceId: 'source', audioStreamIndex: 6 });
        assert.equal(audio.selectedOptions[0].textContent, 'HDRezka Studio 18+ (русский)');
        assert.match(defaultLine.textContent, /По умолчанию.*HDRezka Studio 18\+/);
        buttons[1].click();
        await delay(20);
        assert.ok(f.requests.some(r => r.type === 'DELETE'));
        assert.match(defaultLine.textContent, /По умолчанию.*Английский/);
        f.dom.window.eval(script);
        await delay(250);
        assert.equal(f.document.querySelectorAll('[data-series-audio]').length, 1);
    } finally { f.dom.window.close(); }
});

test('admin explicitly selects another profile; regular user uses their own profile', async () => {
    const f = fixture(true); const regular = fixture(false);
    try {
        await ready(f); await ready(regular);
        const profile = f.document.querySelector('select[aria-label="Профиль"]');
        assert.equal(profile.options.length, 2);
        assert.equal(regular.document.querySelector('select[aria-label="Профиль"]'), null);
        assert.match(regular.requests.at(-1).url, /userId=self/);
        profile.value = 'other'; profile.dispatchEvent(new f.dom.window.Event('change'));
        await delay(20);
        assert.match(f.requests.at(-1).url, /userId=other/);
    } finally { f.dom.window.close(); regular.dom.window.close(); }
});

test('navigation from season to episode removes profile controls', async () => {
    const f = fixture();
    try {
        await ready(f);
        f.dom.window.location.hash = '/details?id=next-episode';
        await delay(250);
        const note = f.document.querySelector('[data-series-audio]');
        assert.ok(note.matches('.seriesAudioEpisode'));
        assert.equal(note.querySelector('select'), null);
    } finally { f.dom.window.close(); }
});

test('unavailable plugin produces an actionable message and disables writes', async () => {
    const f = fixture();
    try {
        f.api.ajax = async () => { throw new Error('404'); };
        await delay(250);
        assert.match(f.document.querySelector('[role=status]').textContent, /Не удалось загрузить озвучки/);
        assert.ok([...f.document.querySelectorAll('[data-series-audio] button')].every(b => b.disabled));
    } finally { f.dom.window.close(); }
});

test('track names are rendered as text, never executable markup', async () => {
    const f = fixture();
    try {
        f.api.ajax = async () => ({ preference: null, choices: [{ ...choice, preference: { title: '<img src=x onerror=alert(1)>', language: 'rus' } }, english] });
        await ready(f);
        assert.equal(f.document.querySelector('[data-series-audio] img'), null);
        assert.match(f.document.querySelector('select[aria-label="Аудиодорожка"]').textContent, /<img/);
    } finally { f.dom.window.close(); }
});

test('shows only the default for a season with one track and hides an empty season', async () => {
    const f = fixture();
    try {
        const results = { season: [choice], empty: [], multiple: [choice, english] };
        f.api.ajax = async ({ url }) => {
            const choices = results[url.split('/')[2].split('?')[0]];
            return { preference: null, choices, defaults: choices.slice(0, 1).map(track => track.preference) };
        };
        for (const id of ['season', 'empty']) {
            f.dom.window.location.hash = '/details?id=' + id;
            await delay(150);
            const panel = f.document.querySelector('[data-series-audio]');
            assert.equal(panel.querySelector('.seriesAudioControls').hidden, true);
            assert.equal(panel.hidden, id === 'empty');
            if (id === 'season') assert.match(panel.querySelector('.seriesAudioDefault').textContent, /По умолчанию.*HDRezka/);
        }
        f.dom.window.location.hash = '/details?id=multiple';
        await ready(f);
        assert.equal(f.document.querySelector('[data-series-audio]').hidden, false);
    } finally { f.dom.window.close(); }
});

test('an admin can switch back after opening a profile with one choice', async () => {
    const f = fixture(true);
    try {
        const ajax = f.api.ajax;
        f.api.ajax = options => options.url.endsWith('userId=other')
            ? Promise.resolve({ preference: null, choices: [choice] }) : ajax(options);
        await ready(f);
        const profile = f.document.querySelector('select[aria-label="Профиль"]');
        const audioField = f.document.querySelector('.seriesAudioTrack');
        profile.value = 'other';
        profile.dispatchEvent(new f.dom.window.Event('change'));
        await delay(20);
        assert.equal(audioField.hidden, true);
        assert.equal(profile.disabled, false);
        profile.value = 'self';
        profile.dispatchEvent(new f.dom.window.Event('change'));
        await ready(f);
        assert.equal(audioField.hidden, false);
    } finally { f.dom.window.close(); }
});

test('shows the episode default next to the native tracks and follows video version changes', async () => {
    const f = fixture(false, 'Episode');
    try {
        const form = f.document.createElement('form');
        form.className = 'trackSelections';
        form.innerHTML = '<select class="selectSource"><option value="first">First</option><option value="second">Second</option></select>';
        f.document.querySelector('#listChildrenCollapsible').before(form);
        f.api.getItem = async () => ({ Type: 'Episode', MediaSources: [
            { Id: 'first', DefaultAudioStreamIndex: 6, MediaStreams: [
                { Type: 'Audio', Index: 1, Title: 'Original', Language: 'eng', IsDefault: true },
                { Type: 'Audio', Index: 6, Title: 'HDRezka Studio 18+', Language: 'rus' }
            ] },
            { Id: 'second', DefaultAudioStreamIndex: 1, MediaStreams: [{ Type: 'Audio', Index: 1, Title: 'Unknown', Language: 'eng' }] }
        ] });
        await delay(250);
        const note = form.nextElementSibling;
        assert.match(note.textContent, /По умолчанию.*HDRezka Studio 18\+/);
        form.querySelector('select').value = 'second';
        form.querySelector('select').dispatchEvent(new f.dom.window.Event('change', { bubbles: true }));
        assert.match(note.textContent, /По умолчанию.*Английский \(аудиодорожка\)/);
        assert.deepEqual(f.requests, []);
    } finally { f.dom.window.close(); }
});

test('keeps profile, track list and saves independent for each season on the series page', async () => {
    const f = fixture(true, 'Series');
    try {
        const japanese = { ...choice, preference: { title: 'Season 2 Studio', language: 'jpn' }, episodeId: 'episode-s2' };
        const catalogs = { first: [choice, english], second: [japanese, english], third: [english] };
        const saved = { 'self/first': choice.preference };
        f.api.ajax = async options => {
            f.requests.push(options);
            if (options.url === 'SeriesAudio/Series/season/Seasons?') {
                return Object.entries(catalogs).map(([id, choices], index) => ({
                    id, name: `Season ${index + 1}`, audio: { choices, preference: saved[`self/${id}`], defaults: choices.map(track => track.preference) }
                }));
            }
            const [path, query] = options.url.split('?');
            const id = path.split('/').at(-1);
            const profileId = new URLSearchParams(query).get('userId');
            const key = `${profileId}/${id}`;
            if (options.type === 'PUT') {
                const selection = JSON.parse(options.data);
                saved[key] = catalogs[id].find(track => track.episodeId === selection.episodeId).preference;
            }
            return { choices: catalogs[id], preference: saved[key] };
        };
        await ready(f);
        const panel = f.document.querySelector('[data-series-audio]');
        assert.equal(panel.hidden, false);
        assert.equal(panel.nextElementSibling.id, 'listChildrenCollapsible');
        const rows = [...panel.querySelectorAll('.seriesAudioSeason')];
        assert.deepEqual(rows.map(row => row.querySelector('h3').textContent), ['Сезон 1', 'Сезон 2', 'Сезон 3']);
        assert.match(rows[0].querySelector('.seriesAudioDefault').textContent, /Зависит от серии.*HDRezka.*Английский/);
        assert.match(rows[2].querySelector('.seriesAudioDefault').textContent, /По умолчанию.*Английский/);
        assert.equal(rows[2].querySelector('.seriesAudioControls').hidden, true);
        const [[firstProfile, firstAudio], [secondProfile, secondAudio]] = rows.map(row => row.querySelectorAll('select'));
        assert.deepEqual(f.requests.map(request => request.url), ['SeriesAudio/Series/season/Seasons?']);
        assert.equal(firstAudio.selectedOptions[0].textContent, 'HDRezka Studio 18+ (русский)');
        assert.doesNotMatch(firstAudio.textContent, /Season 2 Studio/);
        assert.doesNotMatch(secondAudio.textContent, /HDRezka/);

        secondProfile.value = 'other';
        secondProfile.dispatchEvent(new f.dom.window.Event('change'));
        await delay(20);
        secondAudio.value = '0';
        secondAudio.dispatchEvent(new f.dom.window.Event('change'));
        rows[1].querySelector('button').click();
        await delay(20);
        assert.equal(f.requests.find(request => request.type === 'PUT').url, 'SeriesAudio/Season/second?userId=other');
        assert.equal(saved['other/second'], japanese.preference);
        assert.equal(firstProfile.value, 'self');
        assert.equal(firstAudio.value, '0');
    } finally { f.dom.window.close(); }
});

test('hides the series panel when no seasons have a choice', async () => {
    const f = fixture(false, 'Series');
    try {
        f.api.ajax = async () => [];
        await delay(150);
        const panel = f.document.querySelector('[data-series-audio]');
        assert.equal(panel.hidden, true);
        assert.equal(panel.querySelectorAll('select').length, 0);
    } finally { f.dom.window.close(); }
});

test('uses full language names for unnamed tracks', async () => {
    const f = fixture();
    try {
        f.api.ajax = async () => ({
            preference: null,
            choices: ['rus', 'eng', 'jpn'].map(language => ({ ...choice, preference: { title: '', language } }))
        });
        await ready(f);
        const audio = f.document.querySelector('select[aria-label="Аудиодорожка"]');
        assert.deepEqual([...audio.options].slice(1).map(option => option.textContent), [
            'Русский (аудиодорожка)', 'Английский (аудиодорожка)', 'Японский (аудиодорожка)'
        ]);
    } finally { f.dom.window.close(); }
});
