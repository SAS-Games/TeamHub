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
    from worklog_analytics.sources.jira_downloader import download_jira_timesheet

    start_date = date.fromisoformat(args.start)
    end_date = date.fromisoformat(args.end)
    with contextlib.redirect_stdout(sys.stderr):
        worklog_file = download_jira_timesheet(DateRange(start_date, end_date))
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

    print(json.dumps({
        "effortSummary": [
            {"label": label, "hours": float(hours)}
            for label, hours in effort_summary.items()
        ],
        "studioBreakdown": [
            {"label": label, "hours": float(hours)}
            for label, hours in studio_breakdown.items()
        ],
    }))


if __name__ == "__main__":
    main()
