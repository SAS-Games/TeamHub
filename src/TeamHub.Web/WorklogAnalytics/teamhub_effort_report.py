import argparse
import contextlib
import json
import os
import sys
from datetime import date
from pathlib import Path


def parse_args():
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", required=True)
    parser.add_argument("--start", required=True)
    parser.add_argument("--end", required=True)
    return parser.parse_args()


def apply_teamhub_jira_pat(jira_downloader):
    pat = os.environ.pop("TEAMHUB_JIRA_PAT", None)
    original_load_json = getattr(jira_downloader, "load_json", None)
    if not pat or original_load_json is None:
        return

    def load_json_with_teamhub_pat(path, *args, **kwargs):
        config = original_load_json(path, *args, **kwargs)
        if Path(path).name.casefold() == "jira_timesheet_api_config.json":
            config = dict(config)
            config["api_token"] = pat
        return config

    jira_downloader.load_json = load_json_with_teamhub_pat


def main():
    args = parse_args()
    root = Path(args.root).resolve()
    sys.path.insert(0, str(root / "src"))
    os.chdir(root)

    from custom.hpgds.reports.executive_summary import build_executive_summary
    from custom.hpgds.reports.studio_support_breakdown import (
        build_studio_support_breakdown,
    )
    from worklog_analytics.app import build_context
    from worklog_analytics.loaders.config_loader import load_json
    from worklog_analytics.models.date_range import DateRange
    from worklog_analytics.reports.matrix_builder import build_matrix
    from worklog_analytics.sources import jira_downloader

    apply_teamhub_jira_pat(jira_downloader)

    start_date = date.fromisoformat(args.start)
    end_date = date.fromisoformat(args.end)
    with contextlib.redirect_stdout(sys.stderr):
        worklog_file = jira_downloader.download_jira_timesheet(DateRange(start_date, end_date))
        context = build_context(worklog_file)
        worklogs = [
            worklog
            for worklog in context.worklogs
            if start_date
            <= (
                worklog.work_date.date()
                if hasattr(worklog.work_date, "date")
                else worklog.work_date
            )
            <= end_date
        ]
        studio_groups = load_json(
            Path("src/custom/hpgds/configs/studio_groups.json"),
            True,
        )
        effort_summary = build_executive_summary(worklogs, studio_groups)
        studio_breakdown = build_studio_support_breakdown(worklogs, studio_groups)
        employee_activity_matrix, _ = build_matrix(
            worklogs,
            "employee",
            "activity_group",
        )

    print(json.dumps({
        "effortSummary": [
            {"label": label, "hours": float(hours)}
            for label, hours in effort_summary.items()
        ],
        "studioBreakdown": [
            {"label": label, "hours": float(hours)}
            for label, hours in studio_breakdown.items()
        ],
        "employeeEffortBreakdowns": [
            {
                "employee": employee,
                "slices": [
                    {"label": label, "hours": float(hours)}
                    for label, hours in activities.items()
                ],
            }
            for employee, activities in sorted(
                employee_activity_matrix.items(),
                key=lambda item: item[0].casefold(),
            )
        ],
    }))


if __name__ == "__main__":
    main()
