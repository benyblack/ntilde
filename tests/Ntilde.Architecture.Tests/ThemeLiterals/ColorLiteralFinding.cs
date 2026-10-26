namespace Ntilde.Architecture.Tests.ThemeLiterals;

/// <summary>One hardcoded color. <see cref="Member"/> is the enclosing C# member ("" for XAML).</summary>
internal sealed record ColorLiteralFinding(string File, int Line, string Member, string Text)
{
    public override string ToString() => $"{File}:{Line} {Text}";
}
