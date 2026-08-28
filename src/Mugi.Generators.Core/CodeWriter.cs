using System.Text;

namespace Mugi.Generators.Core;

internal sealed class CodeWriter
{
    private readonly StringBuilder _builder = new();
    private int _indent;

    internal void Line(string text = "")
    {
        if (text.Length != 0)
        {
            _builder.Append(' ', _indent * 4);
            _builder.Append(text);
        }

        _builder.AppendLine();
    }

    internal void Open(string text)
    {
        Line(text);
        Line("{");
        _indent++;
    }

    internal void Close(string suffix = "")
    {
        _indent--;
        Line("}" + suffix);
    }

    public override string ToString() => _builder.ToString();
}
