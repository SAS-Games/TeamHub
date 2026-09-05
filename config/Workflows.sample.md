# Sample Workflows.xlsx Content

Create an Excel workbook with two structured tables named exactly:

- Workflows
- WorkflowSteps

## Workflows Table

| WorkflowKey | WorkflowName | Description | Enabled |
|---|---|---|---|
| NEW_HIRE_ONBOARDING | New Hire Onboarding | Candidate onboarding process | TRUE |
| MILESTONE_DELIVERY | Milestone Delivery Tracking | Milestone execution process | TRUE |

## WorkflowSteps Table

| WorkflowKey | StepKey | StepName | Description | OwnerType | Owner | ExpectedDurationHours | DependsOn | ReminderAfterHours | ReminderRepeatHours | EscalationAfterHours | EscalationOwner | Required | Enabled | SortOrder |
|---|---|---|---|---|---|---:|---|---:|---:|---:|---|---|---|---:|
| NEW_HIRE_ONBOARDING | EMPLOYEE_ID | Create Employee ID |  | Email | hr@company.com | 24 | - | 24 | 24 | 48 | manager@company.com | TRUE | TRUE | 1 |
| NEW_HIRE_ONBOARDING | LAPTOP | Laptop Setup |  | Email | it@company.com | 48 | EMPLOYEE_ID | 24 | 24 | 48 | it.manager@company.com | TRUE | TRUE | 2 |
| NEW_HIRE_ONBOARDING | EMAIL | Email Account |  | Email | it@company.com | 24 | EMPLOYEE_ID | 24 | 24 | 48 | it.manager@company.com | TRUE | TRUE | 3 |
| NEW_HIRE_ONBOARDING | ACCESS | Project Access |  | Email | mgr@company.com | 24 | LAPTOP,EMAIL | 24 | 24 | 48 | director@company.com | TRUE | TRUE | 4 |
| MILESTONE_DELIVERY | PLANNING | Planning |  | Email | pm@company.com | 24 | - | 24 | 24 | 48 | pm.lead@company.com | TRUE | TRUE | 1 |
| MILESTONE_DELIVERY | DEVELOPMENT | Development |  | Email | dev@company.com | 72 | PLANNING | 24 | 24 | 72 | eng.manager@company.com | TRUE | TRUE | 2 |
| MILESTONE_DELIVERY | INTERNAL_REVIEW | Internal Review |  | Email | review@company.com | 24 | DEVELOPMENT | 24 | 24 | 48 | qa.manager@company.com | TRUE | TRUE | 3 |
| MILESTONE_DELIVERY | QA_VALIDATION | QA Validation |  | Email | qa@company.com | 24 | INTERNAL_REVIEW | 24 | 24 | 48 | qa.manager@company.com | TRUE | TRUE | 4 |
| MILESTONE_DELIVERY | DELIVERY | Delivery |  | Email | delivery@company.com | 12 | QA_VALIDATION | 12 | 12 | 24 | ops@company.com | TRUE | TRUE | 5 |
