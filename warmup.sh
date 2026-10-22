#!/usr/bin/env bash
# warmup.sh
# Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See the LICENSE file in the repo root.
# warmup.sh — run once after first `docker compose up -d` to download models.
# New installations use Ollama embeddinggemma-2:270m + phi4-mini:3.8b.
# Optional native models are downloaded only when selected.
# Models are stored in named volumes and will NOT be re-downloaded on restart.
# The recon model (phi4:14b, ~8 GB) is not downloaded here — opt-in only:
#   docker compose exec ollama ollama pull phi4:14b

set -euo pipefail

echo ""
echo "SaddleRAG model download"
echo "========================"
echo "Downloading the selected models; new installations use Ollama with the sorter off."
echo "This is a one-time step. Duration depends on your connection speed (typically 5-15 min)."
echo ""

docker compose exec saddlerag ./SaddleRAG.Mcp --prewarm

echo ""
echo "Warmup complete. SaddleRAG is ready."
echo "Access the admin UI at: http://localhost:6100"
echo ""
