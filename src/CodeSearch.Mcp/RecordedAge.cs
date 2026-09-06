using System.Globalization;
using CodeSearch.Mcp.Resources;

namespace CodeSearch.Mcp;

/// <summary>
/// How long ago a build wrote what it wrote, in the unit a reader reads.
///
/// One home rather than two, because both faces print it: the `Sync:` line above an answer and the
/// `Updated:` row of `index_status`. Two ladders would agree until the hour boundary and then say
/// `192.0 min` on one surface and `3.2 h` on the other about the same record.
///
/// It climbs all the way to days because this is the one figure here that is routinely large. A
/// build whose process died leaves a record nothing ever clears, so the honest report of it is
/// `2.4 d` rather than `3456.0 min`.
/// </summary>
internal static class RecordedAge
{
    /// <summary>
    /// Never negative: a clock that disagrees with the writer of the file by a second would
    /// otherwise report the future.
    /// </summary>
    internal static string Since(TimeSpan since)
    {
        var age = since < TimeSpan.Zero ? TimeSpan.Zero : since;
        return age switch
        {
            _ when age < TimeSpan.FromMinutes(1) => CodeSearchText.Seconds(
                age.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)),
            _ when age < TimeSpan.FromHours(1) => CodeSearchText.EtaMinutes(
                age.TotalMinutes.ToString("F1", CultureInfo.InvariantCulture)),
            _ when age < TimeSpan.FromDays(1) => CodeSearchText.Hours(
                age.TotalHours.ToString("F1", CultureInfo.InvariantCulture)),
            _ => CodeSearchText.Days(
                age.TotalDays.ToString("F1", CultureInfo.InvariantCulture)),
        };
    }

    /// <summary>For a recorded moment, which is how every caller has it.</summary>
    internal static string Of(DateTimeOffset recordedAtUtc) =>
        Since(DateTimeOffset.UtcNow - recordedAtUtc);
}
