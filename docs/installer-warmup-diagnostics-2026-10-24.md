# Installer warmup and Phi-3 startup diagnostics

## Observed failures

The Windows 1.14.0 installation completed successfully, but its temporary `--prewarm`
host ran as SYSTEM while ASP.NET Core selected the installing user's profile key
folder. It retried one user-owned DPAPI key eleven times, then created a SYSTEM-owned
key in that folder. The actual service started separately and did not report those
key errors. This installer startup behavior predates 1.14.0.

The service also reported three native ONNX warnings while loading the existing
Phi-3 DirectML model: CPU shape operations with a graph-capture caution, mixed
execution-provider placement, and guidance for obtaining detailed node assignments.
Identical messages occurred before the upgrade. These messages alone do not mean
the whole model fell back to the CPU, and successful startup does not prove every
possible model input is safe for graph capture.

## Disposable prewarm keys

Prewarm selects an ephemeral Data Protection provider before the host is built.
Normal Production and Development key configuration remains separate. In
Development, the existing application-specific directory and Windows DPAPI
protection are retained.

An ephemeral provider alone is insufficient with ASP.NET Core 10.0.13: the
framework's `DataProtectionHostedService` still preloads the persistent key ring.
The final prewarm registration therefore removes only that framework preloader,
identified by its assembly and full implementation type name. Other hosted
services are retained. The expected registration is checked so a framework change
cannot silently restore persistent key access. This is a narrow dependency on a
framework implementation detail, covered by real host-start regression tests.

Prewarm progress uses Information severity; exceptions still produce errors and a
nonzero exit status. No key file is deleted, re-encrypted, or given broader access.
The key created by the earlier installer is retained because other applications'
use of the shared key directory has not been qualified.

Official references:

- [Default key storage](https://learn.microsoft.com/en-us/aspnet/core/security/data-protection/configuration/default-settings?view=aspnetcore-10.0)
- [Ephemeral Data Protection provider](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.dataprotection.dataprotectionbuilderextensions.useephemeraldataprotectionprovider?view=aspnetcore-10.0)
- [ASP.NET Core Data Protection source](https://github.com/dotnet/aspnetcore/tree/v10.0.13/src/DataProtection/DataProtection/src)

## Native diagnostic boundary

The compatible DirectML packages remain Runtime 1.24.4 and GenAI 0.14.1; official
package feeds offered no newer compatible fix during this review. Changing the
classifier, adding a compute fallback, disabling graph capture, or globally raising
the native log threshold would change behavior or obscure unrelated failures.

The narrow diagnostic treatment retains the complete native messages at Information
severity only for the verified successful startup of the known Phi-3 DirectML
configuration. Eligibility also requires the reviewed Runtime 1.24.4 / GenAI
0.14.1 binaries and the official 2,109,332-byte ONNX graph fingerprint
`21e1c8ca461e8affb8344832c0cae22c9125cde31ca8b58e16f4b70cb840d4c6`.
The fingerprint matches Microsoft's model metadata at revision
`5f5f794c1c23c9d5ee142af85df02a6cc52d6945`; external weights are not hashed or
modified. Unknown versions, changed graph/configuration, or unavailable metadata
retain warnings and need a new review before this classification can apply.

The configuration guard requires the built-in registry name, repository and folder;
`phi3` with decoder `model.onnx`; only `log_id` and `provider_options` session keys;
one empty DML provider option; shared past/present buffers; one beam and one return
sequence; absent or single-item `batch_size`; and no `chunk_size` or decoder
`sliding_window`. Runtime eligibility applies only to the DirectML build and uses
the loaded Runtime API version plus native GenAI product version. Failed metadata
inspection keeps the original warnings. Checking a changed graph's length first
avoids hashing an unexpectedly large model file.

A unique decoder session identifier separates concurrent model loads. The scope
requires the complete expected message sequence and successful
model/tokenizer construction without an unrelated warning or error. Incomplete,
failed, unknown-model, changed-configuration, and unrelated-session diagnostics keep
their original severity. Captured diagnostics are bounded and are flushed when the
load scope ends.

This is a documented last-resort diagnostic classification, not a repair to native
graph execution and not a claim that CPU shape operations disappeared. The full
graph-capture caution remains readable in the log. Actual errors, device-loss
warnings, unexpected placement, and diagnostics outside the verified scope remain
visible. The existing exact `gpu_graph_id` replacement handling is separate.

The upstream guard emits the shape warning only after rejecting incompatible
compute placement. GenAI captures the one-token decoder path, uses a separate graph
identifier per generation, and allocates static attention-mask/shared-cache buffers
for that path. The retained model configuration uses one beam and shared past/present
buffers. These are the reasons for the narrow eligibility rules; they are not
general permission to classify arbitrary model placement warnings as harmless.

Relevant upstream code:

- [Reviewed Microsoft Phi-3 graph](https://huggingface.co/microsoft/Phi-3-mini-4k-instruct-onnx/blob/5f5f794c1c23c9d5ee142af85df02a6cc52d6945/directml/directml-int4-awq-block-128/model.onnx)
- [ONNX Runtime graph-capture and shape guards](https://github.com/microsoft/onnxruntime/blob/2d924974ef147392ced8409d36bd6d2e7fcc8a74/onnxruntime/core/session/inference_session.cc)
- [ONNX Runtime provider-placement diagnostics](https://github.com/microsoft/onnxruntime/blob/2d924974ef147392ced8409d36bd6d2e7fcc8a74/onnxruntime/core/framework/session_state.cc)
- [GenAI decoder capture condition](https://github.com/microsoft/onnxruntime-genai/blob/a30f479af016cb098688726831a9acbb8d19f0b2/src/models/decoder_only.cpp)
- [GenAI graph identifiers](https://github.com/microsoft/onnxruntime-genai/blob/a30f479af016cb098688726831a9acbb8d19f0b2/src/models/model.cpp)
- [GenAI static position and mask inputs](https://github.com/microsoft/onnxruntime-genai/blob/a30f479af016cb098688726831a9acbb8d19f0b2/src/models/position_inputs.cpp)
- [GenAI shared cache requirements](https://github.com/microsoft/onnxruntime-genai/blob/a30f479af016cb098688726831a9acbb8d19f0b2/src/models/kv_cache.cpp)

## Verification and delivery boundary

Source, regression, release-artifact and installed-service evidence are recorded
separately. Builds are bounded to two workers and test runs to four threads. The
prewarm regression uses a repository that throws on any persistent key read or
write; normal hosts must retain persistent protection across host instances.
Native regressions exercise failed/incomplete and concurrent scopes as well as the
existing staged Phi-3 structured-classification test.

Local validation on Windows passed a full Release rebuild with warnings treated as
errors (zero warnings and errors) and the full suite: 2,738 passed, 24 skipped,
zero failed. The nine real-host Data Protection regressions and 62 focused native
diagnostic checks passed. A separate real staged Phi-3 test confirmed the reviewed
graph/runtime eligibility, retained all three native messages at Information, and
produced the expected structured classification. Platform-specific skipped tests
are not claimed as locally executed; Linux validation remains a CI gate.

Before installation, the published release must be the latest stable release,
contain the reviewed fix commit, and match official asset sizes, hashes, package
identity and embedded component versions. Immediate before/after snapshots compare
configuration bytes, model selections, corpus/profile inventories and user key-folder
metadata without reading keys. Runtime acceptance includes startup logs, MCP skills
and resource digests, hostile-Origin rejection, 43 saved searches, three MCP corpus
searches and default/explicit-profile equality.

Publication and installation results belong in the task's delivery record after
those operations actually complete; this source document alone does not establish
either outcome.
