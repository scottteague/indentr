import { EditorState } from "https://esm.sh/@codemirror/state@6.4.1";
import {
    EditorView, ViewPlugin, Decoration, WidgetType, keymap,
    drawSelection, highlightActiveLine
} from "https://esm.sh/@codemirror/view@6.36.2?deps=@codemirror/state@6.4.1";
import {
    defaultKeymap, history, historyKeymap
} from "https://esm.sh/@codemirror/commands@6.7.1?deps=@codemirror/state@6.4.1,@codemirror/view@6.36.2";
import { markdown } from "https://esm.sh/@codemirror/lang-markdown@6.3.2?deps=@codemirror/state@6.4.1,@codemirror/view@6.36.2,@codemirror/language@6.10.8";
import { syntaxHighlighting, defaultHighlightStyle, syntaxTree } from "https://esm.sh/@codemirror/language@6.10.8?deps=@codemirror/state@6.4.1,@codemirror/view@6.36.2";

// ── WYSIWYG decorations ────────────────────────────────────────────────────────

class HRWidget extends WidgetType {
    toDOM() {
        const hr = document.createElement("hr");
        hr.className = "cm-md-hr";
        return hr;
    }
    eq() { return true; }
    ignoreEvent() { return true; }
}

function classForTarget(target) {
    if (target.startsWith("note:"))   return "cm-note-link";
    if (target.startsWith("kanban:")) return "cm-kanban-link";
    return "cm-ext-link";
}

function buildWysiwygDecorations(view) {
    const { state } = view;
    const cursorLine = state.doc.lineAt(state.selection.main.head).number;
    const styleRanges = [];  // Decoration.mark and Decoration.line
    const hideRanges  = [];  // Decoration.replace (non-overlapping)

    function onCursorLine(from) {
        return state.doc.lineAt(from).number === cursorLine;
    }

    function hide(from, to) {
        if (from < to) hideRanges.push(Decoration.replace({}).range(from, to));
    }

    syntaxTree(state).iterate({
        enter(node) {
            const { from, to, name } = node;
            if (from >= to) return;

            switch (name) {

                // ── Headings ──────────────────────────────────────────────────
                case "ATXHeading1":
                case "ATXHeading2":
                case "ATXHeading3":
                case "ATXHeading4":
                case "ATXHeading5":
                case "ATXHeading6": {
                    const level = name[10]; // "1"–"6"
                    const lineFrom = state.doc.lineAt(from).from;
                    styleRanges.push(Decoration.line({ class: `cm-md-h${level}` }).range(lineFrom));
                    break;
                }
                case "HeaderMark": {
                    if (!onCursorLine(from)) {
                        // Hide "## " — include the trailing space
                        const lineEnd = state.doc.lineAt(from).to;
                        hide(from, Math.min(to + 1, lineEnd));
                    }
                    break;
                }

                // ── Bold ──────────────────────────────────────────────────────
                case "StrongEmphasis":
                    styleRanges.push(Decoration.mark({ class: "cm-md-strong" }).range(from, to));
                    break;

                // ── Italic ────────────────────────────────────────────────────
                case "Emphasis":
                    styleRanges.push(Decoration.mark({ class: "cm-md-em" }).range(from, to));
                    break;

                // ── Emphasis marks (* ** _ __) ────────────────────────────────
                case "EmphasisMark":
                    if (!onCursorLine(from)) hide(from, to);
                    break;

                // ── Inline code ───────────────────────────────────────────────
                case "InlineCode":
                    styleRanges.push(Decoration.mark({ class: "cm-md-code" }).range(from, to));
                    break;
                case "CodeMark":
                    if (!onCursorLine(from)) hide(from, to);
                    break;

                // ── Strikethrough ─────────────────────────────────────────────
                case "Strikethrough":
                    styleRanges.push(Decoration.mark({ class: "cm-md-strike" }).range(from, to));
                    break;
                case "StrikethroughMark":
                    if (!onCursorLine(from)) hide(from, to);
                    break;

                // ── Links ─────────────────────────────────────────────────────
                case "Link": {
                    const urlNode = node.node.getChild("URL");
                    if (urlNode) {
                        const url = state.doc.sliceString(urlNode.from, urlNode.to);
                        const cls = classForTarget(url);
                        styleRanges.push(
                            Decoration.mark({ class: cls, attributes: { "data-link": url } })
                                      .range(from, to)
                        );
                    }
                    break;
                }
                case "LinkMark":
                case "URL":
                case "LinkTitle":
                    if (!onCursorLine(from)) hide(from, to);
                    break;

                // ── Blockquote ────────────────────────────────────────────────
                case "Blockquote": {
                    // Apply line-level class to every line in the blockquote
                    const startLine = state.doc.lineAt(from).number;
                    const endLine   = state.doc.lineAt(to).number;
                    for (let n = startLine; n <= endLine; n++) {
                        styleRanges.push(Decoration.line({ class: "cm-md-blockquote" }).range(state.doc.line(n).from));
                    }
                    break;
                }
                case "QuoteMark":
                    if (!onCursorLine(from)) {
                        const lineEnd = state.doc.lineAt(from).to;
                        hide(from, Math.min(to + 1, lineEnd)); // hide "> "
                    }
                    break;

                // ── Horizontal rule ───────────────────────────────────────────
                case "HorizontalRule":
                    if (!onCursorLine(from)) {
                        hideRanges.push(Decoration.replace({ widget: new HRWidget() }).range(from, to));
                    } else {
                        styleRanges.push(Decoration.mark({ class: "cm-md-hr-mark" }).range(from, to));
                    }
                    break;
            }
        }
    });

    // Deduplicate overlapping replacements (can happen at boundaries)
    hideRanges.sort((a, b) => a.from - b.from);
    const dedupedHide = [];
    let lastTo = -1;
    for (const r of hideRanges) {
        if (r.from >= lastTo) {
            dedupedHide.push(r);
            lastTo = r.to;
        }
    }

    return Decoration.set([...styleRanges, ...dedupedHide], true /* sort */);
}

