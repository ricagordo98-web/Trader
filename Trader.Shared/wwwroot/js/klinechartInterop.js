const charts = new Map();

const periods = {
    '1m': { span: 1, type: 'minute' },
    '2m': { span: 2, type: 'minute' },
    '5m': { span: 5, type: 'minute' },
    '15m': { span: 15, type: 'minute' },
    '30m': { span: 30, type: 'minute' },
    '60m': { span: 60, type: 'minute' },
    '1d': { span: 1, type: 'day' },
    '1wk': { span: 1, type: 'week' },
    '1mo': { span: 1, type: 'month' }
};

let rectangleRegistered = false;
let extrasRegistered = false;
let compareRegistered = false;

const baseOverlay = {
    needDefaultPointFigure: true,
    needDefaultXAxisFigure: true,
    needDefaultYAxisFigure: true
};

function registerExtras() {
    if (extrasRegistered) {
        return;
    }

    window.klinecharts.registerOverlay({
        ...baseOverlay,
        name: 'arrow',
        totalStep: 3,
        createPointFigures: ({ coordinates }) => {
            if (coordinates.length < 2) {
                return [];
            }

            const [a, b] = coordinates;
            const angle = Math.atan2(b.y - a.y, b.x - a.x);
            const head = 12;
            const left = { x: b.x - head * Math.cos(angle - 0.45), y: b.y - head * Math.sin(angle - 0.45) };
            const right = { x: b.x - head * Math.cos(angle + 0.45), y: b.y - head * Math.sin(angle + 0.45) };

            return [
                { type: 'line', attrs: { coordinates: [a, b] } },
                { type: 'line', attrs: { coordinates: [left, b, right] } }
            ];
        }
    });

    window.klinecharts.registerOverlay({
        ...baseOverlay,
        name: 'circle',
        totalStep: 3,
        createPointFigures: ({ coordinates }) => {
            if (coordinates.length < 2) {
                return [];
            }

            const [center, edge] = coordinates;
            return {
                type: 'circle',
                attrs: { x: center.x, y: center.y, r: Math.hypot(edge.x - center.x, edge.y - center.y) },
                styles: { style: 'stroke_fill', color: 'rgba(76, 141, 255, 0.12)', borderColor: '#4C8DFF', borderSize: 1 }
            };
        }
    });

    window.klinecharts.registerOverlay({
        ...baseOverlay,
        name: 'triangle',
        totalStep: 4,
        createPointFigures: ({ coordinates }) => coordinates.length < 2 ? [] : {
            type: 'polygon',
            attrs: { coordinates },
            styles: { style: 'stroke_fill', color: 'rgba(76, 141, 255, 0.12)', borderColor: '#4C8DFF', borderSize: 1 }
        }
    });

    window.klinecharts.registerOverlay({
        ...baseOverlay,
        name: 'fibonacciExtension',
        totalStep: 4,
        createPointFigures: ({ overlay, coordinates, bounding }) => {
            if (coordinates.length < 3) {
                return [{ type: 'line', attrs: { coordinates } }];
            }

            const [a, b, c] = coordinates;
            const [pa, pb, pc] = overlay.points;
            const ratios = [0, 0.618, 1, 1.618, 2.618];
            const figures = [{ type: 'line', attrs: { coordinates: [a, b, c] }, styles: { style: 'dashed' } }];

            for (const ratio of ratios) {
                const y = c.y + (b.y - a.y) * ratio;
                const price = pc.value + (pb.value - pa.value) * ratio;
                figures.push({ type: 'line', attrs: { coordinates: [{ x: c.x, y }, { x: bounding.width, y }] } });
                figures.push({
                    type: 'text',
                    attrs: { x: c.x, y: y - 2, text: ratio + ' (' + price.toFixed(2) + ')', baseline: 'bottom' },
                    ignoreEvent: true
                });
            }

            return figures;
        }
    });

    extrasRegistered = true;
}

function registerCompare() {
    if (compareRegistered) {
        return;
    }

    window.klinecharts.registerIndicator({
        name: 'COMPARE',
        shortName: 'COMPARE',
        series: 'price',
        precision: 2,
        figures: [{ key: 'value', title: '', type: 'line', styles: () => ({ color: '#F59E0B', size: 2 }) }],
        calc: (dataList, indicator) => {
            const values = indicator.extendData?.values;
            if (!values || dataList.length === 0) {
                return dataList.map(() => ({}));
            }

            let mainBase = null;
            let otherBase = null;

            for (const bar of dataList) {
                const value = values[bar.timestamp];
                if (value !== undefined) {
                    mainBase = bar.close;
                    otherBase = value;
                    break;
                }
            }

            return dataList.map(bar => {
                const value = values[bar.timestamp];
                return value === undefined || otherBase === null ? {} : { value: mainBase * value / otherBase };
            });
        }
    });

    compareRegistered = true;
}

