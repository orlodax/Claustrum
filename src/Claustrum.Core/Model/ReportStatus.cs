namespace Claustrum.Core.Model;

/// <summary>Outcome of looking for a <c>claustrum-report</c> fence in a run's final message.</summary>
public enum ReportStatus
{
    Ok,
    Missing,
    Unparsed,
}
