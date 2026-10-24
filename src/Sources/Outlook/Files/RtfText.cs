using System.Text;

namespace AnyFS.Sources.Outlook.Files;

/// <summary>
/// What an RTF body holds: the HTML Outlook wrapped in it (<c>\fromhtml1</c>, [MS-OXRTFEX]: the markup in
/// <c>\*\htmltag</c> groups, the text between them, and RTF of Outlook's own under <c>\htmlrtf</c> to be left out), or
/// plain text (<c>\fromtext</c>, or any RTF with its control words and tables stripped).
/// </summary>
internal static class RtfText
{
    private static readonly HashSet<string> SkippedDestinations = new(StringComparer.Ordinal)
    {
        "fonttbl", "colortbl", "stylesheet", "info", "generator", "pict", "object", "fldinst", "header", "footer", "footnote",
        "listtable", "listoverridetable", "rsidtbl", "xmlnstbl", "themedata", "colorschememapping", "latentstyles", "datastore",
        "mhtmltag", "pntext", "pntxta", "pntxtb", "revtbl", "protusertbl", "userprops", "docvar", "bkmkstart", "bkmkend",
    };

    static RtfText()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>Whether the RTF wraps HTML.</summary>
    public static bool WrapsHtml(string rtf) => rtf.Contains("\\fromhtml1", StringComparison.Ordinal);

    /// <summary>The HTML an RTF body wraps, or null when it wraps none.</summary>
    public static string? HtmlFromRtf(string rtf) => WrapsHtml(rtf) ? Extract(rtf, html: true) : null;

    /// <summary>The RTF as plain text: the text it shows, control words and tables stripped, paragraphs as line breaks.</summary>
    public static string PlainText(string rtf) => Extract(rtf, html: false);

