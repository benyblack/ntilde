using Ntilde.Shell;

namespace Ntilde.Tests.Input;

public sealed class KittyAssociatedTextEncodingTests
{
    [Theory]
    [InlineData("\u001b[97u", "a", "\u001b[97;1;97u")]
    [InlineData("\u001b[97;2u", "A", "\u001b[97;2;65u")]
    [InlineData("\u001b[97;3u", "å", "\u001b[97;3;229u")]
    [InlineData("\u001b[97;1:2u", "a", "\u001b[97;1:2;97u")]
    [InlineData("\u001b[97:65:113;6u", "A", "\u001b[97:65:113;6;65u")]
    [InlineData("\u001b[0u", "かな", "\u001b[0;1;12363:12394u")]
    [InlineData("\u001b[0u", "e\u0301", "\u001b[0;1;101:769u")]
    [InlineData("\u001b[0u", "😀", "\u001b[0;1;128512u")]
    [InlineData("\u001b[0u", "\ufffd", "\u001b[0;1;65533u")]
    [InlineData("\u001b[0u", "👩‍💻", "\u001b[0;1;128105:8205:128187u")]
    public void Text_is_encoded_as_scalar_codepoints_with_existing_key_fields(string frame, string text, string expected)
        => Assert.Equal(expected, TerminalInputModeEncoder.AppendKittyAssociatedText(frame, text));

    [Theory]
    [InlineData("")]
    [InlineData("\r")]
    [InlineData("a\u001bb")]
    [InlineData("\u007f")]
    [InlineData("\u0085")]
    [InlineData("\u009b")]
    public void Forbidden_or_malformed_payloads_are_rejected_as_a_whole(string text)
        => Assert.Null(TerminalInputModeEncoder.AppendKittyAssociatedText("\u001b[97u", text));

    [Theory]
    [InlineData(0xd800)]
    [InlineData(0xdc00)]
    public void Malformed_utf16_is_constructed_at_runtime_so_attribute_encoding_cannot_replace_it(int codeUnit)
    {
        string text = "a" + new string((char)codeUnit, 1);
        Assert.Null(TerminalInputModeEncoder.AppendKittyAssociatedText("\u001b[97u", text));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(8, false)]
    [InlineData(16, false)]
    [InlineData(17, false)]
    [InlineData(24, true)]
    [InlineData(31, true)]
    public void Text_enhancement_requires_all_key_reporting(int flags, bool expected)
        => Assert.Equal(expected, TerminalInputModeEncoder.ReportsKittyAssociatedText(flags));
}
