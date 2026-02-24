import { EditorState, Compartment } from "@codemirror/state";
import {
    EditorView, ViewPlugin, Decoration, DecorationSet, WidgetType,
    drawSelection, highlightActiveLine, keymap, lineNumbers
} from "@codemirror/view";
import { markdown } from "@codemirror/lang-markdown";
import { syntaxHighlighting, defaultHighlightStyle } from "@codemirror/language";

// ── Link decoration ────────────────────────────────────────────────────────────

const LINK_RE = /\[([^\]]+)\]\((note:|kanban:|https?:\/\/)[^\)]+\)/g;

function classForTarget(target) {
    if (target.startsWith("note:"))   return "cm-note-link";
    if (target.startsWith("kanban:")) return "cm-kanban-link";
    return "cm-ext-link";
}

function buildLinkDecorations(view) {
    const decorations = [];
    for (const { from, to } of view.visibleRanges) {
        const text = view.state.doc.sliceString(from, to);
        let m;
        LINK_RE.lastIndex = 0;
        while ((m = LINK_RE.exec(text)) !== null) {
            const start = from + m.index;
            const end   = start + m[0].length;
            // Extract target (the part after the opening paren)
            const parenContent = m[0].slice(m[1].length + 3, -1); // strip [text]( and )
            const target = parenContent;
            const cls    = classForTarget(target);
            decorations.push(Decoration.mark({ class: cls, attributes: { "data-link": target } }).range(start, end));
        }
    }
    return Decoration.set(decorations, true);
}

const linkPlugin = ViewPlugin.fromClass(class {
    constructor(view) { this.decorations = buildLinkDecorations(view); }
    update(update) {
        if (update.docChanged || update.viewportChanged)
            this.decorations = buildLinkDecorations(update.view);
    }
}, { decorations: v => v.decorations });

// ── Per-editor registry ────────────────────────────────────────────────────────

const editors = new Map(); // editorId → EditorView

// ── Public API ─────────────────────────────────────────────────────────────────

export function create(elementId, initialContent, dotNetRef) {
    const el = document.getElementById(elementId);
    if (!el || editors.has(elementId)) return;

    const theme = EditorView.theme({
        "&": {
            height: "100%",
            background: "var(--bg, #1e1e1e)",
            color: "var(--fg, #d4d4d4)",
            fontSize: "14px",
            fontFamily: "'JetBrains Mono', 'Fira Code', 'Cascadia Code', monospace"
        },
        ".cm-content":    { padding: "12px 20px", caretColor: "var(--fg, #d4d4d4)" },
        ".cm-gutters":    { background: "var(--bg, #1e1e1e)", borderRight: "1px solid var(--border, #3a3a3a)", color: "var(--fg-muted, #888)" },
        ".cm-activeLine": { background: "rgba(255,255,255,0.04)" },
        ".cm-cursor":     { borderLeftColor: "var(--fg, #d4d4d4)" },
        ".cm-selectionBackground, ::selection": { background: "rgba(100,175,255,0.2)" },
        "&.cm-focused .cm-selectionBackground": { background: "rgba(100,175,255,0.3)" },
        ".cm-note-link":   { color: "#64afff", cursor: "pointer" },
        ".cm-kanban-link": { color: "#b978ff", cursor: "pointer" },
        ".cm-ext-link":    { color: "#41d2b4", cursor: "pointer" },
    }, { dark: true });

    const changeListener = EditorView.updateListener.of(update => {
        if (update.docChanged)
            dotNetRef.invokeMethodAsync("OnContentChanged", update.state.doc.toString());
    });

    const clickHandler = EditorView.domEventHandlers({
        click(event, view) {
            const target = event.target;
            if (!(target instanceof HTMLElement)) return false;
            const link = target.closest("[data-link]");
            if (!link) return false;
            const href = link.getAttribute("data-link");
            if (href) {
                event.preventDefault();
                dotNetRef.invokeMethodAsync("OnLinkClicked", href);
            }
            return true;
        }
    });

    const state = EditorState.create({
        doc: initialContent,
        extensions: [
            lineNumbers(),
            drawSelection(),
            highlightActiveLine(),
            syntaxHighlighting(defaultHighlightStyle),
            markdown(),
            linkPlugin,
            changeListener,
            clickHandler,
            theme,
            EditorView.lineWrapping,
        ]
    });

    const view = new EditorView({ state, parent: el });
    editors.set(elementId, view);
}

export function getContent(elementId) {
    return editors.get(elementId)?.state.doc.toString() ?? "";
}

export function setContent(elementId, content) {
    const view = editors.get(elementId);
    if (!view) return;
    view.dispatch({
        changes: { from: 0, to: view.state.doc.length, insert: content }
    });
}

export function destroy(elementId) {
    const view = editors.get(elementId);
    if (view) {
        view.destroy();
        editors.delete(elementId);
    }
}
