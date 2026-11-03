const workspaces = new Map();
const SNAP = 10;
const MIN_WIDTH = 320;
const MIN_HEIGHT = 220;
const INTERACTIVE = 'button, a, input, textarea, select, .mud-select, .mud-input-control, .mud-menu';

export function init(id, element, dotnet) {
    if (!(element instanceof Element)) {
        return false;
    }

    const state = { element, dotnet, action: null, topZ: 10 };

    state.onDown = event => {
        if (event.button !== 0 || window.matchMedia('(max-width: 899px)').matches) {
            return;
        }

        const panel = event.target.closest('.ws-panel');

        if (!panel || panel.classList.contains('maximized')) {
            return;
        }

        const resizing = event.target.closest('.ws-resize') !== null;
        const header = event.target.closest('.chart-panel-header');
        const moving = !resizing && header !== null && event.target.closest(INTERACTIVE) === null;

        state.topZ += 1;
        panel.style.zIndex = state.topZ;
        dotnet.invokeMethodAsync('OnFocused', panel.dataset.panelId, state.topZ);

        if (!resizing && !moving) {
            return;
        }

        event.preventDefault();
        state.action = {
            kind: resizing ? 'resize' : 'move',
            panel,
            startX: event.clientX,
            startY: event.clientY,
            left: panel.offsetLeft,
            top: panel.offsetTop,
            width: panel.offsetWidth,
            height: panel.offsetHeight
        };
        panel.classList.add('dragging');
    };

    state.onMove = event => {
        const action = state.action;

        if (!action) {
            return;
        }

        const dx = event.clientX - action.startX;
        const dy = event.clientY - action.startY;
        const snap = value => Math.round(value / SNAP) * SNAP;
        const style = action.panel.style;

        if (action.kind === 'move') {
            style.left = `${Math.max(0, snap(action.left + dx))}px`;
            style.top = `${Math.max(0, snap(action.top + dy))}px`;
        } else {
            style.width = `${Math.max(MIN_WIDTH, snap(action.width + dx))}px`;
            style.height = `${Math.max(MIN_HEIGHT, snap(action.height + dy))}px`;
        }
    };

    state.onUp = () => {
        const action = state.action;

        if (!action) {
            return;
        }

        state.action = null;
        action.panel.classList.remove('dragging');
        dotnet.invokeMethodAsync(
            'OnGeometry',
            action.panel.dataset.panelId,
            action.panel.offsetLeft,
            action.panel.offsetTop,
            action.panel.offsetWidth,
            action.panel.offsetHeight);
    };

    element.addEventListener('pointerdown', state.onDown);
    window.addEventListener('pointermove', state.onMove);
    window.addEventListener('pointerup', state.onUp);
    window.addEventListener('pointercancel', state.onUp);
    workspaces.set(id, state);
    return true;
}

export function measure(element) {
    const top = element.getBoundingClientRect().top;
    return [element.clientWidth, Math.max(480, window.innerHeight - top - 16)];
}

export function dispose(id) {
    const state = workspaces.get(id);

    if (!state) {
        return;
    }

    state.element.removeEventListener('pointerdown', state.onDown);
    window.removeEventListener('pointermove', state.onMove);
    window.removeEventListener('pointerup', state.onUp);
    window.removeEventListener('pointercancel', state.onUp);
    workspaces.delete(id);
}
