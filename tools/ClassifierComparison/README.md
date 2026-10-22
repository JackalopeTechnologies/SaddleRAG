# Local subject-classifier comparison

This tool runs the production Ollama classifier, structured subject prompt,
validation, bounded retry, catalog reconciliation, and assignment code against
previously exported `SubjectDescriptor` records. Repositories are in-memory
substitutes: it never connects to MongoDB, changes installed settings, or writes
classifications to the live library.

Build with `dotnet build tools/ClassifierComparison/ClassifierComparison.csproj
-c Release -m:2 -p:TreatWarningsAsErrors=true` from the repository working directory.
Run the built `SaddleRAG.ClassifierComparison.dll` with these positional arguments:

```text
<descriptors.json> <new-output-directory> <ollama-model-tag> [document-count]
```

Use an existing exported corpus and an output directory that does not exist.
The optional count supports a small smoke check; omit it for the complete corpus.
The tool sorts documents by their stored identifier for a repeatable order.

Ollama must already be running at `http://localhost:11434`, with the requested
model installed. Downloading a model and running inference require the user's
authorization. Run models sequentially. Fixed comparison settings are temperature
0, seed 42, context 8192, output limit 2048, thinking disabled, two CPU threads,
all layers requested on the graphics card, and keep-alive 0. No other model or
system settings are changed. These are comparison settings, not a proposed live
configuration. Measurements include any model loading that occurs. Consecutive
requests can arrive before Ollama completes unloading, so these are batch
timings, not guaranteed cold-load timings for every document. Verify an empty
resident-model list before starting the next model's run and after the last run.

Each document has a five-minute deadline, including the production retry.
Ollama's resident-model endpoint is sampled every 250 milliseconds. If its
reported graphics allocation is smaller than its reported loaded model size,
the request is canceled. Infrastructure failures stop the run after saving the
partial results; classification validation failures are recorded and the next
document is attempted. Inspect `GpuPlacementObserved` before claiming graphics
placement was measured. Allocation is Ollama's reported model allocation, not
total graphics-card memory use or a process-wide peak.

The output preserves the input checksum, prompt version and settings in
`run.json`, every submitted prompt/schema and reply, per-document timing and
validation in `results.json`, and the resulting in-memory `catalog.json`.
Treat the output as document content: keep raw evidence local unless separately
authorized to publish it. A successful validation only proves that the response
passes the production structural/evidence checks. Review subject identity,
secondary subjects, aliases, and cited evidence separately before recommending
a model. Repeated exports of one document are not independent test documents.
