using System.Runtime.CompilerServices;
using System.Text;

namespace DataRepo.Site;

/// <summary>Interpolates a multi-line template with <c>\n</c> line ends in its literal text, whatever the
/// source file's line ends are, and leaves every interpolated value exactly as it is.</summary>
/// <remarks>The pages are published from any OS and must be byte-identical to Python's, which writes
/// <c>\n</c>. A git checkout with <c>core.autocrlf</c> turns a raw string's line ends into <c>\r\n</c>, and
/// normalising the finished string instead would also rewrite a <c>\r</c> inside a producer's message.</remarks>
internal static class Lf
{
    public static string Text(Handler handler) => handler.ToString();

    [InterpolatedStringHandler]
    public ref struct Handler
    {
        private readonly StringBuilder _sb;

        public Handler(int literalLength, int formattedCount) => _sb = new StringBuilder(literalLength + formattedCount * 16);

        public void AppendLiteral(string text) => _sb.Append(text.ReplaceLineEndings("\n"));

        public void AppendFormatted(string? value) => _sb.Append(value);

        public void AppendFormatted<T>(T value) => _sb.Append(value is null ? "" : PyText.Str(value));

        public override string ToString() => _sb.ToString();
    }
}
