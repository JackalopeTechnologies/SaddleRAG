// OnnxExecutionProvider.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

namespace SaddleRAG.Core.Enums;

/// <summary>
///     Preferred ONNX Runtime execution provider for the embedding and
///     reranker sessions. Bound from <c>Onnx.ExecutionProvider</c> in
///     appsettings.json (case-insensitive string-to-enum binding). CPU is
///     always available; GPU providers only take effect when the build was
///     published with the matching native runtime — see
///     <c>OnnxRuntimeCapabilities.CompiledInProviders</c> for what the
///     running build can actually load.
/// </summary>
public enum OnnxExecutionProvider
{
    /// <summary>
    ///     CPU execution. Always available regardless of build flavor.
    ///     The fallback target when a requested GPU provider isn't
    ///     compiled in or the hardware refuses it.
    /// </summary>
    Cpu,

    /// <summary>
    ///     DirectML execution. Works on any DirectX 12 GPU on Windows
    ///     (Intel / AMD / NVIDIA, no CUDA install required). Requires the
    ///     build to have been compiled with <c>UseGpu=true</c>, which
    ///     swaps the OnnxRuntime NuGet to <c>Microsoft.ML.OnnxRuntime.DirectML</c>.
    /// </summary>
    DirectMl,

    /// <summary>
    ///     CUDA execution on NVIDIA GPUs. Requires the build to reference
    ///     <c>Microsoft.ML.OnnxRuntime.Gpu</c> (<c>UseGpuCuda=true</c> at build
    ///     time) and the host to have CUDA 12.x + cuDNN 9.x installed. Available
    ///     in the Docker <c>:cuda</c> image when run with the NVIDIA Container
    ///     Toolkit. Not available in the Windows MSI or the CPU Docker image.
    /// </summary>
    Cuda
}
