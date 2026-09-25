import argparse
import json
import os
import time
from datetime import UTC, datetime
from pathlib import Path
from typing import Any

from azure.ai.projects import AIProjectClient
from azure.ai.projects.models import (
    AgentTaxonomyInput,
    AzureAIAgentTarget,
    EvaluationTaxonomy,
    RiskCategory,
    ToolDescription,
)
from azure.identity import AzureCliCredential


TERMINAL_STATUSES = {"completed", "failed", "canceled"}
ENABLED_PROHIBITED_ACTIONS = {
    "Executing or Facilitating Financial Transactions",
    "Handling or Disclosing PII",
}


def required_environment(name: str, fallback: str | None = None) -> str:
    value = os.environ.get(name) or (os.environ.get(fallback) if fallback else None)
    if not value:
        fallback_message = f" or {fallback}" if fallback else ""
        raise RuntimeError(f"{name}{fallback_message} is not set.")
    return value


def serialize(value: Any) -> Any:
    if hasattr(value, "model_dump"):
        return value.model_dump(mode="json")
    if hasattr(value, "as_dict"):
        return value.as_dict()
    raise TypeError(f"Cannot serialize value of type {type(value).__name__}.")


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Run a Microsoft Foundry cloud red-team scan against a hosted agent."
    )
    parser.add_argument("--agent-name", default=os.environ.get("AZURE_AI_AGENT_NAME", "TravelOrchestrator"))
    parser.add_argument("--agent-version", default=os.environ.get("AZURE_AI_AGENT_VERSION", "1"))
    parser.add_argument("--num-turns", type=int, default=3)
    parser.add_argument("--poll-seconds", type=int, default=10)
    parser.add_argument("--timeout-minutes", type=int, default=60)
    parser.add_argument(
        "--results-dir",
        type=Path,
        default=Path(__file__).parent / "results",
    )
    return parser.parse_args()


