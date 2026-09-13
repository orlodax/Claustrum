using Claustrum.Core.Model;

namespace Claustrum.Core.Report;

public sealed record ExtractedReport(ClaustrumReport? Report, ReportStatus Status, string[] Warnings);
