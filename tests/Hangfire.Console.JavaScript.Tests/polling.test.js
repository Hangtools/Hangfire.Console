const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const { JSDOM } = require('jsdom');
const createJQuery = require('jquery');

const script = fs.readFileSync(path.join(__dirname, '../../src/Hangfire.Console/Resources/script.js'), 'utf8');

function openJobHistory(sessions) {
    const html = sessions.map(({ id, cursor, empty = false }) =>
        `<div class="console" data-id="${id}"><div class="line-buffer" data-n="${cursor}">${empty ? '' : '<div class="line">Message</div>'}</div></div>`
    ).join('');
    const dom = new JSDOM(html, {
        url: 'https://example.test/hangfire/jobs/details/1',
        runScripts: 'outside-only'
    });
    const window = dom.window;
    const $ = createJQuery(window);
    const timers = [];
    const requests = [];
    const resizeListeners = new Set();
    window.$ = window.jQuery = $;
    window.Hangfire = { config: { consolePollUrl: '/hangfire/console/', consolePollInterval: 1000 } };
    window.addResizeListener = element => resizeListeners.add(element);
    window.removeResizeListener = element => resizeListeners.delete(element);
    window.console.log = () => {};
    window.setTimeout = (callback, delay = 0) => {
        timers.push({ callback, delay });
        return timers.length;
    };
    $.fn.tooltip = function () { return this; };
    $.get = (url, data, success) => {
        const request = { url, data, respond: success };
        requests.push(request);
        return { always(callback) { request.finish = callback; } };
    };

    window.eval(script);
    window.document.dispatchEvent(new window.Event('DOMContentLoaded'));
    for (let index = 0; index < timers.length; index++) {
        if (timers[index].delay === 0) timers[index].callback();
    }

    return { dom, $, timers, requests, resizeListeners };
}

function tickPoll(timers) {
    const index = timers.findIndex(timer => timer.delay === 1000);
    assert.notEqual(index, -1, 'Expected a scheduled console poll');
    timers.splice(index, 1)[0].callback();
}

for (const currentFirst of [true, false]) {
    test(`polls the current session when it is ${currentFirst ? 'first' : 'last'} in history`, () => {
        const sessions = [{ id: 'current', cursor: 5 }, { id: 'old', cursor: -1 }];
        if (!currentFirst) sessions.reverse();
        const { dom, $, timers, requests } = openJobHistory(sessions);
        try {
            assert.equal($('.console.active').data('id'), 'current');
            assert.equal($('.console.collapsed').data('id'), 'old');
            assert.equal(timers.filter(timer => timer.delay === 1000).length, 1);
            timers.find(timer => timer.delay === 1000).callback();
            assert.equal(requests.length, 1);
            assert.equal(requests[0].url, '/hangfire/console/current');
            assert.equal(requests[0].data.start, 5);
        } finally {
            dom.window.close();
        }
    });
}

test('keeps the first completed session expanded without polling', () => {
    const { dom, $, timers, requests } = openJobHistory([
        { id: 'completed', cursor: -1 }, { id: 'old', cursor: -1 }
    ]);
    try {
        assert.equal($('.console.active').length, 0);
        assert.equal($('.console.collapsed').data('id'), 'old');
        assert.equal(timers.filter(timer => timer.delay === 1000).length, 0);
        assert.equal(requests.length, 0);
    } finally {
        dom.window.close();
    }
});

test('polls a newly started session with an empty buffer', () => {
    const { dom, $, timers, requests } = openJobHistory([
        { id: 'old', cursor: -1 }, { id: 'new', cursor: 0, empty: true }
    ]);
    try {
        assert.equal($('.console[data-id="new"] .line').length, 0);
        assert.equal($('.console.active').data('id'), 'new');
        assert.equal(timers.filter(timer => timer.delay === 1000).length, 1);
        tickPoll(timers);
        assert.equal(requests[0].url, '/hangfire/console/new');
        assert.equal(requests[0].data.start, 0);
        requests[0].respond('<div class="line-buffer" data-n="1"><div class="line">First message</div></div>');
        requests[0].finish();
        assert.equal($('.console[data-id="new"] .line').text(), 'First message');
        tickPoll(timers);
        assert.equal(requests[1].data.start, 1);
        assert.equal(requests[1].url, '/hangfire/console/new');
    } finally {
        dom.window.close();
    }
});

test('stops polling and releases the resize listener when the parent job expires', () => {
    const { dom, $, timers, requests, resizeListeners } = openJobHistory([
        { id: 'current', cursor: 1 }
    ]);
    try {
        assert.equal(resizeListeners.size, 1);
        tickPoll(timers);
        requests[0].respond('<div class="line-buffer" data-n="-2"></div>');
        requests[0].finish();
        tickPoll(timers);

        assert.equal(requests.length, 1);
        assert.equal($('.console.active, .console.waiting').length, 0);
        assert.equal(resizeListeners.size, 0);
        assert.equal(timers.filter(timer => timer.delay === 1000).length, 0);
    } finally {
        dom.window.close();
    }
});