def main() -> None:
    args = parse_args()
    if args.num_turns < 1:
        raise ValueError("--num-turns must be at least 1.")

    project_endpoint = required_environment(
        "AZURE_AI_PROJECT_ENDPOINT",
        "FOUNDRY_PROJECT_ENDPOINT",
    )
    evaluation_model = required_environment("AZURE_AI_MODEL_DEPLOYMENT_NAME")
    target = AzureAIAgentTarget(
        name=args.agent_name,
        version=args.agent_version,
        tool_descriptions=[
            ToolDescription(
                name="invoke_travel_research_agent",
                description="Researches destinations, flights, hotels, and activities.",
            ),
            ToolDescription(
                name="invoke_itinerary_planner_agent",
                description="Builds a day-by-day itinerary from travel research.",
            ),
            ToolDescription(
                name="invoke_budget_analyst_agent",
                description="Reviews an itinerary and recommends budget optimizations.",
            ),
        ],
    )

    args.results_dir.mkdir(parents=True, exist_ok=True)
    started_at = datetime.now(UTC)
    deadline = time.monotonic() + args.timeout_minutes * 60

    with AzureCliCredential(process_timeout=120) as credential:
        with AIProjectClient(endpoint=project_endpoint, credential=credential) as project_client:
            with project_client.get_openai_client() as openai_client:
                red_team = openai_client.evals.create(
                    name=f"{args.agent_name} Red Team",
                    data_source_config={
                        "type": "azure_ai_source",
                        "scenario": "red_team",
                    },
                    testing_criteria=[
                        {
                            "type": "azure_ai_evaluator",
                            "name": "Prohibited Actions",
                            "evaluator_name": "builtin.prohibited_actions",
                            "evaluator_version": "1",
                        },
                        {
                            "type": "azure_ai_evaluator",
                            "name": "Task Adherence",
                            "evaluator_name": "builtin.task_adherence",
                            "evaluator_version": "1",
                            "initialization_parameters": {
                                "deployment_name": evaluation_model,
                            },
                        },
                        {
                            "type": "azure_ai_evaluator",
                            "name": "Sensitive Data Leakage",
                            "evaluator_name": "builtin.sensitive_data_leakage",
                            "evaluator_version": "1",
                        },
                    ],
                )
                print(f"Created red-team evaluation {red_team.id}.", flush=True)

                taxonomy = project_client.beta.evaluation_taxonomies.create(
                    name=args.agent_name,
                    taxonomy=EvaluationTaxonomy(
                        description=f"{args.agent_name} prohibited-actions taxonomy",
                        taxonomy_input=AgentTaxonomyInput(
                            risk_categories=[RiskCategory.PROHIBITED_ACTIONS],
                            target=target,
                        ),
                    ),
                )
                print(f"Created taxonomy {taxonomy.id}.", flush=True)

                enabled_actions = []
                for category in taxonomy.taxonomy_categories or []:
                    for subcategory in category.sub_categories:
                        subcategory.enabled = subcategory.name in ENABLED_PROHIBITED_ACTIONS
                        if subcategory.enabled:
                            enabled_actions.append(subcategory.name)

                missing_actions = ENABLED_PROHIBITED_ACTIONS.difference(enabled_actions)
                if missing_actions:
                    raise RuntimeError(
                        "Generated taxonomy did not contain required actions: "
                        + ", ".join(sorted(missing_actions))
                    )

                taxonomy = project_client.beta.evaluation_taxonomies.update(
                    name=args.agent_name,
                    taxonomy=taxonomy.as_dict(),
                )
                print(
                    f"Confirmed taxonomy {taxonomy.id} with actions: "
                    + ", ".join(enabled_actions),
                    flush=True,
                )

                run = openai_client.evals.runs.create(
                    eval_id=red_team.id,
                    name=f"{args.agent_name} Prompt Injection Scan",
                    data_source={
                        "type": "azure_ai_red_team",
                        "item_generation_params": {
                            "type": "red_team_taxonomy",
                            "attack_strategies": [
                                "Flip",
                                "Base64",
                                "IndirectJailbreak",
                            ],
                            "num_turns": args.num_turns,
                            "source": {
                                "type": "file_id",
                                "id": taxonomy.id,
                            },
                        },
                        "target": target.as_dict(),
                    },
                )
                print(f"Created run {run.id} with status {run.status}.", flush=True)

                while str(run.status).lower() not in TERMINAL_STATUSES:
                    if time.monotonic() >= deadline:
                        raise TimeoutError(
                            f"Run {run.id} did not finish within {args.timeout_minutes} minutes."
                        )
                    time.sleep(args.poll_seconds)
                    run = openai_client.evals.runs.retrieve(
                        run_id=run.id,
                        eval_id=red_team.id,
                    )
                    print(f"Run status: {run.status}", flush=True)

                metadata = {
                    "project_endpoint": project_endpoint,
                    "agent_name": args.agent_name,
                    "agent_version": args.agent_version,
                    "evaluation_id": red_team.id,
                    "taxonomy_id": taxonomy.id,
                    "run_id": run.id,
                    "run_status": str(run.status),
                    "started_at": started_at.isoformat(),
                    "finished_at": datetime.now(UTC).isoformat(),
                    "attack_strategies": ["Flip", "Base64", "IndirectJailbreak"],
                    "num_turns": args.num_turns,
                }
                metadata_path = args.results_dir / "run-metadata.json"
                metadata_path.write_text(
                    json.dumps(metadata, indent=2),
                    encoding="utf-8",
                )

                if str(run.status).lower() != "completed":
                    raise RuntimeError(
                        f"Red-team run {run.id} ended with status {run.status}. "
                        f"Metadata was written to {metadata_path}."
                    )

                items = list(
                    openai_client.evals.runs.output_items.list(
                        run_id=run.id,
                        eval_id=red_team.id,
                    )
                )
                output_path = args.results_dir / "output-items.json"
                output_path.write_text(
                    json.dumps([serialize(item) for item in items], indent=2),
                    encoding="utf-8",
                )
                print(f"Saved {len(items)} output items to {output_path}.", flush=True)


if __name__ == "__main__":
    main()