const wysiwygPlugin = ViewPlugin.fromClass(class {
    constructor(view) { this.decorations = buildWysiwygDecorations(view); }
    update(update) {
        if (update.docChanged || update.viewportChanged || update.selectionSet)
            this.decorations = buildWysiwygDecorations(update.view);
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
        ".cm-gutters":    { display: "none" },
        ".cm-activeLine": { background: "rgba(255,255,255,0.04)" },
        ".cm-cursor":     { borderLeftColor: "var(--fg, #d4d4d4)" },
        ".cm-selectionBackground, ::selection": { background: "rgba(100,175,255,0.2)" },
        "&.cm-focused .cm-selectionBackground": { background: "rgba(100,175,255,0.3)" },

        // Link colours
        ".cm-note-link":   { color: "#64afff", cursor: "pointer" },
        ".cm-kanban-link": { color: "#b978ff", cursor: "pointer" },
        ".cm-ext-link":    { color: "#41d2b4", cursor: "pointer" },

        // Heading sizes (applied to the .cm-line element)
        ".cm-md-h1": { fontSize: "2em",   fontWeight: "bold",   lineHeight: "1.3" },
        ".cm-md-h2": { fontSize: "1.6em", fontWeight: "bold",   lineHeight: "1.3" },
        ".cm-md-h3": { fontSize: "1.3em", fontWeight: "bold",   lineHeight: "1.3" },
        ".cm-md-h4": { fontSize: "1.1em", fontWeight: "bold" },
        ".cm-md-h5": { fontSize: "1em",   fontWeight: "bold",   fontStyle: "italic" },
        ".cm-md-h6": { fontSize: "0.9em", fontWeight: "bold",   fontStyle: "italic",  color: "var(--fg-muted, #888)" },

        // Inline styles
        ".cm-md-strong": { fontWeight: "bold" },
        ".cm-md-em":     { fontStyle: "italic" },
        ".cm-md-strike": { textDecoration: "line-through", opacity: "0.7" },
        ".cm-md-code":   { fontFamily: "monospace", background: "rgba(255,255,255,0.08)", borderRadius: "3px", padding: "0 3px" },

        // Blockquote line decoration
        ".cm-md-blockquote.cm-line": { borderLeft: "3px solid #555", paddingLeft: "12px", color: "var(--fg-muted, #888)", fontStyle: "italic" },

        // Horizontal rule
        ".cm-md-hr":     { display: "block", border: "none", borderTop: "1px solid #555", margin: "4px 0", width: "100%" },
        ".cm-md-hr-mark":{ color: "var(--fg-muted, #888)" },
    }, { dark: true });

    const saveKeymap = [{
        key: "Mod-s",
        preventDefault: true,
        run: () => { dotNetRef.invokeMethodAsync("SaveFromKeyboard"); return true; }
    }];

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
            history(),
            keymap.of([...defaultKeymap, ...historyKeymap, ...saveKeymap]),
            drawSelection(),
            highlightActiveLine(),
            syntaxHighlighting(defaultHighlightStyle),
            markdown(),
            wysiwygPlugin,
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
