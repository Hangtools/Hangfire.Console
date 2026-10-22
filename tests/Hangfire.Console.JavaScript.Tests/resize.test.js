const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const { JSDOM } = require('jsdom');

const script = fs.readFileSync(path.join(__dirname, '../../src/Hangfire.Console/Resources/resize.min.js'), 'utf8');

test('removes the real resize scroll listener when its last subscriber is removed', () => {
    const dom = new JSDOM('<div class="line-buffer"><div class="line">Message</div></div>', {
        runScripts: 'outside-only'
    });
    const window = dom.window;
    const buffer = window.document.querySelector('.line-buffer');
    const scrollListeners = [];
    const addEventListener = buffer.addEventListener.bind(buffer);
    const removeEventListener = buffer.removeEventListener.bind(buffer);
    const captureValue = options => typeof options === 'boolean' ? options : Boolean(options && options.capture);

    buffer.addEventListener = (type, listener, options) => {
        if (type === 'scroll') {
            scrollListeners.push({ listener, capture: captureValue(options) });
        }
        addEventListener(type, listener, options);
    };
    buffer.removeEventListener = (type, listener, options) => {
        if (type === 'scroll') {
            const index = scrollListeners.findIndex(entry => entry.listener === listener && entry.capture === captureValue(options));
            if (index !== -1) {
                scrollListeners.splice(index, 1);
            }
        }
        removeEventListener(type, listener, options);
    };

    const errors = [];
    window.addEventListener('error', event => {
        errors.push(event.error.message);
        event.preventDefault();
    });

    try {
        window.eval(script);
        const first = () => {};
        const second = () => {};
        window.addResizeListener(buffer, first);
        window.addResizeListener(buffer, second);
        assert.equal(scrollListeners.length, 1);
        assert.equal(buffer.querySelectorAll('.resize-triggers').length, 1);

        window.removeResizeListener(buffer, first);
        assert.equal(scrollListeners.length, 1);
        assert.equal(buffer.querySelectorAll('.resize-triggers').length, 1);

        window.removeResizeListener(buffer, second);
        buffer.querySelector('.line').dispatchEvent(new window.Event('scroll'));
        assert.deepEqual(errors, [], 'Scrolling after teardown must not call the removed resize handler');
        assert.equal(scrollListeners.length, 0);
        assert.equal(buffer.querySelectorAll('.resize-triggers').length, 0);
    } finally {
        window.close();
    }
});
