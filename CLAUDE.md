# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

TaskAgent is a Windows desktop app (C# / .NET 8 / Avalonia 11) — a personal task tracker with an AI agent per task, plus bundled utilities (RAM monitor, startup programs, web pages → PDF, PDF → EPUB/Markdown for Kindle with translation). The user is French: UI strings, comments, README and CHANGELOG are all in French; keep it that way.

## Commands

```powershell
dotnet build                     # compile (bin\Debug)
dotnet run                       # run the dev build — window title shows "x.y.z (dev)"
powershell -ExecutionPolicy Bypass -File deploy.ps1   # publish a frozen release
```

There is no test project and no linter. Verify changes by building and, when behaviour matters, running the app.

## Release workflow (do this after every fix/feature)

The user runs the **deployed** version from `releases\x.y.z\`, not `bin\Debug`. A change isn't delivered until it's deployed:

1. Add a section at the top of `CHANGELOG.md`: `## x.y.z — YYYY-MM-DD` (x = big change, y = new feature, z = fix), with `### Ajouté` / `### Corrigé` / `### À savoir` subsections written for the end user.
2. Run `deploy.ps1`. It takes the version from the **first** `## x.y.z` heading, refuses if `releases\x.y.z` already exists, publishes with `-p:Version=…`, writes `releases\current.txt`, updates shortcuts (incl. the user's pinned ones pointing at old versions or `bin\Debug`), keeps the last 3 versions.
3. The running app (`Services/AppVersion.cs`) polls `current.txt` and shows a "version prête → Redémarrer" banner. `CHANGELOG.md` is copied next to the exe and shown in the "Nouveautés" window, so it is user-facing.

The `<Version>` in `TaskAgent.csproj` only applies to dev builds.

## Architecture

- **Views are mostly built in C# code**, not XAML: only `MainWindow` and `ReminderWindow` have `.axaml`; the other windows (`KindleWindow`, `MemoryWindow`, `SitePrintWindow`, `StartupWindow`, `AssistedPrintWindow`, `LinkPickerWindow`, `Dialogs`) construct their controls in code. Compiled bindings are off (`AvaloniaUseCompiledBindingsByDefault=false`).
- **LLM abstraction** (`Services/Llm.cs`): `ILlmProvider` with three implementations chosen by `settings.json → Provider` via `LlmFactory`:
  - `ClaudeCodeProvider` (default) shells out to the `claude` CLI (`claude -p --output-format json --system-prompt-file …`) so the user's Pro/Max subscription is used. It runs in `AppPaths.DataDir`, restricts tools to web search (or none), and deliberately excludes the user's hooks/plugins/MCP.
  - `AnthropicApiProvider` (`ANTHROPIC_API_KEY`), `OpenAiCompatibleProvider` (Ollama/LM Studio, e.g. Qwen).
  Everything AI-driven (task chat, auto research, link finder, book translation via `Kindle/Translation/LlmClients.cs` → `ProviderLlmClient`) goes through this interface, so features must work with a local model too.
- **Agent output contract** (`Services/PromptBuilder.cs`): the system prompt includes today's date, task, subtasks and history; replies must end with a "Prochaine action" line, and `- [ ] …` lines are parsed as importable subtasks. Changing the prompt means keeping the extraction in sync.
- **Persistence** (`Services/Storage.cs`): atomic JSON in `%AppData%\TaskAgent\` (`tasks.json`, `settings.json`), shared by all versions — schema changes must stay backward-compatible with existing data. Secrets (GitHub token) are DPAPI-encrypted via `Services/Secrets.cs` and never sent to the LLM.
- **Reminders** (`Services/ReminderService.cs`): only real progress (button "J'ai avancé", ticking a subtask, completing/reopening) resets the "sans avancée" counter — chatting or declaring a blocker deliberately does not.
- **Kindle/** is the former standalone `pdf2kindle` tool merged into the app: `PdfExtractor` (PdfPig) → `PdfOcr` (PDFium + Tesseract, Windows only) → `BookBuilder` → `EpubWriter` / `MarkdownWriter`; `ChapterOrder` sorts a folder of PDFs into book order; `KindleConverter` is the UI-free entry point; `BookTranslator` does glossary pass + batched translation with a resumable cache.
- **`PdfToKindle/` is legacy and excluded from compilation** (`<Compile Remove="PdfToKindle\**" />`); don't edit it — the live code is in `Kindle/`. `releases\` is likewise excluded.
- `Services/SitePrinter.cs` drives a headless Edge/Chrome via Microsoft.Playwright; login sessions persist in `%LocalAppData%\TaskAgent\site-printer-profile`.
- `LaunchAtStartup` writes `HKCU\...\Run` but is skipped when running from `bin\` (`AppVersion.IsDev`).

## Claude Code harness in this repo

`.claude/hooks/passation-*.ps1` (SessionStart / Stop / SessionEnd, wired in `.claude/settings.json`) delegate to `eval/passation.py`, which writes session handoff notes under `.claude/apprentissage/passations/`. The Stop hook may exit 2 asking for a narrative handoff — write it when asked. `eval/` (Python, stdlib only) and `.claude/skills/capitaliser` form a weekly learning-review loop; the files listed in `.harnais` are pinned by hash — don't modify them.

### Exam of the per-task chat agent (skill `evaluer`)

The candidate is the real `PromptBuilder.System` + an `ILlmProvider`; nothing is copied.

```bash
dotnet run --no-build -- --eval eval/cas/cas.jsonl --sortie eval/runs/<run>.jsonl [--fournisseur OpenAICompatible] [--modele X] [--cas 04,12] [--parallele 3]
PYTHONIOENCODING=utf-8 python eval/juger.py eval/runs/<run>.jsonl     # 0 no regression · 1 regression · 2 failure
```

- `Services/EvalRunner.cs` (entered from `Program.Main` on `--eval`, no window): builds fictional tasks from `eval/cas/cas.jsonl` (dates relative to today, since the prompt contains today's date), forces a fixed fictional `AboutMe` and `AllowWebResearch=false`, and records `ExtractNextAction` / `ExtractSteps` results so the judge grades exactly what the UI would see.
- `eval/juger.py`: mechanical criteria only (Prochaine action present, French, ≤ 400 words, plus per-case `criteres`); one reference per provider in `eval/reference.json`, rewritten only with `--ecrire-reference` on explicit human request.
- Vocabulary used with the user: candidat, cas, jeu de cas, critère, note, référence, rejeu, régression, cas piège, juge.
- `eval/cas/**` and `eval/reference.json` are deny-listed for Edit/Write: the exam never changes to make the agent pass. Each case already carries a `rubrique` for the future AI judge (step 5, not built yet: input = case + output + rubric, never an expected answer; output `{"pass","reason"}`; contract from `D:\HorusCoachIA\harnais\eval\juge_local.py`; the judge must not be the candidate).

## Git / GitLab

Personal material must never be committed: books and conversions (`*.pdf`, `*.epub`, glossaries, translation caches, `PdfToKindle/*/`), session notes and learning queue (`.claude/apprentissage/`), session measurements (`eval/apprentissage/*` except its 4 config files), exam outputs (`eval/runs/`) and any personal cases (`eval/perso/`). All are in `.gitignore`; keep it that way. Exam cases must stay fictional — never derive them from `%AppData%\TaskAgent\tasks.json`.