    private static string Extract(string rtf, bool html)
    {
        var output = new StringBuilder(rtf.Length);
        var stack = new Stack<State>();
        var state = new State();
        var encoding = Encoding.Latin1;
        var bytes = new List<byte>();   // \'xx runs, decoded together so multi-byte code pages come out right
        var i = 0;

        void FlushBytes()
        {
            if (bytes.Count > 0)
            {
                output.Append(encoding.GetString(bytes.ToArray()));
                bytes.Clear();
            }
        }

        bool Emitting() => !state.Skip && (!html || state.HtmlTag || !state.HtmlRtf);

        void Text(string s)
        {
            if (state.SkipChars > 0)
            {
                var n = Math.Min(state.SkipChars, s.Length);
                state = state with { SkipChars = state.SkipChars - n };
                s = s[n..];
            }

            if (s.Length > 0 && Emitting())
            {
                FlushBytes();
                output.Append(s);
            }
        }

        while (i < rtf.Length)
        {
            var c = rtf[i];
            switch (c)
            {
                case '{':
                    FlushBytes();
                    stack.Push(state);
                    state = state with { SkipChars = 0 };
                    i++;
                    break;
                case '}':
                    FlushBytes();
                    state = stack.Count > 0 ? stack.Pop() : new State();
                    i++;
                    break;
                case '\\':
                    i++;
                    if (i >= rtf.Length)
                    {
                        break;
                    }

                    var next = rtf[i];
                    if (next == '\'')
                    {
                        // \'xx: a byte in the document's code page
                        if (i + 2 < rtf.Length && Uri.IsHexDigit(rtf[i + 1]) && Uri.IsHexDigit(rtf[i + 2]))
                        {
                            var b = Convert.ToByte(rtf.Substring(i + 1, 2), 16);
                            if (state.SkipChars > 0)
                            {
                                state = state with { SkipChars = state.SkipChars - 1 };
                            }
                            else if (Emitting())
                            {
                                bytes.Add(b);
                            }

                            i += 3;
                        }
                        else
                        {
                            i++;
                        }

                        break;
                    }

                    if (next is '{' or '}' or '\\')
                    {
                        Text(next.ToString());
                        i++;
                        break;
                    }

                    if (next == '*')
                    {
                        // \*\dest: a destination older readers skip; htmltag is the one kept
                        i++;
                        if (i < rtf.Length && rtf[i] == '\\')
                        {
                            var (word, _, after) = ControlWord(rtf, i + 1);
                            i = after;
                            if (word == "htmltag" && html)
                            {
                                state = state with { HtmlTag = true };
                            }
                            else
                            {
                                state = state with { Skip = true };
                            }
                        }

                        break;
                    }

                    if (next == '~')
                    {
                        Text(" ");
                        i++;
                        break;
                    }

                    if (next == '-')
                    {
                        i++;
                        break;
                    }

                    if (next == '_')
                    {
                        Text("-");
                        i++;
                        break;
                    }

                    if (next is '\r' or '\n')
                    {
                        // \<newline> is \par
                        Text(html && !state.HtmlTag ? "" : "\r\n");
                        i++;
                        break;
                    }

                    if (!char.IsLetter(next))
                    {
                        i++;
                        break;
                    }

                    {
                        var (word, parameter, after) = ControlWord(rtf, i);
                        i = after;
                        switch (word)
                        {
                            case "ansicpg":
                                encoding = EncodingFor(parameter ?? 1252);
                                break;
                            case "uc":
                                state = state with { UnicodeSkip = parameter ?? 1 };
                                break;
                            case "u":
                                if (parameter is { } code)
                                {
                                    var ch = code < 0 ? code + 65536 : code;
                                    Text(char.ConvertFromUtf32(ch is >= 0xD800 and <= 0xDFFF or > 0x10FFFF ? 0xFFFD : ch));
                                    state = state with { SkipChars = state.UnicodeSkip };
                                }

                                break;
                            case "par":
                            case "line":
                            case "sect":
                            case "page":
                                Text("\r\n");
                                break;
                            case "tab":
                            case "cell":
                                Text("\t");
                                break;
                            case "row":
                                Text("\r\n");
                                break;
                            case "emdash":
                                Text("—");
                                break;
                            case "endash":
                                Text("–");
                                break;
                            case "bullet":
                                Text("•");
                                break;
                            case "lquote":
                                Text("‘");
                                break;
                            case "rquote":
                                Text("’");
                                break;
                            case "ldblquote":
                                Text("“");
                                break;
                            case "rdblquote":
                                Text("”");
                                break;
                            case "htmlrtf":
                                state = state with { HtmlRtf = parameter != 0 };
                                break;
                            case "htmltag":
                                if (html)
                                {
                                    state = state with { HtmlTag = true };
                                }

                                break;
                            default:
                                if (SkippedDestinations.Contains(word))
                                {
                                    state = state with { Skip = true };
                                }

                                break;
                        }
                    }

                    break;
                case '\r':
                case '\n':
                case '\0':
                    // line breaks in the RTF source mean nothing; a NUL ends what Outlook stored
                    i++;
                    break;
                default:
                    {
                        var start = i;
                        while (i < rtf.Length && rtf[i] is not ('{' or '}' or '\\' or '\r' or '\n' or '\0'))
                        {
                            i++;
                        }

                        Text(rtf[start..i]);
                        break;
                    }
            }
        }

        FlushBytes();
        var text = output.ToString();
        return html ? text : text.Trim('\r', '\n', ' ', '\t');
    }

    /// <summary>The control word at <paramref name="at"/> (just after the backslash): its name, its number, and where the next token starts.</summary>
    private static (string Word, int? Parameter, int After) ControlWord(string rtf, int at)
    {
        var i = at;
        while (i < rtf.Length && char.IsAsciiLetter(rtf[i]))
        {
            i++;
        }

        var word = rtf[at..i];
        int? parameter = null;
        var start = i;
        if (i < rtf.Length && (rtf[i] == '-' || char.IsAsciiDigit(rtf[i])))
        {
            i++;
            while (i < rtf.Length && char.IsAsciiDigit(rtf[i]))
            {
                i++;
            }

            if (int.TryParse(rtf.AsSpan(start, i - start), System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var n))
            {
                parameter = n;
            }
        }

        // one space after a control word is part of it
        if (i < rtf.Length && rtf[i] == ' ')
        {
            i++;
        }

        return (word, parameter, i);
    }

    private static Encoding EncodingFor(int codePage)
    {
        try
        {
            return codePage == 65001 ? Encoding.UTF8 : Encoding.GetEncoding(codePage);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return Encoding.Latin1;
        }
    }

    /// <summary>What the reader is inside: a group that is skipped, an htmltag group, Outlook's own RTF (htmlrtf), and the \uc count.</summary>
    private readonly record struct State(bool Skip = false, bool HtmlTag = false, bool HtmlRtf = false, int UnicodeSkip = 1, int SkipChars = 0);
}
