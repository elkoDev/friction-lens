# FrictionLens — Decision & Friction Intelligence Platform

A generalizable, zero-cost local-first (and cloud-ready) full-stack MVP built on **.NET 11 (ASP.NET Core + EF Core 11)** and **React + TypeScript**.

Instead of generic sentiment scores, FrictionLens extracts **structured buyer switching decisions, deal-breaker friction points, and unmet product requirements**—every claim deterministically grounded against its verbatim quote.

## Architecture Highlights

- **Strongly Typed Backend (`.NET 11` / `C#`):** Minimal APIs, immutable record DTOs, `Channel<T>`-backed `BackgroundService` ingestion worker, OpenAPI schema.
- **Zero-Cost Local → Cloud Migration:**
  - **Database:** Defaults to local **SQLite** (`frictionlens.db`). Switch to **PostgreSQL** in `appsettings.json` (`"Database:Provider": "Postgres"`) for cloud deployment.
  - **Extraction Engine (Pluggable):**
    1. `Heuristic` *(default — \$0, works offline with zero downloads)*
    2. `Ollama` *(local LLM via `qwen2.5:7b` / `llama3.1:8b` — \$0)*
    3. `OpenAiCompatible` *(OpenAI, Gemini OpenAI endpoint, OpenRouter, Groq, vLLM)*
- **Compliant Source Connectors:**
  - **Hacker News** (free Algolia Search API)
  - **GitHub Public Issues** (free Search API, optional `GITHUB_TOKEN`)
  - **Discourse Forums** (works on any public Discourse community via `/search.json`)
  - **Manual Paste / First-Party Import** (for customer interviews, Zendesk/Gong exports, or sensitive verticals)
- **Hallucination Prevention:** Programmatic `GroundingVerifier` validates verbatim quotes against raw source text before marking an insight as grounded.

## Quickstart (\$0 Local Setup)

### 1. Run the .NET 11 Backend
```bash
dotnet run --project backend/src/FrictionLens.Api
```
The API starts at `http://localhost:5055`, creates `frictionlens.db`, and seeds two contrasting demo workspaces (*B2B DevTools* + *High-Consideration Hardware*).

### 2. Run the React + TypeScript Frontend
```bash
cd frontend
npm install
npm run dev
```
Open `http://localhost:5173`.

## Upgrading to Local LLM (Ollama) or Cloud Postgres

Start Postgres + Ollama locally via Docker Compose:
```bash
docker compose up -d
docker exec -it $(docker ps -qf "ancestor=ollama/ollama") ollama pull qwen2.5:7b
```
Then update `backend/src/FrictionLens.Api/appsettings.json`:
```json
{
  "Database": { "Provider": "Postgres" },
  "Extraction": { "Provider": "Ollama", "OllamaModel": "qwen2.5:7b" }
}
```
Or point `Extraction:Provider` to `"OpenAiCompatible"` and set `LLM_API_KEY` for cloud LLMs.
