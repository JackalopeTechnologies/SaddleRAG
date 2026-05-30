// IMcpServiceController.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

namespace SaddleRAG.Tray.Services;

public interface IMcpServiceController
{
    McpServiceState GetState();

    void Start();

    void Stop();
}
