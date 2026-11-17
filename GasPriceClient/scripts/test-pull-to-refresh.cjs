const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const ts = require('typescript');

class DomNode extends EventTarget {
  removeEventListener(type, listener, options) {
    super.removeEventListener(type, listener, typeof options === 'boolean' ? { capture: options } : options);
  }
  parentElement = null;
}
class Element extends DomNode {
  style = { overscrollBehaviorY: 'auto', overflowX: '', overflowY: '' };
  scrollTop = 0;
  scrollHeight = 100; clientHeight = 100;
  scrollWidth = 100; clientWidth = 100;
  parentElement = null;
  tagName = 'div'; role = null; editable = false; ignored = false;
  ownerDocument = { selection: null, getSelection() { return this.selection; } };
  contains(node) {
    for (let current = node; current; current = current.parentElement) if (current === this) return true;
    return false;
  }
  closest(selector) {
    const matches = selector.split(',').some(value => {
      const rule = value.trim();
      return rule === this.tagName || (this.role && rule === `[role="${this.role}"]`) ||
        (this.editable && rule.startsWith('[contenteditable]')) ||
        (this.ignored && rule === '[data-pull-refresh-ignore]');
    });
    return matches ? this : this.parentElement?.closest(selector) ?? null;
  }
}
const exportsObject = {};
const source = fs.readFileSync(path.join(__dirname, '../src/scroll/pull-to-refresh.ts'), 'utf8');
vm.runInNewContext(ts.transpileModule(source, { compilerOptions: {
  module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022,
} }).outputText, { exports: exportsObject, HTMLElement: Element, Element, Node: DomNode, getComputedStyle: node => node.style });

