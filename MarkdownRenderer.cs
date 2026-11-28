using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using Brushes = System.Windows.Media.Brushes;

namespace FsClient;

/// <summary>
/// Небольшой Markdown-рендерер для ченджлогов GitHub. Поддерживает заголовки,
/// списки, цитаты, код, ссылки, жирный/курсив и горизонтальную линию.
/// Текст добавляется через Run.Text, поэтому HTML из релиза не исполняется.
/// </summary>
public static class MarkdownRenderer
{
    private static readonly Brush Text = new SolidColorBrush(Color.FromRgb(0xF4, 0xF2, 0xFA));
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0xA9, 0xA2, 0xBC));
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0xFF, 0xB8, 0x6B));
    private static readonly Brush Link = new SolidColorBrush(Color.FromRgb(0x9F, 0xC7, 0xFF));
    private static readonly Brush CodeBack = new SolidColorBrush(Color.FromRgb(0x16, 0x14, 0x1D));
    private static readonly Brush CodeBorder = new SolidColorBrush(Color.FromRgb(0x38, 0x33, 0x48));

    public static FlowDocument Render(string markdown)
    {
        var document = new FlowDocument
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13,
            Foreground = Text,
            TextAlignment = TextAlignment.Left,
            PagePadding = new Thickness(0)
        };

        string[] lines = (markdown ?? "")
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n');

        Paragraph paragraph = null;
        bool inCode = false;
        var codeLines = new List<string>();

        void Flush()
        {
            if (paragraph != null && paragraph.Inlines.Count == 0)
                document.Blocks.Remove(paragraph);
            paragraph = null;
        }

        void EnsureParagraph()
        {
            if (paragraph != null)
                return;
            paragraph = NewParagraph();
            document.Blocks.Add(paragraph);
        }

        foreach (string line in lines)
        {
            string trimmed = line.TrimEnd();

            if (inCode)
            {
                if (trimmed.TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    document.Blocks.Add(BuildCodeBlock(codeLines));
                    codeLines.Clear();
                    inCode = false;
                }
                else
                {
                    codeLines.Add(line);
                }
                continue;
            }

            if (trimmed.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                Flush();
                inCode = true;
                codeLines.Clear();
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                Flush();
                continue;
            }

            if (IsHorizontalRule(trimmed))
            {
                Flush();
                document.Blocks.Add(BuildHorizontalRule());
                continue;
            }

            int headingLevel = HeadingLevel(trimmed);
            if (headingLevel > 0)
            {
                Flush();
                document.Blocks.Add(BuildHeading(trimmed, headingLevel));
                continue;
            }

            if (trimmed.TrimStart().StartsWith(">", StringComparison.Ordinal))
            {
                Flush();
                document.Blocks.Add(BuildQuote(trimmed.TrimStart().TrimStart('>').TrimStart()));
                continue;
            }

            if (TryBuildListItem(trimmed, out Paragraph listItem))
            {
                Flush();
                document.Blocks.Add(listItem);
                continue;
            }

            EnsureParagraph();
            if (paragraph.Inlines.Count > 0)
                paragraph.Inlines.Add(new LineBreak());
            AddInline(paragraph.Inlines, trimmed.Trim());
        }

        if (inCode && codeLines.Count > 0)
            document.Blocks.Add(BuildCodeBlock(codeLines));
        Flush();

        if (document.Blocks.Count == 0)
        {
            var empty = NewParagraph();
            var run = new Run("Автор релиза не добавил описание.")
            {
                Foreground = Muted,
                FontStyle = FontStyles.Italic
            };
            empty.Inlines.Add(run);
            document.Blocks.Add(empty);
        }

        return document;
    }

    private static Paragraph NewParagraph()
    {
        return new Paragraph
        {
            Margin = new Thickness(0, 0, 0, 9),
            LineHeight = 19,
            Foreground = Text
        };
    }

    private static bool IsHorizontalRule(string line)
    {
        string value = line.Trim().Replace(" ", "");
        if (value.Length < 3)
            return false;
        char first = value[0];
        if (first != '-' && first != '*' && first != '_')
            return false;
        foreach (char c in value)
        {
            if (c != first)
                return false;
        }
        return true;
    }

    private static int HeadingLevel(string line)
    {
        int level = 0;
        foreach (char c in line)
        {
            if (c != '#')
                break;
            level++;
        }
        return level > 0 && level <= 6 && line.Length > level && line[level] == ' '
            ? level
            : 0;
    }

    private static Paragraph BuildHeading(string line, int level)
    {
        string text = line[level..].Trim().TrimEnd('#').Trim();
        var paragraph = NewParagraph();
        paragraph.Margin = new Thickness(0, level == 1 ? 0 : 6, 0, 8);
        paragraph.FontSize = level switch
        {
            1 => 19,
            2 => 16,
            3 => 14,
            _ => 13
        };
        paragraph.FontWeight = FontWeights.SemiBold;
        paragraph.Foreground = level <= 2 ? Brushes.White : Accent;
        AddInline(paragraph.Inlines, text);
        return paragraph;
    }

    private static Paragraph BuildQuote(string text)
    {
        var paragraph = NewParagraph();
        paragraph.BorderThickness = new Thickness(3, 0, 0, 0);
        paragraph.BorderBrush = Accent;
        paragraph.Padding = new Thickness(10, 0, 0, 0);
        paragraph.Foreground = Muted;
        paragraph.FontStyle = FontStyles.Italic;
        AddInline(paragraph.Inlines, text);
        return paragraph;
    }

    private static Paragraph BuildHorizontalRule()
    {
        return new Paragraph
        {
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = CodeBorder,
            Margin = new Thickness(0, 4, 0, 10)
        };
    }

    private static Paragraph BuildCodeBlock(List<string> lines)
    {
        var paragraph = NewParagraph();
        paragraph.Background = CodeBack;
        paragraph.BorderThickness = new Thickness(1);
        paragraph.BorderBrush = CodeBorder;
        paragraph.Padding = new Thickness(10);
        paragraph.FontFamily = new FontFamily("Consolas");
        paragraph.FontSize = 12;
        paragraph.Foreground = new SolidColorBrush(Color.FromRgb(0xD9, 0xD2, 0xE9));
        paragraph.Margin = new Thickness(0, 2, 0, 10);

        for (int i = 0; i < lines.Count; i++)
        {
            if (i > 0)
                paragraph.Inlines.Add(new LineBreak());
            paragraph.Inlines.Add(new Run(lines[i]));
        }
        return paragraph;
    }

    private static bool TryBuildListItem(string line, out Paragraph paragraph)
    {
        paragraph = null;
        string text = line.TrimStart();

        string marker = null;
        if (text.StartsWith("- ", StringComparison.Ordinal) ||
            text.StartsWith("* ", StringComparison.Ordinal) ||
            text.StartsWith("+ ", StringComparison.Ordinal))
            marker = text[..1];
        else
        {
            int dot = text.IndexOf(". ", StringComparison.Ordinal);
            if (dot > 0 && IsNumber(text[..dot]))
                marker = text[..(dot + 1)];
        }

        if (marker == null)
            return false;

        string body = text[marker.Length..].TrimStart();
        paragraph = NewParagraph();
        paragraph.Margin = new Thickness(18, 0, 0, 4);

        if (body.StartsWith("[ ] ", StringComparison.Ordinal) ||
            body.StartsWith("[x] ", StringComparison.OrdinalIgnoreCase))
        {
            bool done = body[1] != ' ';
            paragraph.Inlines.Add(new Run(done ? "☑ " : "☐ ")
            {
                Foreground = done ? Accent : Muted
            });
            body = body[4..];
        }
        else
        {
            paragraph.Inlines.Add(new Run(marker == "-" || marker == "*" || marker == "+" ? "• " : marker + " ")
            {
                Foreground = Accent
            });
        }

        AddInline(paragraph.Inlines, body);
        return true;
    }

    private static bool IsNumber(string value)
    {
        if (string.IsNullOrEmpty(value))
            return false;
        foreach (char c in value)
        {
            if (!char.IsDigit(c))
                return false;
        }
        return true;
    }

    private static void AddInline(InlineCollection inlines, string text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        int start = 0;
        int i = 0;
        while (i < text.Length)
        {
            if (text[i] == '`')
            {
                int close = text.IndexOf('`', i + 1);
                if (close > i)
                {
                    FlushText(inlines, text, ref start, i);
                    inlines.Add(new Run(text[(i + 1)..close])
                    {
                        FontFamily = new FontFamily("Consolas"),
                        Background = CodeBack,
                        Foreground = Accent
                    });
                    i = close + 1;
                    start = i;
                    continue;
                }
            }

            if (i + 1 < text.Length && text[i] == '*' && text[i + 1] == '*')
            {
                int close = text.IndexOf("**", i + 2, StringComparison.Ordinal);
                if (close > i + 1)
                {
                    FlushText(inlines, text, ref start, i);
                    var span = new Span { FontWeight = FontWeights.Bold };
                    AddInline(span.Inlines, text[(i + 2)..close]);
                    inlines.Add(span);
                    i = close + 2;
                    start = i;
                    continue;
                }
            }

            if (i + 1 < text.Length && text[i] == '_' && text[i + 1] == '_')
            {
                int close = text.IndexOf("__", i + 2, StringComparison.Ordinal);
                if (close > i + 1)
                {
                    FlushText(inlines, text, ref start, i);
                    var span = new Span { FontWeight = FontWeights.Bold };
                    AddInline(span.Inlines, text[(i + 2)..close]);
                    inlines.Add(span);
                    i = close + 2;
                    start = i;
                    continue;
                }
            }

            if (text[i] == '*')
            {
                int close = text.IndexOf('*', i + 1);
                if (close > i)
                {
                    FlushText(inlines, text, ref start, i);
                    var span = new Span { FontStyle = FontStyles.Italic };
                    AddInline(span.Inlines, text[(i + 1)..close]);
                    inlines.Add(span);
                    i = close + 1;
                    start = i;
                    continue;
                }
            }

            if (text[i] == '[')
            {
                int labelEnd = text.IndexOf(']', i + 1);
                int urlStart = labelEnd >= 0 && labelEnd + 1 < text.Length && text[labelEnd + 1] == '('
                    ? labelEnd + 2
                    : -1;
                int urlEnd = urlStart >= 0 ? text.IndexOf(')', urlStart) : -1;

                if (urlEnd > urlStart)
                {
                    string label = text[(i + 1)..labelEnd];
                    string url = text[urlStart..urlEnd];
                    FlushText(inlines, text, ref start, i);

                    if (TryUri(url, out Uri uri))
                    {
                        var link = new Hyperlink
                        {
                            NavigateUri = uri,
                            Foreground = Link,
                            TextDecorations = null
                        };
                        AddInline(link.Inlines, label);
                        inlines.Add(link);
                    }
                    else
                    {
                        inlines.Add(new Run(label));
                    }

                    i = urlEnd + 1;
                    start = i;
                    continue;
                }
            }

            i++;
        }

        FlushText(inlines, text, ref start, text.Length);
    }

    private static void FlushText(InlineCollection inlines, string text, ref int start, int end)
    {
        if (end <= start)
            return;
        inlines.Add(new Run(text[start..end]));
        start = end;
    }

    private static bool TryUri(string value, out Uri uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(value))
            return false;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var parsed))
            return false;
        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
            return false;
        uri = parsed;
        return true;
    }
}
