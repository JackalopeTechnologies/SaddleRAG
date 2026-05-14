# SaddleRAG.Installer.Logic

Test-oracle library for the SaddleRAG MSI installer's JScript custom actions.

## Why this project exists

The MSI installer at `SaddleRAG.Installer/` runs JScript inside the Windows
Installer service. JScript is not .NET — the MSI custom-action host does not
load managed assemblies, so the installer cannot call this library at runtime.

What this library provides is a **testable mirror** of the heuristics the
JScript custom actions use. The C# copy is the source of truth for unit
tests; the JScript copy is what actually runs in production. The two must
be kept in sync by hand. A divergence between them is the exact regression
class this project was created to catch.

## What lives here

- **`GpuDetectionRules`** — the Microsoft-fallback-adapter classification
  used by `CheckGpuCapability.js`. Mirrored adapter-name fragments and PnP
  device-ID prefixes. Behavior-level mirror is held by
  `SaddleRAG.Tests/Installer/GpuDetectionRulesTests.cs`; the cross-language
  string-literal mirror is held by `JScriptMirrorContainsAllExpectedFragmentsAndPrefixes`
  in the same test class.

## What does NOT belong here

- Code the production runtime calls. That belongs in `SaddleRAG.Core`,
  `SaddleRAG.Ingestion`, etc.
- Pure WiX schema or build wiring. Those live in `SaddleRAG.Installer/`.
- General utility helpers. The scope is specifically "logic mirrored from
  an installer custom action so it can be unit-tested." Anything else
  belongs in its proper project.

## When adding a new mirror

1. Implement the rule in the relevant JScript file under `SaddleRAG.Installer/`.
2. Mirror it in a new static class here. Keep the implementation
   visually diffable line-for-line with the JScript.
3. Add tests in `SaddleRAG.Tests/Installer/` that exercise the C# directly,
   plus a string-literal parity test that loads the JScript as text and
   asserts the live installer copy still contains the same constants.
4. Update the JScript header to point at the C# class so a future maintainer
   editing one side finds the other.
