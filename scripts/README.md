# scripts/

Maintenance and developer-workflow scripts. None are required to build or run SaddleRAG — they wrap repetitive commands so you don't have to remember the flags.

## Coverage — `coverage.ps1` / `coverage.sh`

Run the test suite with code-coverage collection and produce an HTML report. The PowerShell and bash entry points behave identically; pick whichever matches your shell.

```powershell
# Windows
scripts/coverage.ps1
```

```bash
# Linux / macOS
scripts/coverage.sh
```

### What they do

1. Restore the local `dotnet-tools.json` manifest (gets `reportgenerator` 5.5.10 from the pinned version).
2. Run `dotnet test SaddleRAG.Tests --collect:"XPlat Code Coverage"` into `./coverage-results`. Default filter is `Category!=Integration` so the run matches CI.
3. Run `reportgenerator` against the resulting `coverage.cobertura.xml` to produce `./coverage-results/html/index.html` (with `Summary.txt` and `SummaryGithub.md` alongside).
4. Print the text summary, then open the HTML report in your default browser.

### Flags

| PowerShell | Bash | Effect |
|---|---|---|
| `-NoOpen` | `--no-open` | Skip the browser launch (useful in CI or headless runs). |
| `-Filter <expr>` | `--filter <expr>` | Override the default `Category!=Integration` xUnit filter. |

### ReportGenerator Pro license (optional)

If you have a [ReportGenerator Pro](https://reportgenerator.io/pro) license, set the `REPORTGENERATOR_LICENSE` environment variable and the tool will pick it up automatically — no script change needed:

```powershell
[System.Environment]::SetEnvironmentVariable('REPORTGENERATOR_LICENSE', 'your-key', 'User')
```

```bash
export REPORTGENERATOR_LICENSE="your-key"  # ~/.bashrc or ~/.zshrc
```

CI reads the same env var from the `REPORTGENERATOR_LICENSE` repo secret. Without the secret, the tool runs in free-tier mode — CI doesn't break.

### Gating policy

No coverage gate is enforced. The scripts exit 0 on a successful test run regardless of coverage percentage. Picking a hard threshold at the current floor would just churn PRs — the right time to add a gate is after intentional coverage lifts on the assemblies that drag the average down (`SaddleRAG.Cli`, `SaddleRAG.Monitor`, `SaddleRAG.Database`).

If you want to enforce one later, parse `./coverage-results/html/Summary.txt` (or the cobertura XML's root `line-rate` attribute) and exit non-zero when below the threshold.

### CI parity

`build-linux` in `.github/workflows/build.yml` runs the same `--collect` flag and the same `reportgenerator` invocation, then:

- Renders the markdown summary on the workflow run page via `$GITHUB_STEP_SUMMARY`.
- Posts/updates a sticky coverage comment on the PR.
- Uploads the cobertura XML + full HTML drill-down report as a workflow artifact.

Local results should match CI's exactly when run on the same revision with the same filter.
