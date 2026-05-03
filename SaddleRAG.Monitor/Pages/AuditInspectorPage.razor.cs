// AuditInspectorPage.razor.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings
using Microsoft.AspNetCore.Components;
using MudBlazor;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models.Audit;
#endregion

namespace SaddleRAG.Monitor.Pages;

public abstract class AuditInspectorPageBase : ComponentBase
{
    [Parameter] public string JobId { get; set; } = string.Empty;
    [Inject] private IScrapeAuditRepository? AuditRepo { get; set; }

    protected AuditSummary?                      Summary { get; private set; }
    protected IReadOnlyList<ScrapeAuditLogEntry> Entries { get; private set; } = [];

    protected string? FilterStatus     { get; set; }
    protected string? FilterSkipReason { get; set; }
    protected string? FilterHost       { get; set; }
    protected string? FilterUrl        { get; set; }

    private const int DefaultEntryLimit = 200;

    protected static readonly string[] pmStatusOptions = Enum.GetNames<AuditStatus>();
    protected static readonly string[] pmReasonOptions = Enum.GetNames<AuditSkipReason>();

    protected override async Task OnParametersSetAsync()
    {
        await LoadAsync();
    }

    protected async Task ApplyFilters()
    {
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        ArgumentNullException.ThrowIfNull(AuditRepo);
        Summary = await AuditRepo.SummarizeAsync(JobId);

        AuditStatus?     status = ParseEnum<AuditStatus>(FilterStatus);
        AuditSkipReason? reason = ParseEnum<AuditSkipReason>(FilterSkipReason);

        Entries = await AuditRepo.QueryAsync(JobId, status, reason,
                                             FilterHost, FilterUrl,
                                             limit: DefaultEntryLimit);
    }

    protected static Color StatusColor(string status) => status switch
    {
        "Indexed" => Color.Success,
        "Fetched" => Color.Info,
        "Skipped" => Color.Warning,
        "Failed"  => Color.Error,
        _         => Color.Default
    };

    private static T? ParseEnum<T>(string? raw) where T : struct, Enum
    {
        T? result = null;
        if (!string.IsNullOrEmpty(raw) && Enum.TryParse<T>(raw, ignoreCase: true, out var v))
            result = v;
        return result;
    }
}
