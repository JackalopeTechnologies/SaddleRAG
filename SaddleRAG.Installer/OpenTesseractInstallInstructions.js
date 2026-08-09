// OpenTesseractInstallInstructions.js
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

function SaddleRagInstallerAction()
{
var _shell = new ActiveXObject("WScript.Shell");
_shell.Run("https://tesseract-ocr.github.io/tessdoc/Installation.html", 1, false);
return 1;
}
