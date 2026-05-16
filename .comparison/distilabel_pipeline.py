"""
Distilabel comparison pipeline.

Mirrors DistSharp's ExplanationPromptBuilder + LlmStep:
- Reads the same JSONL baseline DistSharp produced (10 method rows)
- Builds the same prompt text (system + user) per row
- Calls the same OpenAI model (gpt-4.1-mini)
- Writes a comparable JSONL output

Usage:
    .venv/Scripts/python distilabel_pipeline.py <baseline.jsonl> <output.jsonl>
"""

import json
import os
import sys
import time
from typing import Any, Dict

from distilabel.llms import OpenAILLM
from distilabel.pipeline import Pipeline
from distilabel.steps import LoadDataFromDicts, StepInput, step
from distilabel.steps.tasks import TextGeneration


SYSTEM_PROMPT = (
    "You are a senior .NET engineer explaining code to a capable colleague. "
    "Be precise, reference type names, and mention important edge cases."
)

USER_PROMPT_TEMPLATE = (
    "Explain what the {kind} `{name}` does. Provide your answer in 2–4 "
    "sentences focused on intent and behaviour, not line-by-line.\n\n"
    "Code:\n```csharp\n{body}\n```"
)


def load_baseline(path: str) -> list[dict[str, Any]]:
    """Read DistSharp's baseline JSONL and project to distilabel-friendly dicts."""
    rows: list[dict[str, Any]] = []
    with open(path, "r", encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            obj = json.loads(line)
            symbol = obj.get("symbol", {})
            # DistSharp emits camelCase JSON keys (System.Text.Json Web defaults).
            kind = symbol.get("kind") or obj.get("kind", "method")
            name = symbol.get("fullyQualifiedName") or obj.get("name", "unknown")
            body = symbol.get("bodyText", "")
            instruction = USER_PROMPT_TEMPLATE.format(kind=kind, name=name, body=body)
            rows.append(
                {
                    "instruction": instruction,
                    "system_prompt": SYSTEM_PROMPT,
                    "name": name,
                    "kind": kind,
                    "complexity": symbol.get("complexity") or obj.get("complexity", 0),
                    "namespace": symbol.get("namespace") or obj.get("namespace", ""),
                }
            )
    return rows


def main(baseline_path: str, output_path: str) -> None:
    rows = load_baseline(baseline_path)
    print(f"loaded {len(rows)} rows from {baseline_path}", flush=True)

    with Pipeline(name="distsharp-comparison") as pipeline:
        loader = LoadDataFromDicts(data=rows)
        generator = TextGeneration(
            llm=OpenAILLM(
                model="gpt-4.1-mini",
                api_key=os.environ["OPENAI_API_KEY"],
                generation_kwargs={"temperature": 0.7, "max_new_tokens": 1024},
            ),
            input_batch_size=4,
            num_generations=1,
        )
        loader >> generator

    started = time.monotonic()
    distiset = pipeline.run(use_cache=False)
    elapsed = time.monotonic() - started

    # distilabel returns a Distiset; the default split is "default"
    ds = distiset["default"]["train"]

    with open(output_path, "w", encoding="utf-8") as out:
        for row in ds:
            generations = row.get("generations") or []
            # In some distilabel versions the single generation lives under "generation"
            response = generations[0] if generations else (row.get("generation") or "")
            out.write(
                json.dumps(
                    {
                        "name": row["name"],
                        "kind": row["kind"],
                        "complexity": row["complexity"],
                        "namespace": row["namespace"],
                        "instruction": row["instruction"],
                        "response": response,
                    },
                    ensure_ascii=False,
                )
                + "\n"
            )

    print(f"wrote {len(ds)} rows to {output_path}", flush=True)
    print(f"elapsed: {elapsed:.2f}s ({elapsed / max(1, len(ds)):.2f}s/row)", flush=True)


if __name__ == "__main__":
    if len(sys.argv) != 3:
        print("Usage: python distilabel_pipeline.py <baseline.jsonl> <output.jsonl>", file=sys.stderr)
        sys.exit(2)
    main(sys.argv[1], sys.argv[2])
