using System;
using System.Globalization;
using System.IO;
using Xunit;

namespace Ntilde.Tests.Core;

/// <summary>
/// #108: the build line debug.log opens with stamps the binary's write time in the Gregorian calendar. It was formatted
/// in the current culture, so under th-TH's Buddhist calendar a binary built in 2026 was logged as built in 2569.
/// </summary>
public sealed class DescribeBuildTests
{
    [Fact]
    public void DescribeBuild_StampsTheBinaryWithAGregorianDate()
    {
        string expected = "built=" + File.GetLastWriteTime(Environment.ProcessPath!).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("th-TH");

            Assert.Contains(expected, Program.DescribeBuild(), StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
