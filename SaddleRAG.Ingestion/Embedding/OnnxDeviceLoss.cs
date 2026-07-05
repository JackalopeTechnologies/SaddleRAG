// OnnxDeviceLoss.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Ingestion.Embedding;

/// <summary>
///     Detects the DXGI device-removed family of failures in ONNX Runtime
///     exception messages (issue #144). DirectML surfaces GPU device loss —
///     driver reset, sleep/resume, TDR — as an
///     <c>OnnxRuntimeException</c> whose message embeds the DXGI HRESULT;
///     the managed exception carries no structured code, so substring
///     matching on the well-known hex values is the only detection seam.
/// </summary>
public static class OnnxDeviceLoss
{
    /// <summary>
    ///     True when the exception message contains a DXGI device-loss
    ///     HRESULT (device suspended/removed, hung, reset, or driver
    ///     internal error).
    /// </summary>
    public static bool IsDeviceLossMessage(string? message)
    {
        var result = false;
        if (!string.IsNullOrEmpty(message))
            result = smDeviceLossCodes.Any(code => message.Contains(code, StringComparison.OrdinalIgnoreCase));

        return result;
    }

    private const string DxgiErrorDeviceRemoved = "887A0005";
    private const string DxgiErrorDeviceHung = "887A0006";
    private const string DxgiErrorDeviceReset = "887A0007";
    private const string DxgiErrorDriverInternalError = "887A0020";

    private static readonly string[] smDeviceLossCodes =
        [DxgiErrorDeviceRemoved, DxgiErrorDeviceHung, DxgiErrorDeviceReset, DxgiErrorDriverInternalError];
}