function harness(refresh) {
  const node = new Element();
  let enabled = true, calls = 0, distance = 0, busy = false, pulling = false;
  const detach = exportsObject.attachPullToRefresh(node, {
    canRefresh: () => enabled,
    onRefresh: () => { calls++; return refresh?.(); },
    onPull: (value, dragging) => { distance = value; pulling = dragging; },
    onBusy: value => { busy = value; },
  });
  const send = (type, x = 0, y = 0, extra = {}) => {
    const event = new Event(type, { cancelable: true });
    Object.defineProperty(event, 'touches', { configurable: true, value: [{ clientX: x, clientY: y, identifier: 1 }] });
    for (const [key, value] of Object.entries(extra)) Object.defineProperty(event, key, { value });
    node.dispatchEvent(event);
    return event;
  };
  const pull = (y = 160, x = 0) => { send('touchstart'); send('touchmove', x, y); send('touchend'); };
  return { node, send, pull, detach, disable: () => { enabled = false; },
    isPulling: () => pulling, state: () => ({ calls, distance, busy }) };
}
const settle = () => new Promise(resolve => setImmediate(resolve));
(async () => {
  let finish;
  const h = harness(() => new Promise(resolve => { finish = resolve; }));
  h.send('touchstart');
  assert.equal(h.send('touchmove', 0, 160).defaultPrevented, true);
  assert.equal(h.state().distance, exportsObject.rubberBandDistance(160));
  assert.equal(h.isPulling(), true, 'follow finger during pull');
  const rubber = exportsObject.rubberBandDistance;
  assert.equal(rubber(0), 0);
  assert.equal(rubber(-50), 0);
  assert(rubber(160) >= exportsObject.pullRefreshThreshold, 'normal pull reaches refresh');
  assert(rubber(320) > rubber(160), 'content keeps moving on a longer pull');
  assert(rubber(320) - rubber(160) < rubber(160), 'resistance grows with travel');
  assert(rubber(10000) <= 120, 'overscroll stays bounded');
  h.send('touchend');
  await settle();
  assert.deepEqual(h.state(), { calls: 1, distance: 0, busy: true });
  assert.equal(h.isPulling(), false, 'release enables settling animation');
  h.pull(); await settle(); assert.equal(h.state().calls, 1, 'deduplicate in-flight refresh');
  finish(); await settle(); assert.equal(h.state().busy, false);
  h.pull(50); await settle(); assert.equal(h.state().calls, 1, 'short pull cancels');
  h.pull(80, 160); await settle(); assert.equal(h.state().calls, 1, 'horizontal swipe cancels');
  h.node.scrollTop = 50; h.pull(); await settle(); assert.equal(h.state().calls, 1, 'must start at top');
  h.node.scrollTop = 0;
  h.send('touchstart'); h.send('touchmove', 0, 160); h.send('touchcancel'); h.send('touchend');
  await settle(); assert.equal(h.state().calls, 1, 'cancel never refreshes');
  h.send('touchstart'); h.send('touchmove', 0, 160, { touches: [{}, {}] }); h.send('touchend');
  await settle(); assert.equal(h.state().calls, 1, 'pinch never refreshes');
  const child = new Element(); child.parentElement = h.node; child.tagName = 'input';
  h.send('touchstart', 0, 0, { target: child }); h.send('touchmove', 0, 160); h.send('touchend');
  await settle(); assert.equal(h.state().calls, 1, 'interactive control keeps its gesture');
  child.tagName = 'div'; child.style.overflowY = 'auto'; child.scrollHeight = 200;
  h.send('touchstart', 0, 0, { target: child }); h.send('touchmove', 0, 160); h.send('touchend');
  await settle(); assert.equal(h.state().calls, 1, 'nested scroll keeps its gesture');
  h.disable(); h.pull(); await settle(); assert.equal(h.state().calls, 1, 'disabled refresh ignored');
  h.detach(); assert.equal(h.node.style.overscrollBehaviorY, 'auto');
  const textPull = harness();
  const textElement = new Element(); textElement.parentElement = textPull.node;
  const textNode = new DomNode(); textNode.parentElement = textElement;
  textPull.send('touchstart', 0, 0, { target: textNode });
  assert.equal(textPull.send('touchmove', 0, 2).defaultPrevented, true,
    'claim a slow downward pull before browser scrolling takes over');
  assert.equal(textPull.state().distance, 0, 'tiny movement does not show indicator');
  textPull.send('touchmove', 0, 160); textPull.send('touchend'); await settle();
  assert.equal(textPull.state().calls, 1, 'pull can begin on an ordinary text node');
  textPull.send('touchstart', 0, 0, { target: textNode });
  textPull.send('touchmove', 0, 160); textPull.send('touchmove', 0, 2); textPull.send('touchend');
  await settle();
  assert.equal(textPull.state().calls, 1, 'retreating below threshold cancels a text pull');
  textElement.tagName = 'input';
  textPull.send('touchstart', 0, 0, { target: textNode });
  assert.equal(textPull.send('touchmove', 0, 160).defaultPrevented, false,
    'text inside a control preserves its gesture');
  textPull.send('touchend'); await settle();
  assert.equal(textPull.state().calls, 1, 'text inside a control cannot refresh');
  textPull.detach();
  for (const controlType of ['button', 'a', 'role-button', 'role-link']) {
    const controlPull = harness();
    const control = new Element(); control.parentElement = controlPull.node;
    if (controlType.startsWith('role-')) control.role = controlType.slice(5);
    else control.tagName = controlType;
    const label = new Element(); label.parentElement = control;
    const target = new DomNode(); target.parentElement = label;
    let activations = 0;
    controlPull.node.addEventListener('click', () => { activations++; });
    const click = detail => controlPull.send('click', 0, 0, { target: control, detail });
    controlPull.send('touchstart', 0, 0, { target });
    controlPull.send('touchend');
    assert.equal(click(1).defaultPrevented, false, `${controlType} tap is preserved`);
    assert.equal(activations, 1);
    controlPull.send('touchstart', 0, 0, { target });
    assert.equal(controlPull.send('touchmove', 0, 2).defaultPrevented, false, 'small tap jitter is preserved');
    controlPull.send('touchend'); click(1);
    assert.equal(activations, 2);
    controlPull.send('touchstart', 0, 0, { target });
    controlPull.send('touchmove', 0, 160); controlPull.send('touchend'); await settle();
    assert.equal(controlPull.state().calls, 1, `${controlType} can start refresh`);
    assert.equal(click(1).defaultPrevented, true, 'drag suppresses navigation/activation');
    assert.equal(activations, 2);
    controlPull.send('touchstart', 0, 0, { target });
    controlPull.send('touchmove', 0, 30); controlPull.send('touchend'); await settle();
    assert.equal(controlPull.state().calls, 1, 'short drag does not refresh');
    assert.equal(click(1).defaultPrevented, true, 'short drag also cancels activation');
    controlPull.send('touchstart', 0, 0, { target });
    controlPull.send('touchmove', 0, 160); controlPull.send('touchend'); await settle();
    assert.equal(click(0).defaultPrevented, false, 'keyboard activation stays available');
    controlPull.send('touchstart', 0, 0, { target }); controlPull.send('touchend');
    assert.equal(click(1).defaultPrevented, false, 'next deliberate tap is independent');
    controlPull.detach();
  }
  for (const kind of ['input', 'textarea', 'select', 'slider', 'tab', 'checkbox', 'switch', 'editable', 'map']) {
    const protectedPull = harness();
    const control = new Element(); control.parentElement = protectedPull.node;
    if (['input', 'textarea', 'select'].includes(kind)) control.tagName = kind;
    else if (kind === 'editable') control.editable = true;
    else if (kind === 'map') control.ignored = true;
    else control.role = kind;
    protectedPull.send('touchstart', 0, 0, { target: control });
    assert.equal(protectedPull.send('touchmove', 0, 160).defaultPrevented, false, `${kind} keeps its gesture`);
    protectedPull.send('touchend'); await settle();
    assert.equal(protectedPull.state().calls, 0);
    protectedPull.detach();
  }
  const selected = harness();
  const selectedText = new Element(); selectedText.parentElement = selected.node;
  selected.node.ownerDocument.selection = { isCollapsed: false, anchorNode: selectedText, focusNode: selectedText };
  selected.pull(); await settle(); assert.equal(selected.state().calls, 0, 'preserve active text selection');
  selected.detach();
  const detached = harness(); detached.detach(); detached.pull(); await settle();
  assert.equal(detached.state().calls, 0, 'cleanup removes listeners');
  const failed = harness(() => Promise.reject(new Error('offline')));
  failed.pull(); await settle(); assert.equal(failed.state().busy, false);
  failed.pull(); await settle(); assert.equal(failed.state().calls, 2, 'failure permits retry');
  failed.detach();
  console.log('Pull-to-refresh gesture checks passed.');
})().catch(error => { console.error(error); process.exitCode = 1; });
