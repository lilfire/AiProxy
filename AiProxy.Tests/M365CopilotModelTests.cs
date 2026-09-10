using AiProxy.Services.Providers;

namespace AiProxy.Tests;

[TestClass]
public sealed class M365CopilotModelTests
{
    // These are the identifiers exposed by the M365 app's model selector.
    // Gpt_Quick/Gpt_Reasoning are obsolete and can fail Chat invocation.
    [TestMethod]
    [DataRow("quick", "Chat")]
    [DataRow("think-deeper", "Reasoning")]
    [DataRow("gpt-5.6-think-deeper", "Gpt_5_6_Reasoning")]
    [DataRow("gpt-5.5-quick", "Gpt_5_5_Chat")]
    [DataRow("auto", "magic")]
    public void Model_selection_uses_the_app_tone(string model, string expectedTone)
    {
        Assert.AreEqual(expectedTone, M365CopilotProvider.GetToneForModel(model));
    }
}
