// MonitorDirectoryLibraryEndpoints.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion.Scanning;
using SaddleRAG.Mcp.Auth;
using SaddleRAG.Monitor.Services;

namespace SaddleRAG.Mcp.Api;

/// <summary>Explicit Monitor actions for user-owned directory libraries.</summary>
public static class MonitorDirectoryLibraryEndpoints
{
    /// <summary>Maps directory actions behind the established Monitor write policy.</summary>
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var group = app.MapGroup(DirectoryLibrariesRoute)
                       .RequireAuthorization(DiagnosticsWriteRequirement.PolicyName);
        group.MapPost(TestAccessPath, TestAccess);
        group.MapPost(RegisterPath, RegisterAsync);
        group.MapPost(ScanPath, ScanAsync);
    }

    private static IResult TestAccess(DirectoryAccessTestRequest request,
                                      DirectoryRootValidator validator)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(validator);
        DirectoryRootValidationResult validation = validator.Validate(request.RootPath);
        var response = new DirectoryAccessTestResult(validation.Succeeded,
                                                     validation.ReasonCode,
                                                     validation.Detail);
        return Results.Ok(response);
    }

    private static async Task<IResult> RegisterAsync(DirectoryRegistrationRequest request,
                                                      string? profile,
                                                      IDirectoryLibraryRegistrationService registration,
                                                      CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(registration);
        DirectoryRegistrationResult result = await registration.RegisterAsync(request, profile, ct);
        return Results.Ok(result);
    }

    private static async Task<IResult> ScanAsync(string libraryId,
                                                  string? profile,
                                                  IDirectoryScanJobQueue scans,
                                                  CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentNullException.ThrowIfNull(scans);
        DirectoryScanQueueResult result = await scans.QueueAsync(libraryId, profile, ct);
        return Results.Ok(result);
    }

    private const string DirectoryLibrariesRoute = "/api/monitor/directory-libraries";
    private const string TestAccessPath = "/test-access";
    private const string RegisterPath = "/register";
    private const string ScanPath = "/{libraryId}/scan";
}
