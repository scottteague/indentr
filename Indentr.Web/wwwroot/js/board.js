const boards = new Map();

export function init(elementId) {
    const el = document.getElementById(elementId);
    if (!el || boards.has(elementId)) return;

    function handler(e) {
        if (['ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight'].includes(e.key) &&
            !(e.target instanceof HTMLInputElement || e.target instanceof HTMLTextAreaElement)) {
            e.preventDefault();
        }
    }

    el.addEventListener('keydown', handler, true);
    boards.set(elementId, handler);
}

export function scrollCardIntoView(cardId) {
    document.getElementById(cardId)?.scrollIntoView({ block: 'nearest' });
}

export function cleanup(elementId) {
    const el = document.getElementById(elementId);
    const handler = boards.get(elementId);
    if (el && handler) el.removeEventListener('keydown', handler, true);
    boards.delete(elementId);
}
