using System.Text.RegularExpressions;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

namespace Indentr.UI.Controls.Markdown;

/// <summary>
/// Applies visual Markdown styling inline while leaving the raw source text unchanged.
/// Rendering rules (Indentr-specific):
///   # … ######    → H1–H6: bold + scaled font size
///   **text**      → Bold
///   __text__      → Red (Indentr deviation from standard Markdown)
///   *text*        → Italic
///   _text_        → Underline (Indentr deviation)
///   `code`        → Inline code: tinted background
///   ```…```       → Fenced code block: tinted background on every line
///   [t](note:…)   → Blue underline (in-app link)
///   [t](http…)    → Darker blue underline (external link)
/// </summary>
public class MarkdownColorizer(FontFamily monoFamily, FontFamily proportionalFamily) : DocumentColorizingTransformer
{
    private readonly Typeface _monoTypeface         = new(monoFamily,         FontStyle.Normal, FontWeight.Regular);
    private readonly Typeface _proportionalTypeface = new(proportionalFamily, FontStyle.Normal, FontWeight.Regular);

    // ── Patterns ─────────────────────────────────────────────────────────────

    // More-specific patterns must come before their single-char siblings
    private static readonly Regex Bold      = new(@"\*\*(.+?)\*\*",                       RegexOptions.Compiled | RegexOptions.Singleline);
    // Single pattern handles both prefixed (g__text__) and unprefixed (__text__) spans.
    // Preceding-character guard is applied manually in the loop instead of via a lookbehind,
    // because the compiled lookbehind was incorrectly matching at position 1 in "g__text__".
    private static readonly Regex ColorSpan = new(@"([a-z]?)__(.+?)__",                 RegexOptions.Singleline);
    private static readonly Regex Italic    = new(@"(?<!\*)\*(?!\*)(.+?)(?<!\*)\*(?!\*)", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex Underline  = new(@"(?<!_)_(?!_)(.+?)(?<!_)_(?!_)",      RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex Link       = new(@"\[([^\]]*)\]\(([^)]*)\)",             RegexOptions.Compiled);
    private static readonly Regex Heading    = new(@"^(#{1,6}) ",                          RegexOptions.Compiled);
    private static readonly Regex InlineCode = new(@"`([^`]+)`",                           RegexOptions.Compiled);
    private static readonly Regex FenceOpen         = new(@"^```",              RegexOptions.Compiled);
    private static readonly Regex ListLine          = new(@"^\s*([-*]|\d+\.)\s", RegexOptions.Compiled);
    private static readonly Regex CheckboxUnchecked = new(@"\[ \]",            RegexOptions.Compiled);
    private static readonly Regex CheckboxChecked   = new(@"\[[xX]\]",         RegexOptions.Compiled);

    // ── Brushes ───────────────────────────────────────────────────────────────

    private static readonly IBrush NoteLinkBrush  = new SolidColorBrush(Color.FromRgb(100, 175, 255));
    private static readonly IBrush KanbanBrush    = new SolidColorBrush(Color.FromRgb(185, 120, 255));
    private static readonly IBrush ExtLinkBrush   = new SolidColorBrush(Color.FromRgb( 65, 210, 180));
    private static readonly IBrush CodeBg         = new SolidColorBrush(Color.FromArgb(60, 128, 128, 128));

    // Color-span brushes — one static field per color, same pattern as NoteLinkBrush above.
    private static readonly IBrush BrushRed     = Brushes.Red;
    private static readonly IBrush BrushGreen   = new SolidColorBrush(Color.FromRgb(  0, 148,   0));
    private static readonly IBrush BrushBlue    = new SolidColorBrush(Color.FromRgb( 60, 110, 230));
    private static readonly IBrush BrushOrange  = new SolidColorBrush(Color.FromRgb(210, 110,   0));
    private static readonly IBrush BrushGold    = new SolidColorBrush(Color.FromRgb(170, 140,   0));
    private static readonly IBrush BrushPurple  = new SolidColorBrush(Color.FromRgb(150,  40, 210));
    private static readonly IBrush BrushCyan    = new SolidColorBrush(Color.FromRgb(  0, 155, 175));
    private static readonly IBrush BrushMagenta = new SolidColorBrush(Color.FromRgb(210,  30, 140));
    private static readonly IBrush BrushTeal    = new SolidColorBrush(Color.FromRgb(  0, 128, 128));
    private static readonly IBrush BrushNavy    = new SolidColorBrush(Color.FromRgb( 30,  60, 180));
    private static readonly IBrush BrushLime    = new SolidColorBrush(Color.FromRgb( 90, 180,   0));
    private static readonly IBrush BrushAmber   = new SolidColorBrush(Color.FromRgb(195, 130,   0));
    private static readonly IBrush BrushEmerald = new SolidColorBrush(Color.FromRgb(  0, 165,  90));
    private static readonly IBrush BrushSky     = new SolidColorBrush(Color.FromRgb( 20, 145, 210));
    private static readonly IBrush BrushSlate   = new SolidColorBrush(Color.FromRgb(110, 110, 120));
    private static readonly IBrush BrushWhite   = Brushes.White;

    private static IBrush PrefixBrush(char c) => c switch
    {
        'r' => BrushRed,     'g' => BrushGreen,   'b' => BrushBlue,
        'o' => BrushOrange,  'y' => BrushGold,     'p' => BrushPurple,
        'c' => BrushCyan,    'm' => BrushMagenta,  't' => BrushTeal,
        'n' => BrushNavy,    'l' => BrushLime,     'a' => BrushAmber,
        'e' => BrushEmerald, 's' => BrushSky,      'k' => BrushSlate,
        'w' => BrushWhite,   _   => BrushRed,
    };

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

        // Fenced code blocks: tinted background + monospace font; no markdown formatting inside.
        if (GetFencedLines().Contains(line.LineNumber))
        {
            ChangeLinePart(line.Offset, line.Offset + line.Length, el =>
            {
                el.TextRunProperties.SetBackgroundBrush(CodeBg);
                el.TextRunProperties.SetTypeface(_monoTypeface);
            });
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

        foreach (Match m in ColorSpan.Matches(text))
        {
            // Manual lookbehind: skip if the char immediately before the match is a lowercase
            // letter, which means the prefix letter is part of a word rather than a color tag.
            if (m.Index > 0 && char.IsAsciiLetterLower(text[m.Index - 1])) continue;

            string prefix = m.Groups[1].Value;
            IBrush b = prefix.Length > 0 ? PrefixBrush(prefix[0]) : BrushRed;
            int docStart = line.Offset + m.Index;
            int docEnd   = line.Offset + m.Index + m.Length + 1;
            if (prefix.Length > 0)
            {
                // Split the call so AvaloniaEdit sees a clean element boundary after the
                // prefix letter. Without this, the second __ is excluded from the styled run.
                ChangeLinePart(docStart,     docStart + 1, el => el.TextRunProperties.SetForegroundBrush(b));
                ChangeLinePart(docStart + 1, docEnd,       el => el.TextRunProperties.SetForegroundBrush(b));
            }
            else
            {
                ChangeLinePart(docStart, docEnd, el => el.TextRunProperties.SetForegroundBrush(b));
            }
        }

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
                el.TextRunProperties.SetForegroundBrush(BrushRed));
            Apply(line, text, CheckboxChecked, el =>
                el.TextRunProperties.SetForegroundBrush(BrushGreen));
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void ApplyLinks(DocumentLine line, string text)
    {
        foreach (Match m in Link.Matches(text))
        {
            var target = m.Groups[2].Value;
            IBrush brush;
            if (target.StartsWith("note:", StringComparison.OrdinalIgnoreCase))
                brush = NoteLinkBrush;
            else if (target.StartsWith("kanban:", StringComparison.OrdinalIgnoreCase))
                brush = KanbanBrush;
            else
                brush = ExtLinkBrush;

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
