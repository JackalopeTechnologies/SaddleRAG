// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
namespace SaddleRAG.ClassifierComparison;

internal sealed record ComparisonResult(string RelativePath, bool PassedValidation, string Primary,
    string[] Secondary, string Error, double Seconds, int Calls, long PeakModelGpuBytes, bool GpuPlacementObserved);
