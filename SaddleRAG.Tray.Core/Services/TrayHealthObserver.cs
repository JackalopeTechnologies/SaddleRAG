// TrayHealthObserver.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using System.Net.Http;
using System.Text.Json;

namespace SaddleRAG.Tray.Services;

/// <summary>Reads health even on HTTP 503 and announces each changed problem once.</summary>
public sealed class TrayHealthObserver(HttpClient client, Uri baseAddress)
{
    private const string HealthPath = "/health";
    private const string StatusProperty = "status";
    private const string UnavailableStatus = "Unavailable";
    private const string HealthySummary = "SaddleRAG — healthy";
    private const string HealthPage = "/monitor/health";
    private const string NotificationsProperty = "notifications";
    private const string WarningsProperty = "warnings";
    private const string ErrorsProperty = "errors";
    private const string HasWarningsProperty = "hasWarnings";
    private const string HasErrorsProperty = "hasErrors";
    private const string UnreadErrorsSummary = "Unread server errors; open logs";
    private const string UnreadWarningsSummary = "Unread server warnings; open logs";
    private const string ReadFailedProperty = "readFailed";
    private const string LogReadFailedSummary = "Unable to read server logs";
    private const string ProductPrefix = "SaddleRAG — ";
    private const string LogsPage = "/monitor/logs";
    private const string LogNotificationPrefix = "logs:";
    private const string NewestProperty = "newestAt";
    private const string HealthyStatus = "Healthy";
    private const string ServicesProperty = "services";
    private const string NameProperty = "name";
    private const string ServiceSeparator = ", ";
    private const string UnavailableSuffix = " unavailable";
    private const string StartingStatus = "Starting";
    private const string ServiceNotificationPrefix = "services:";
    private const string KeySeparator = ":";
    private string? mLastNotificationKey;

    public async Task<TrayAttention> ReadAsync(CancellationToken cancellationToken)
    {
        TrayAttention result;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            using HttpResponseMessage response = await client.GetAsync(new Uri(baseAddress, HealthPath), timeout.Token);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            result = Parse(document.RootElement);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested
            && exception is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException)
        {
            result = TrayAttention.Unreachable;
        }
        return result;
    }

    public bool ShouldNotify(TrayAttention attention)
    {
        ArgumentNullException.ThrowIfNull(attention);
        bool result = !string.IsNullOrEmpty(attention.NotificationKey)
                      && attention.NotificationKey != mLastNotificationKey;
        mLastNotificationKey = attention.NotificationKey;
        return result;
    }

    internal static TrayAttention Parse(JsonElement health)
    {
        string status = health.GetProperty(StatusProperty).GetString() ?? UnavailableStatus;
        var result = new TrayAttention(McpServiceState.Running, HealthySummary, HealthPage, "");
        if (health.TryGetProperty(NotificationsProperty, out JsonElement notifications)
            && notifications.ValueKind == JsonValueKind.Object)
        {
            int warnings = notifications.GetProperty(WarningsProperty).GetInt32();
            int errors = notifications.GetProperty(ErrorsProperty).GetInt32();
            bool hasWarnings = warnings > 0 || ReadFlag(notifications, HasWarningsProperty);
            bool hasErrors = errors > 0 || ReadFlag(notifications, HasErrorsProperty);
            bool readFailed = notifications.GetProperty(ReadFailedProperty).GetBoolean();
            if (hasWarnings || hasErrors || readFailed)
            {
                string unread = hasErrors ? UnreadErrorsSummary : UnreadWarningsSummary;
                string detail = readFailed ? LogReadFailedSummary
                    : unread;
                result = new(hasErrors || readFailed ? McpServiceState.Stopped : McpServiceState.Transitioning,
                             ProductPrefix + detail, LogsPage,
                             LogNotificationPrefix + notifications.GetProperty(NewestProperty).ToString() + KeySeparator + readFailed);
            }
        }
        if (status != HealthyStatus)
        {
            string failed = "";
            if (health.TryGetProperty(ServicesProperty, out JsonElement services))
                failed = string.Join(ServiceSeparator, services.EnumerateArray()
                    .Where(service => service.GetProperty(StatusProperty).GetString() == UnavailableStatus)
                    .Select(service => service.GetProperty(NameProperty).GetString()));
            string detail = string.IsNullOrEmpty(failed) ? status : failed + UnavailableSuffix;
            result = new(status == UnavailableStatus ? McpServiceState.Stopped : McpServiceState.Transitioning,
                         ProductPrefix + detail, HealthPage,
                         status == StartingStatus ? "" : ServiceNotificationPrefix + detail);
        }
        return result;
    }

    private static bool ReadFlag(JsonElement notifications, string property) =>
        notifications.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.True;
}
