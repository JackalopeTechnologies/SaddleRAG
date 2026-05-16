#!/usr/bin/env bash
# Run the test suite with code-coverage collection and produce an HTML report.
#
# 1. Restores the local dotnet tool manifest (dotnet-reportgenerator-globaltool).
# 2. Runs `dotnet test SaddleRAG.Tests` with --collect:"XPlat Code Coverage"
#    into ./coverage-results.
# 3. Generates an HTML report under ./coverage-results/html and prints the
#    summary. On macOS/Linux desktops with `xdg-open` (Linux) or `open`
#    (macOS) available, the report opens in the default browser unless
#    --no-open is passed.
#
# Coverage gating is NOT enforced — the script exits 0 on a successful
# test run regardless of the coverage percentage.
#
# Usage:
#   scripts/coverage.sh                       # default: skip integration tests
#   scripts/coverage.sh --no-open             # don't launch a browser
#   scripts/coverage.sh --filter "Category=Bench"   # custom xUnit filter
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RESULTS_DIR="${REPO_ROOT}/coverage-results"
HTML_DIR="${RESULTS_DIR}/html"
FILTER="Category!=Integration"
NO_OPEN=0

while [[ $# -gt 0 ]]; do
    case "$1" in
        --no-open) NO_OPEN=1; shift ;;
        --filter)  FILTER="$2"; shift 2 ;;
        *)         echo "Unknown argument: $1" >&2; exit 64 ;;
    esac
done

rm -rf "${RESULTS_DIR}"

echo "Restoring dotnet tools..."
dotnet tool restore --tool-manifest "${REPO_ROOT}/.config/dotnet-tools.json" >/dev/null

echo "Running tests with coverage..."
dotnet test "${REPO_ROOT}/SaddleRAG.Tests/SaddleRAG.Tests.csproj" \
    --collect:"XPlat Code Coverage" \
    --results-directory "${RESULTS_DIR}" \
    --filter "${FILTER}" \
    --nologo

# coverage.cobertura.xml lands in a randomly-named guid subdir per test run.
mapfile -t COBERTURA_FILES < <(find "${RESULTS_DIR}" -name 'coverage.cobertura.xml')
if [[ "${#COBERTURA_FILES[@]}" -eq 0 ]]; then
    echo "No coverage.cobertura.xml produced." >&2
    exit 1
fi

# reportgenerator wants a semicolon-separated reports list.
REPORTS_ARG=""
for f in "${COBERTURA_FILES[@]}"; do
    REPORTS_ARG+="${f};"
done
REPORTS_ARG="${REPORTS_ARG%;}"

echo "Generating HTML report..."
dotnet tool run reportgenerator \
    "-reports:${REPORTS_ARG}" \
    "-targetdir:${HTML_DIR}" \
    "-reporttypes:HtmlInline_AzurePipelines;TextSummary;MarkdownSummaryGithub"

echo ""
cat "${HTML_DIR}/Summary.txt"

if [[ "${NO_OPEN}" -eq 0 ]]; then
    INDEX="${HTML_DIR}/index.html"
    if [[ -f "${INDEX}" ]]; then
        if command -v xdg-open >/dev/null 2>&1; then
            xdg-open "${INDEX}" >/dev/null 2>&1 || true
        elif command -v open >/dev/null 2>&1; then
            open "${INDEX}" >/dev/null 2>&1 || true
        fi
    fi
fi