function registerRectangle() {
    if (rectangleRegistered) {
        return;
    }

    window.klinecharts.registerOverlay({
        name: 'rectangle',
        totalStep: 3,
        needDefaultPointFigure: true,
        needDefaultXAxisFigure: true,
        needDefaultYAxisFigure: true,
        createPointFigures: ({ coordinates }) => {
            if (coordinates.length < 2) {
                return [];
            }

            const [a, b] = coordinates;
            return {
                type: 'rect',
                attrs: {
                    x: Math.min(a.x, b.x),
                    y: Math.min(a.y, b.y),
                    width: Math.abs(a.x - b.x),
                    height: Math.abs(a.y - b.y)
                },
                styles: {
                    style: 'stroke_fill',
                    color: 'rgba(76, 141, 255, 0.15)',
                    borderColor: '#4C8DFF',
                    borderSize: 1
                }
            };
        }
    });

    rectangleRegistered = true;
}

function toBar(candle) {
    return {
        timestamp: typeof candle.time === 'number' ? candle.time : Date.parse(candle.time),
        open: candle.open,
        high: candle.high,
        low: candle.low,
        close: candle.close,
        volume: candle.volume
    };
}

function notifyDrawingsChanged(state) {
    if (state.suspended) {
        return;
    }

    state.dotnet.invokeMethodAsync('OnDrawingsChanged', JSON.stringify(serializeDrawings(state)));
}

function serializeDrawings(state) {
    return state.chart.getOverlays()
        .filter(o => state.drawingIds.has(o.id))
        .map(o => ({ name: o.name, extendData: o.extendData ?? null, points: o.points.map(p => ({ timestamp: p.timestamp, value: p.value })) }));
}

function createDrawing(state, definition) {
    const id = state.chart.createOverlay({
        name: definition.name,
        points: definition.points,
        extendData: definition.extendData ?? undefined,
        onDrawEnd: () => notifyDrawingsChanged(state),
        onPressedMoveEnd: () => notifyDrawingsChanged(state),
        onSelected: ({ overlay }) => { state.selectedId = overlay.id; },
        onDeselected: () => { state.selectedId = null; },
        onRemoved: ({ overlay }) => {
            state.drawingIds.delete(overlay.id);
            notifyDrawingsChanged(state);
        }
    });

    if (id) {
        state.drawingIds.add(id);
    }
}

export function create(id, container, dotnet, dark, showVolume) {
    registerRectangle();
    registerExtras();
    registerCompare();

    const chart = window.klinecharts.init(container, { timezone: 'Europe/Lisbon' });
    const state = {
        chart,
        dotnet,
        data: [],
        pushCallback: null,
        indicators: new Map(),
        indicatorConfig: new Map(),
        raw: [],
        kind: 'candle_solid',
        syncGroup: null,
        planIds: [],
        alertLineId: null,
        drawingIds: new Set(),
        levelIds: [],
        selectedId: null,
        suspended: false,
        container,
        onKeyDown: null
    };

    chart.setStyles(dark ? 'dark' : 'light');
    chart.setDataLoader({
        subscribeBar: ({ callback }) => { state.pushCallback = callback; },
        unsubscribeBar: () => { state.pushCallback = null; },
        getBars: ({ type, callback }) => {
            callback(type === 'init' ? state.data : [], false);
        }
    });

    state.onKeyDown = e => {
        if (e.key === 'Delete' && state.selectedId) {
            chart.removeOverlay({ id: state.selectedId });
            state.selectedId = null;
        }
    };
    document.addEventListener('keydown', state.onKeyDown);

    let frame = 0;
    state.observer = new ResizeObserver(() => {
        cancelAnimationFrame(frame);
        frame = requestAnimationFrame(() => chart.resize());
    });
    state.observer.observe(container);

    charts.set(id, state);
    if (showVolume) {
        chart.createIndicator({ name: 'VOL', paneId: 'vol_pane' }, false);
        chart.setPaneOptions({ id: 'vol_pane', height: 90 });
        state.indicators.set('VOL', 'vol_pane');
    }
}

