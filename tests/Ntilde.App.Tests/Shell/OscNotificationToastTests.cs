using Ntilde.Shell;

namespace Ntilde.Tests.Shell;

/// <summary>How a titled OSC 777 / OSC 99 notification maps onto the in-app toast.</summary>
public class OscNotificationToastTests
{
    [Theory]
    [InlineData("Pane", "Title", "Body", "Title", "Body")]    // both: as given
    [InlineData("Pane", "Only title", "", "Pane", "Only title")] // lone title: message under the pane title
    [InlineData("Pane", "", "Only body", "Pane", "Only body")]   // lone body: same
    public void Resolves_title_and_message(string pane, string title, string body, string expectedTitle, string expectedMessage)
    {
        Assert.Equal((expectedTitle, expectedMessage), OscNotificationToast.Resolve(pane, title, body));
    }
}
