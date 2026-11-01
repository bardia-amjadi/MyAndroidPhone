using System.Text.RegularExpressions;

namespace MyAndroidPhone.Android;

public enum TokenKind
{
    Command,
    Subcommand,
    Flag,
    Path,
    String,
    Number,
    Plain,
    Space
}

public sealed record CommandToken(string Text, string Value, TokenKind Kind, int Start);

/// <summary>Splits a typed line into words (quotes group words; there is no escape processing) and classifies them for coloring.</summary>
public static partial class CommandLine
{
    [GeneratedRegex(@"^-?\d+([.,]\d+)?$")]
    private static partial Regex NumberRegex();

    /// <summary>Returns the words with quotes removed. An unterminated quote runs to the end of the line.</summary>
    public static IReadOnlyList<string> Words(string line) => Tokenize(line, null).Where(t => t.Kind != TokenKind.Space).Select(t => t.Value).ToList();

    /// <summary>Tokens including the spaces between them, so that the concatenation of <see cref="CommandToken.Text"/> equals the line.</summary>
    public static IReadOnlyList<CommandToken> Tokenize(string line, Func<string, bool>? isKnownCommand)
    {
        var tokens = new List<CommandToken>();
        var i = 0;
        var wordIndex = 0;
        while (i < line.Length)
        {
            if (char.IsWhiteSpace(line[i]))
            {
                var start = i;
                while (i < line.Length && char.IsWhiteSpace(line[i]))
                {
                    i++;
                }

                tokens.Add(new CommandToken(line[start..i], line[start..i], TokenKind.Space, start));
                continue;
            }

            var wordStart = i;
            var value = new System.Text.StringBuilder();
            var quoted = false;
            while (i < line.Length && !char.IsWhiteSpace(line[i]))
            {
                if (line[i] is '"' or '\'')
                {
                    var quote = line[i++];
                    quoted = true;
                    while (i < line.Length && line[i] != quote)
                    {
                        value.Append(line[i++]);
                    }

                    if (i < line.Length)
                    {
                        i++;
                    }
                }
                else
                {
                    value.Append(line[i++]);
                }
            }

            var text = line[wordStart..i];
            var word = value.ToString();
            TokenKind kind;
            if (wordIndex == 0)
            {
                kind = isKnownCommand is null || isKnownCommand(word) ? TokenKind.Command : TokenKind.Plain;
            }
            else if (quoted)
            {
                kind = TokenKind.String;
            }
            else if (word.StartsWith('-') && word.Length > 1 && !NumberRegex().IsMatch(word))
            {
                kind = TokenKind.Flag;
            }
            else if (word.Contains('/') || word.StartsWith('~'))
            {
                kind = TokenKind.Path;
            }
            else if (NumberRegex().IsMatch(word))
            {
                kind = TokenKind.Number;
            }
            else if (wordIndex == 1 && tokens.FirstOrDefault(t => t.Kind == TokenKind.Command)?.Value is "adb" or "show")
            {
                kind = TokenKind.Subcommand;
            }
            else
            {
                kind = TokenKind.Plain;
            }

            tokens.Add(new CommandToken(text, word, kind, wordStart));
            wordIndex++;
        }

        return tokens;
    }

    /// <summary>Everything after the first <paramref name="skipWords"/> words, exactly as typed (used by "shell", where the phone's shell does the parsing).</summary>
    public static string RestAfter(string line, int skipWords)
    {
        var tokens = Tokenize(line, null);
        var seen = 0;
        foreach (var token in tokens)
        {
            if (token.Kind == TokenKind.Space)
            {
                continue;
            }

            seen++;
            if (seen == skipWords)
            {
                return line[(token.Start + token.Text.Length)..].Trim();
            }
        }

        return string.Empty;
    }
}

public enum OutputKind
{
    Plain,
    Path,
    Number,
    Address,
    Error,
    Success,
    Warning,
    Dim
}

public sealed record OutputSegment(string Text, OutputKind Kind);

/// <summary>Colors things people look for in command output: paths, numbers with units, addresses and failure/success words.</summary>
public static partial class OutputHighlighter
{
    [GeneratedRegex(
        @"(?<addr>\b\d{1,3}(?:\.\d{1,3}){3}(?::\d{2,5})?\b|\b[0-9A-Fa-f]{2}(?::[0-9A-Fa-f]{2}){5}\b)"
        + @"|(?<path>(?<![\w/])/[\w.@+\-]+(?:/[\w.@+\- ]*[\w.@+\-])*/?|\b[A-Za-z]:\\[^\s]+)"
        + @"|(?<err>\b(?:error|errors|failed|failure|denied|fatal|exception|not found|unauthorized|offline|refused)\b)"
        + @"|(?<ok>\b(?:success|successful|successfully|ok|done|ready|connected|granted)\b)"
        + @"|(?<warn>\b(?:warning|warn|deprecated)\b)"
        + @"|(?<num>(?<![\w.])-?\d+(?:[.,]\d+)?(?:\s?(?:%|[KMGT]?i?B|kB|ms|s|Mbps|dBm|MHz|°C))?(?![\w.]))",
        RegexOptions.IgnoreCase)]
    private static partial Regex Pattern();

    public static IReadOnlyList<OutputSegment> Highlight(string line)
    {
        var segments = new List<OutputSegment>();
        var last = 0;
        foreach (Match m in Pattern().Matches(line))
        {
            if (m.Index > last)
            {
                segments.Add(new OutputSegment(line[last..m.Index], OutputKind.Plain));
            }

            var kind = m.Groups["addr"].Success ? OutputKind.Address
                : m.Groups["path"].Success ? OutputKind.Path
                : m.Groups["err"].Success ? OutputKind.Error
                : m.Groups["ok"].Success ? OutputKind.Success
                : m.Groups["warn"].Success ? OutputKind.Warning
                : OutputKind.Number;
            segments.Add(new OutputSegment(m.Value, kind));
            last = m.Index + m.Length;
        }

        if (last < line.Length)
        {
            segments.Add(new OutputSegment(line[last..], OutputKind.Plain));
        }

        return segments;
    }
}
