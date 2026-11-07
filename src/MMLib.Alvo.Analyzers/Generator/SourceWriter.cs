using System.Text;

namespace MMLib.Alvo.Analyzers.Generator;

/// <summary>Generated source text: four-space indentation and <c>\n</c> line ends whatever the platform, so output is identical everywhere.</summary>
internal sealed class SourceWriter
{
    private const string Indentation = "    ";

    private readonly StringBuilder _text = new();

    private int _depth;

    internal SourceWriter Line(string line = "")
    {
        if (line.Length > 0)
        {
            for (var i = 0; i < _depth; i++)
            {
                _text.Append(Indentation);
            }
        }

        _text.Append(line).Append('\n');

        return this;
    }

    internal SourceWriter Open()
    {
        Line("{");
        _depth++;

        return this;
    }

    internal SourceWriter Close(string suffix = "")
    {
        _depth--;

        return Line("}" + suffix);
    }

    public override string ToString() => _text.ToString();
}
