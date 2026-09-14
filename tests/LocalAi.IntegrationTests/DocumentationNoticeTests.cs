using System.Globalization;
using LocalAi.Cli;
using LocalAi.Contracts;

namespace LocalAi.IntegrationTests;

public sealed class DocumentationNoticeTests
{
    [Fact]
    public void The_receipt_reports_actual_durations_and_marks_unmeasured_savings()
    {
        var notice = DocumentationNotice.Format("fixture-model", TimeSpan.FromSeconds(2.5), TimeSpan.FromSeconds(12.3),
            ResidencyShortfall.None, null, CultureInfo.GetCultureInfo("en"));
        Assert.Contains("fixture-model", notice);
        Assert.Contains("Queue: 2.5s", notice);
        Assert.Contains("Execution: 12.3s", notice);
        Assert.Contains("Cloud token savings were not measured.", notice);
    }
    [Theory]
    [InlineData(ResidencyShortfall.PartialOffload, 80, "80%")]
    [InlineData(ResidencyShortfall.Cpu, null, "процессоре")]
    public void Relaxed_residency_remains_visible_next_to_the_model(ResidencyShortfall shortfall, int? percent, string mark)
    {
        var notice = DocumentationNotice.Format("fixture-model", TimeSpan.Zero, TimeSpan.FromSeconds(12.3), shortfall, percent, CultureInfo.GetCultureInfo("ru"));
        Assert.Contains("fixture-model (", notice);
        Assert.Contains(mark, notice);
        Assert.Contains("12.3 с", notice);
        Assert.Contains("не измерялась", notice);
    }
}