function heikinOne(bar, previous) {
    const close = (bar.open + bar.high + bar.low + bar.close) / 4;
    const open = previous ? (previous.open + previous.close) / 2 : (bar.open + bar.close) / 2;
    return { ...bar, open, close, high: Math.max(bar.high, open, close), low: Math.min(bar.low, open, close) };
}

function displayData(state) {
    if (state.kind !== 'heikin') {
        return state.raw.slice();
    }

    const shown = [];
    for (const bar of state.raw) {
        shown.push(heikinOne(bar, shown[shown.length - 1]));
    }
    return shown;
}

export function setData(id, symbol, pricePrecision, interval, candles) {
    const state = charts.get(id);
    state.raw = candles.map(toBar);
    state.data = displayData(state);
    state.interval = interval;
    const flat = state.raw.length > 0 && state.raw.filter(b => b.open === b.high && b.high === b.low && b.low === b.close).length / state.raw.length > 0.8;
    state.chart.setStyles({ candle: { type: flat && state.kind === 'candle_solid' ? 'area' : (state.kind === 'heikin' ? 'candle_solid' : state.kind) } });
    state.chart.setSymbol({ ticker: symbol, pricePrecision, volumePrecision: 0 });
    state.chart.setPeriod(periods[interval] ?? periods['1d']);
    state.chart.resetData();
}

export function pushBar(id, candle) {
    const state = charts.get(id);
    if (!state || state.raw.length === 0) {
        return;
    }

    const bar = toBar(candle);
    const last = state.raw[state.raw.length - 1];

    if (bar.timestamp < last.timestamp) {
        return;
    }

    if (bar.timestamp === last.timestamp) {
        bar.high = Math.max(bar.high, last.high);
        bar.low = Math.min(bar.low, last.low);
        state.raw[state.raw.length - 1] = bar;
    } else {
        state.raw.push(bar);
    }

    const index = state.raw.length - 1;
    const shown = state.kind === 'heikin' ? heikinOne(bar, state.data[index - 1]) : bar;
    state.data[index] = shown;
    state.pushCallback?.(shown);
}

export function setChartKind(id, kind) {
    const state = charts.get(id);
    if (!state) {
        return;
    }

    state.kind = kind;
    state.chart.setStyles({ candle: { type: kind === 'heikin' ? 'candle_solid' : kind } });
    state.data = displayData(state);
    state.chart.resetData();
}

export function setYAxisType(id, type) {
    charts.get(id)?.chart.setStyles({ yAxis: { type } });
}

export function setIndicatorConfig(id, name, calcParams, color, size) {
    const state = charts.get(id);
    if (!state) {
        return;
    }

    state.indicatorConfig.set(name, { calcParams, color, size });
    applyIndicatorConfig(state, name);
}

function applyIndicatorConfig(state, name) {
    const config = state.indicatorConfig.get(name);
    const paneId = state.indicators.get(name);

    if (!config || !paneId) {
        return;
    }

    const lines = Array.from({ length: Math.max(config.calcParams.length, 3) }, () => ({
        color: config.color,
        size: config.size,
        style: 'solid',
        smooth: false,
        dashedValue: [2, 2]
    }));

    try {
        state.chart.overrideIndicator({ name, paneId, calcParams: config.calcParams.length > 0 ? config.calcParams : undefined, styles: { lines } });
    } catch (error) {
        console.warn('Indicator configuration failed', error);
    }
}

const syncGroups = new Map();

export function setSyncGroup(id, group) {
    const state = charts.get(id);
    if (!state) {
        return;
    }

    if (state.syncGroup) {
        syncGroups.get(state.syncGroup)?.delete(id);
    }

    state.syncGroup = group;

    if (!group) {
        return;
    }

    if (!syncGroups.has(group)) {
        syncGroups.set(group, new Set());
    }

    syncGroups.get(group).add(id);

    if (state.syncSubscribed) {
        return;
    }

    state.syncSubscribed = true;

    try {
        state.chart.subscribeAction('onCrosshairChange', crosshair => broadcastCrosshair(id, crosshair));
        state.chart.subscribeAction('onScroll', () => broadcastScroll(id));
        state.chart.subscribeAction('onZoom', () => broadcastScroll(id));
    } catch (error) {
        console.warn('Chart synchronisation unavailable', error);
    }
}

function peers(id) {
    const state = charts.get(id);
    if (!state?.syncGroup) {
        return [];
    }

    return Array.from(syncGroups.get(state.syncGroup) ?? []).filter(other => other !== id).map(other => charts.get(other)).filter(Boolean);
}

