using System.Text.RegularExpressions;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

namespace Organiz.UI.Controls.Markdown;

/// <summary>
/// Applies visual Markdown styling inline while leaving the raw source text unchanged.
/// Rendering rules (Organiz-specific):
///   # … ######    → H1–H6: bold + scaled font size
///   **text**      → Bold
///   __text__      → Red (Organiz deviation from standard Markdown)
///   *text*        → Italic
///   _text_        → Underline (Organiz deviation)
///   `code`        → Inline code: tinted background
///   ```…```       → Fenced code block: tinted background on every line
///   [t](note:…)   → Blue underline (in-app link)
///   [t](http…)    → Darker blue underline (external link)
/// </summary>
public class MarkdownColorizer(FontFamily monoFamily) : DocumentColorizingTransformer
{
    private readonly Typeface _monoTypeface = new(monoFamily, FontStyle.Normal, FontWeight.Regular);

    // ── Patterns ─────────────────────────────────────────────────────────────

    // More-specific patterns must come before their single-char siblings
    private static readonly Regex Bold       = new(@"\*\*(.+?)\*\*",                       RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex Red        = new(@"__(.+?)__",                           RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex Italic     = new(@"(?<!\*)\*(?!\*)(.+?)(?<!\*)\*(?!\*)", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex Underline  = new(@"(?<!_)_(?!_)(.+?)(?<!_)_(?!_)",      RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex Link       = new(@"\[([^\]]*)\]\(([^)]*)\)",             RegexOptions.Compiled);
    private static readonly Regex Heading    = new(@"^(#{1,6}) ",                          RegexOptions.Compiled);
    private static readonly Regex InlineCode = new(@"`([^`]+)`",                           RegexOptions.Compiled);
    private static readonly Regex FenceOpen         = new(@"^```",              RegexOptions.Compiled);
    private static readonly Regex ListLine          = new(@"^\s*([-*]|\d+\.)\s", RegexOptions.Compiled);
    private static readonly Regex CheckboxUnchecked = new(@"\[ \]",            RegexOptions.Compiled);
    private static readonly Regex CheckboxChecked   = new(@"\[[xX]\]",         RegexOptions.Compiled);

    // ── Brushes ───────────────────────────────────────────────────────────────

    private static readonly IBrush RedBrush      = Brushes.Red;
    private static readonly IBrush NoteLinkBrush = new SolidColorBrush(Color.FromRgb(0,  102, 204));
    private static readonly IBrush ExtLinkBrush  = new SolidColorBrush(Color.FromRgb(30,  30, 200));
    private static readonly IBrush CodeBg     = new SolidColorBrush(Color.FromArgb(60, 128, 128, 128));
    private static readonly IBrush GreenBrush = new SolidColorBrush(Color.FromRgb(0, 160, 0));

    // Font-size multipliers for H1 … H6 relative to the editor's base size
    private static readonly double[] HeadingScales = { 2.0, 1.6, 1.35, 1.15, 1.05, 1.0 };

    // ── Fenced-block cache (for skipping markup inside code blocks) ──────────

    private ITextSourceVersion? _lastVersion;
    private HashSet<int>        _fencedLines = new();

    private HashSet<int> GetFencedLines()
    {
        var doc = CurrentContext.Document;
        var ver = doc.Version;
        bool stale = ver is null
                  || _lastVersion is null
                  || !_lastVersion.BelongsToSameDocumentAs(ver)
                  || _lastVersion.CompareAge(ver) != 0;
        if (stale)
        {
            _fencedLines = ComputeFencedLines(doc);
            _lastVersion = ver;
        }
        return _fencedLines;
    }

    private static HashSet<int> ComputeFencedLines(IDocument document)
    {
        var fenced   = new HashSet<int>();
        bool inFence = false;
        for (int i = 1; i <= document.LineCount; i++)
        {
            var line = document.GetLineByNumber(i);
            var text = document.GetText(line.Offset, line.Length);
            if (FenceOpen.IsMatch(text)) { fenced.Add(i); inFence = !inFence; }
            else if (inFence)            { fenced.Add(i); }
        }
        return fenced;
    }

    // ── Per-line colorizing ───────────────────────────────────────────────────

    protected override void ColorizeLine(DocumentLine line)
    {
        if (line.Length == 0) return;

        var text     = CurrentContext.Document.GetText(line.Offset, line.Length);
        var baseSize = CurrentContext.GlobalTextRunProperties.FontRenderingEmSize;

        // Fenced code blocks: tinted background; no markdown formatting inside.
        if (GetFencedLines().Contains(line.LineNumber))
        {
            ChangeLinePart(line.Offset, line.Offset + line.Length, el =>
                el.TextRunProperties.SetBackgroundBrush(CodeBg));
            return;
        }

        // ── Headings ──────────────────────────────────────────────────────────
        var hm = Heading.Match(text);
        if (hm.Success)
        {
            int    level = hm.Groups[1].Length;
            double scale = HeadingScales[level - 1];

            ChangeLinePart(line.Offset, line.Offset + line.Length, el =>
            {
                var tf = el.TextRunProperties.Typeface;
                el.TextRunProperties.SetTypeface(new Typeface(tf.FontFamily, tf.Style, FontWeight.Bold));
                el.TextRunProperties.SetFontRenderingEmSize(baseSize * scale);
            });

            ApplyLinks(line, text); // links inside headings still get coloured
            return;
        }

        // ── Inline formatting ─────────────────────────────────────────────────
        Apply(line, text, Bold, el =>
        {
            var tf = el.TextRunProperties.Typeface;
            el.TextRunProperties.SetTypeface(new Typeface(tf.FontFamily, tf.Style, FontWeight.Bold));
        });

        Apply(line, text, Red, el =>
            el.TextRunProperties.SetForegroundBrush(RedBrush));

        Apply(line, text, Italic, el =>
        {
            var tf = el.TextRunProperties.Typeface;
            el.TextRunProperties.SetTypeface(new Typeface(tf.FontFamily, FontStyle.Italic, tf.Weight));
        });

        Apply(line, text, Underline, el =>
            el.TextRunProperties.SetTextDecorations(TextDecorations.Underline));

        ApplyLinks(line, text);

        // ── Inline code ───────────────────────────────────────────────────────
        Apply(line, text, InlineCode, el =>
        {
            el.TextRunProperties.SetBackgroundBrush(CodeBg);
            el.TextRunProperties.SetTypeface(_monoTypeface);
        });

        // ── Checkboxes (only on list item lines) ──────────────────────────────
        if (ListLine.IsMatch(text))
        {
            Apply(line, text, CheckboxUnchecked, el =>
                el.TextRunProperties.SetForegroundBrush(RedBrush));
            Apply(line, text, CheckboxChecked, el =>
                el.TextRunProperties.SetForegroundBrush(GreenBrush));
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void ApplyLinks(DocumentLine line, string text)
    {
        foreach (Match m in Link.Matches(text))
        {
            var isNote = m.Groups[2].Value.StartsWith("note:", StringComparison.OrdinalIgnoreCase);
            var brush  = isNote ? NoteLinkBrush : ExtLinkBrush;
            ChangeLinePart(line.Offset + m.Index, line.Offset + m.Index + m.Length, el =>
            {
                el.TextRunProperties.SetForegroundBrush(brush);
                el.TextRunProperties.SetTextDecorations(TextDecorations.Underline);
            });
        }
    }

    private void Apply(DocumentLine line, string text, Regex pattern, Action<VisualLineElement> style)
    {
        foreach (Match m in pattern.Matches(text))
            ChangeLinePart(line.Offset + m.Index, line.Offset + m.Index + m.Length, style);
    }
}
