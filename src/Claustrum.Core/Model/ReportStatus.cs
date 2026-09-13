using System.Text.Json.Serialization;
using Claustrum.Core.Json;

namespace Claustrum.Core.Model;

/// <summary>Outcome of looking for a <c>claustrum-report</c> fence in a run's final message.</summary>
[JsonConverter(typeof(SnakeCaseEnumConverter<ReportStatus>))]
public enum ReportStatus
{
    Ok,
    Missing,
    Unparsed,
}
