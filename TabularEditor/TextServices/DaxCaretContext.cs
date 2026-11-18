using System;
using System.Collections.Generic;
using System.Linq;

namespace TabularEditor.TextServices
{
    internal sealed class DaxCaretContext
    {
        private static readonly char[] LineBreaks = { '\r', '\n', '\u0085', '\u2028', '\u2029' };

        public int Start { get; private set; }
        public int End { get; private set; }
        public string Prefix { get; private set; } = "";
        public char ReferenceDelimiter { get; private set; }
        public string TableName { get; private set; }
        public bool InCommentOrString { get; private set; }
        public (char Delimiter, int Position)? OpenGroup { get; private set; }
        public IEnumerable<(string Name, int Argument)> Calls { get; private set; }

        public static DaxCaretContext Analyze(string text, int caret, bool useSemicolons)
        {
            var context = new DaxCaretContext { Start = caret, End = caret };
            var groups = new Stack<(char Delimiter, int Position, string Function, int Argument)>();
            var separator = useSemicolons ? ';' : ',';
            string identifier = null;
            string table = null;
            for (var i = 0; i < caret;)
            {
                var start = i;
                var c = text[i++];
                if (char.IsWhiteSpace(c)) continue;

                if (IsIdentifier(c))
                {
                    while (i < caret && IsIdentifier(text[i])) i++;
                    identifier = text.Substring(start, i - start);
                    table = identifier;
                    if (i == caret)
                    {
                        context.Start = start;
                        context.End = i;
                        while (context.End < text.Length && IsIdentifier(text[context.End])) context.End++;
                        context.Prefix = identifier;
                    }
                    continue;
                }

                var next = i < text.Length ? text[i] : '\0';
                if ((c == '/' && (next == '*' || next == '/')) || (c == '-' && next == '-'))
                {
                    var end = next == '*' ? ReadBlockComment(text, i + 1) : text.IndexOfAny(LineBreaks, i + 1);
                    if (end < 0 || caret < end || (next != '*' && caret == end))
                    {
                        context.InCommentOrString = true;
                        break;
                    }
                    i = end;
                    continue;
                }

                if (c == '\'' || c == '[' || c == '"')
                {
                    var closing = c == '[' ? ']' : c;
                    var end = ReadDelimited(text, i, closing);
                    if (end < 0 || caret < end)
                    {
                        if (c == '"') context.InCommentOrString = true;
                        else
                        {
                            context.ReferenceDelimiter = c;
                            context.TableName = c == '[' ? table : null;
                            context.Start = start;
                            context.End = end < 0 ? caret : end;
                            context.Prefix = Decode(text.Substring(i, caret - i), closing);
                        }
                        break;
                    }
                    table = c == '\'' ? Decode(text.Substring(i, end - i - 1), closing) : null;
                    identifier = null;
                    i = end;
                    continue;
                }

                if (c == '(' || c == '{') groups.Push((c, start, c == '(' ? identifier : null, 0));
                else if (groups.Count > 0)
                {
                    if ((c == ')' && groups.Peek().Delimiter == '(') || (c == '}' && groups.Peek().Delimiter == '{'))
                        groups.Pop();
                    else if (c == separator && groups.Peek().Delimiter == '(')
                    {
                        var group = groups.Pop();
                        groups.Push((group.Delimiter, group.Position, group.Function, group.Argument + 1));
                    }
                }
                identifier = null;
                table = null;
            }

            if (groups.Count > 0)
            {
                var group = groups.Peek();
                context.OpenGroup = (group.Delimiter, group.Position);
            }
            context.Calls = groups.Where(group => group.Function != null).Select(group => (group.Function, group.Argument));
            return context;
        }

        private static bool IsIdentifier(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '.';

        private static string Decode(string text, char closing) =>
            text.Replace(new string(closing, 2), closing.ToString());

        private static int ReadDelimited(string text, int position, char closing)
        {
            for (var i = position; i < text.Length; i++)
            {
                if (closing != '"' && Array.IndexOf(LineBreaks, text[i]) >= 0) break;
                if (text[i] != closing) continue;
                if (i + 1 == text.Length || text[i + 1] != closing) return i + 1;
                i++;
            }
            return -1;
        }

        private static int ReadBlockComment(string text, int position)
        {
            var depth = 1;
            for (var i = position; i + 1 < text.Length; i++)
            {
                if (text[i] == '/' && text[i + 1] == '*')
                {
                    depth++;
                    i++;
                }
                else if (text[i] == '*' && text[i + 1] == '/')
                {
                    if (--depth == 0) return i + 2;
                    i++;
                }
            }
            return -1;
        }
    }
}