function broadcastCrosshair(id, crosshair) {
    const source = charts.get(id);
    if (!source || source.applyingSync || !crosshair?.timestamp) {
        return;
    }

    for (const target of peers(id)) {
        try {
            target.applyingSync = true;
            const bar = target.data.find(b => b.timestamp === crosshair.timestamp) ?? target.data.filter(b => b.timestamp <= crosshair.timestamp).pop();
            if (bar) {
                const pixel = target.chart.convertToPixel({ timestamp: bar.timestamp, value: bar.close }, { paneId: 'candle_pane' });
                if (pixel && Number.isFinite(pixel.x)) {
                    target.chart.executeAction('onCrosshairChange', { x: pixel.x, y: pixel.y, paneId: 'candle_pane' });
                }
            }
        } catch (error) {
            console.warn('Crosshair sync failed', error);
        } finally {
            target.applyingSync = false;
        }
    }
}

function broadcastScroll(id) {
    const source = charts.get(id);
    if (!source || source.applyingSync) {
        return;
    }

    try {
        const range = source.chart.getVisibleRange();
        const edge = source.data[Math.min(source.data.length - 1, Math.max(0, Math.floor(range.to) - 1))];
        const space = source.chart.getBarSpace?.();

        for (const target of peers(id)) {
            try {
                target.applyingSync = true;
                if (target.interval === source.interval && space && typeof space.bar === 'number') {
                    target.chart.setBarSpace(space.bar);
                }
                if (edge) {
                    target.chart.scrollToTimestamp(edge.timestamp);
                }
            } finally {
                target.applyingSync = false;
            }
        }
    } catch (error) {
        console.warn('Scroll sync failed', error);
    }
}

function lineOverlay(state, price, color, onMoved) {
    const timestamp = state.data.length > 0 ? state.data[state.data.length - 1].timestamp : undefined;

    return state.chart.createOverlay({
        name: 'horizontalStraightLine',
        points: [{ timestamp, value: price }],
        lock: false,
        styles: { line: { color, style: 'solid', size: 2 } },
        onPressedMoveEnd: ({ overlay }) => {
            onMoved(overlay.points[0].value);
            return false;
        }
    });
}

export function startPlan(id, entry, stop, target) {
    const state = charts.get(id);
    if (!state) {
        return;
    }

    clearPlan(id);
    const make = (which, price, color) => lineOverlay(state, price, color, value => state.dotnet.invokeMethodAsync('OnPlanChanged', which, value));
    state.planIds = [make('entry', entry, '#4C8DFF'), make('stop', stop, '#EF4444'), make('target', target, '#22C55E')].filter(Boolean);
}

export function clearPlan(id) {
    const state = charts.get(id);
    for (const planId of state?.planIds ?? []) {
        state.chart.removeOverlay({ id: planId });
    }
    if (state) {
        state.planIds = [];
    }
}

export function startAlertLine(id, price) {
    const state = charts.get(id);
    if (!state) {
        return;
    }

    clearAlertLine(id);
    state.alertLineId = lineOverlay(state, price, '#F59E0B', value => state.dotnet.invokeMethodAsync('OnAlertLineChanged', value));
}

export function clearAlertLine(id) {
    const state = charts.get(id);
    if (state?.alertLineId) {
        state.chart.removeOverlay({ id: state.alertLineId });
        state.alertLineId = null;
    }
}

export function setTheme(id, dark) {
    charts.get(id)?.chart.setStyles(dark ? 'dark' : 'light');
}

export function toggleIndicator(id, name, mainPane) {
    const state = charts.get(id);

    if (state.indicators.has(name)) {
        state.chart.removeIndicator({ paneId: state.indicators.get(name), name });
        state.indicators.delete(name);
    } else {
        const paneId = mainPane ? 'candle_pane' : `pane_${name}`;

        if (state.chart.createIndicator({ name, paneId }, mainPane)) {
            state.indicators.set(name, paneId);
            applyIndicatorConfig(state, name);
        }
    }

    return Array.from(state.indicators.keys());
}

