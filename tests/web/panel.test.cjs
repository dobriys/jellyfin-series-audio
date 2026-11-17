const { test } = require('node:test');
const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { JSDOM } = require('jsdom');
const script = readFileSync('src/Jellyfin.Plugin.SeriesAudio/Web/series-audio.js', 'utf8');

const choice = { preference: { title: 'HDRezka Studio 18+', language: 'rus' }, episodeId: 'episode', mediaSourceId: 'source', audioStreamIndex: 6 };
function fixture(admin = false) {
    const dom = new JSDOM('<div class="itemDetailPage"><div class="detailPagePrimaryContainer"></div></div>', {
        url: 'http://localhost/web/#/details?id=episode', runScripts: 'outside-only'
    });
    const requests = [];
    let preference = null;
    const api = {
        getCurrentUserId: () => 'self', serverId: () => 'server',
        getItem: async () => ({ Id: 'episode', Type: 'Episode', SeriesId: 'series' }),
        getCurrentUser: async () => ({ Id: 'self', Name: 'User', Policy: { IsAdministrator: admin } }),
        getUsers: async () => [{ Id: 'self', Name: 'User' }, { Id: 'other', Name: 'Other' }],
        getUrl: (path, params) => path + '?' + new URLSearchParams(params),
        ajax: async options => {
            requests.push(options);
            if (options.type === 'PUT') preference = choice.preference;
            if (options.type === 'DELETE') preference = null;
            return { preference, choices: [choice], sampledEpisodes: 2, hasMoreEpisodes: false };
        }
    };
    dom.window.ApiClient = api;
    dom.window.eval(script);
    return { dom, api, requests, document: dom.window.document };
}
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
async function ready(f) {
    for (let i = 0; i < 40; i++) {
        if (f.document.querySelector('select[aria-label="Озвучка для всего сериала"]')?.options.length) return;
        await delay(25);
    }
    assert.fail('Panel did not load');
}

test('saves the chosen track, resets it and never renders duplicate panels', async () => {
    const f = fixture();
    try {
        await ready(f);
        const audio = f.document.querySelector('select[aria-label="Озвучка для всего сериала"]');
        const buttons = f.document.querySelectorAll('[data-series-audio] button');
        assert.equal(buttons[0].disabled, true);
        audio.value = '0';
        audio.dispatchEvent(new f.dom.window.Event('change'));
        buttons[0].click();
        await delay(20);
        assert.deepEqual(JSON.parse(f.requests.find(r => r.type === 'PUT').data), { episodeId: 'episode', mediaSourceId: 'source', audioStreamIndex: 6 });
        assert.match(f.document.querySelector('[role=status]').textContent, /Сохранено: HDRezka Studio 18\+/);
        buttons[1].click();
        await delay(20);
        assert.ok(f.requests.some(r => r.type === 'DELETE'));
        f.dom.window.eval(script);
        await delay(250);
        assert.equal(f.document.querySelectorAll('[data-series-audio]').length, 1);
    } finally { f.dom.window.close(); }
});

test('admin explicitly selects another profile; regular user has only their own', async () => {
    const f = fixture(true); const regular = fixture(false);
    try {
        await ready(f); await ready(regular);
        const profile = f.document.querySelector('select[aria-label="Профиль для озвучки"]');
        assert.equal(profile.options.length, 2);
        assert.equal(regular.document.querySelector('select[aria-label="Профиль для озвучки"]').options.length, 1);
        profile.value = 'other'; profile.dispatchEvent(new f.dom.window.Event('change'));
        await delay(20);
        assert.match(f.requests.at(-1).url, /userId=other/);
    } finally { f.dom.window.close(); regular.dom.window.close(); }
});

test('navigation away removes profile controls', async () => {
    const f = fixture();
    try {
        await ready(f);
        f.dom.window.location.hash = '/home';
        await delay(250);
        assert.equal(f.document.querySelector('[data-series-audio]'), null);
    } finally { f.dom.window.close(); }
});

test('unavailable plugin produces an actionable message and disables writes', async () => {
    const f = fixture();
    try {
        f.api.ajax = async () => { throw new Error('404'); };
        await delay(250);
        assert.match(f.document.querySelector('[role=status]').textContent, /плагин Series Audio установлен/);
        assert.ok([...f.document.querySelectorAll('[data-series-audio] button')].every(b => b.disabled));
    } finally { f.dom.window.close(); }
});

test('track names are rendered as text, never executable markup', async () => {
    const f = fixture();
    try {
        f.api.ajax = async () => ({ preference: null, choices: [{ ...choice, preference: { title: '<img src=x onerror=alert(1)>', language: 'rus' } }], hasMoreEpisodes: false });
        await ready(f);
        assert.equal(f.document.querySelector('[data-series-audio] img'), null);
        assert.match(f.document.querySelector('select[aria-label="Озвучка для всего сериала"]').textContent, /<img/);
    } finally { f.dom.window.close(); }
});
