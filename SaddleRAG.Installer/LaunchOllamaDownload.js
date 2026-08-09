// LaunchOllamaDownload.js
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

function SaddleRagInstallerAction()
{
try { new ActiveXObject("WScript.Shell").Run("https://ollama.com"); } catch(e) {}
return 1;
}