export function startDrawing(id, name) {
    const state = charts.get(id);
    let extendData;

    if (name === 'simpleAnnotation' || name === 'simpleTag') {
        extendData = window.prompt('Texto da nota', 'Nota');

        if (extendData === null || extendData === '') {
            return;
        }
    }

    const overlayId = state.chart.createOverlay({
        name,
        extendData,
        onDrawEnd: () => notifyDrawingsChanged(state),
        onPressedMoveEnd: () => notifyDrawingsChanged(state),
        onSelected: ({ overlay }) => { state.selectedId = overlay.id; },
        onDeselected: () => { state.selectedId = null; },
        onRemoved: ({ overlay }) => {
            state.drawingIds.delete(overlay.id);
            notifyDrawingsChanged(state);
        }
    });

    if (overlayId) {
        state.drawingIds.add(overlayId);
    }
}

export function loadDrawings(id, drawings) {
    const state = charts.get(id);
    state.suspended = true;

    for (const drawingId of state.drawingIds) {
        state.chart.removeOverlay({ id: drawingId });
    }
    state.drawingIds.clear();

    for (const drawing of drawings) {
        createDrawing(state, drawing);
    }

    state.suspended = false;
}

export function removeSelected(id) {
    const state = charts.get(id);
    if (state.selectedId) {
        state.chart.removeOverlay({ id: state.selectedId });
        state.selectedId = null;
    }
}

export function clearDrawings(id) {
    const state = charts.get(id);
    state.suspended = true;

    for (const drawingId of state.drawingIds) {
        state.chart.removeOverlay({ id: drawingId });
    }
    state.drawingIds.clear();

    state.suspended = false;
    notifyDrawingsChanged(state);
}

export function resize(id) {
    charts.get(id)?.chart.resize();
}

export function dispose(id) {
    const state = charts.get(id);
    if (!state) {
        return;
    }

    document.removeEventListener('keydown', state.onKeyDown);
    state.observer?.disconnect();
    if (state.syncGroup) {
        syncGroups.get(state.syncGroup)?.delete(id);
    }
    window.klinecharts.dispose(state.container);
    charts.delete(id);
}

export function setLevels(id, levels) {
    const state = charts.get(id);

    for (const levelId of state.levelIds) {
        state.chart.removeOverlay({ id: levelId });
    }
    state.levelIds = [];

    const timestamp = state.data.length > 0 ? state.data[state.data.length - 1].timestamp : undefined;

    for (const level of levels) {
        const overlayId = state.chart.createOverlay({
            name: 'horizontalStraightLine',
            points: [{ timestamp, value: level.price }],
            lock: true,
            styles: { line: { color: level.color, style: 'dashed', dashedValue: [6, 4], size: 1 } }
        });

        if (overlayId) {
            state.levelIds.push(overlayId);
        }
    }
}

export function getVisibleRange(id) {
    const state = charts.get(id);
    const range = state.chart.getVisibleRange();
    const data = state.data;

    if (!range || data.length === 0) {
        return [];
    }

    const first = data[Math.max(0, Math.min(data.length - 1, Math.floor(range.from)))];
    const last = data[Math.max(0, Math.min(data.length - 1, Math.ceil(range.to) - 1))];
    return [first.timestamp, last.timestamp];
}

export function setMarkers(id, markers) {
    const state = charts.get(id);

    for (const markerId of state.markerIds ?? []) {
        state.chart.removeOverlay({ id: markerId });
    }
    state.markerIds = [];

    for (const marker of markers) {
        let candle = state.data[0];
        for (const bar of state.data) {
            if (bar.timestamp <= marker.timestamp) {
                candle = bar;
            } else {
                break;
            }
        }

        const overlayId = state.chart.createOverlay({
            name: 'simpleAnnotation',
            points: [{ timestamp: candle.timestamp, value: candle.high }],
            extendData: marker.label,
            lock: true,
            onClick: marker.key ? () => { state.dotnet.invokeMethodAsync('OnMarkerClicked', marker.key); return true; } : undefined,
            styles: { line: { color: marker.color }, text: { color: marker.color, borderColor: marker.color } }
        });

        if (overlayId) {
            state.markerIds.push(overlayId);
        }
    }
}

export function setCompare(id, label, candles) {
    const state = charts.get(id);
    clearCompare(id);

    const values = {};
    for (const c of candles) {
        values[typeof c.time === 'number' ? c.time : Date.parse(c.time)] = c.close;
    }

    state.chart.createIndicator({ name: 'COMPARE', shortName: label, extendData: { values }, paneId: 'candle_pane' }, true);
    state.compareActive = true;
}

export function clearCompare(id) {
    const state = charts.get(id);

    if (state?.compareActive) {
        state.chart.removeIndicator({ paneId: 'candle_pane', name: 'COMPARE' });
        state.compareActive = false;
    }
}
