# Contract: Rulesets

Both files are bodies for `POST/PUT /repos/{owner}/{repo}/rulesets`. The owner is the bypass
actor through the repository admin role: `actor_type: RepositoryRole`, `actor_id: 5`.
`OrganizationAdmin` does not apply to personal repositories. The bypass mode is
`pull_request` for main (bypass only by explicitly merging a PR) and `always` for tags (the
owner creates release tags).

## `.github/rulesets/main.json`

```json
{
  "name": "main-protection",
  "target": "branch",
  "enforcement": "active",
  "conditions": { "ref_name": { "include": ["~DEFAULT_BRANCH"], "exclude": [] } },
  "bypass_actors": [ { "actor_type": "RepositoryRole", "actor_id": 5, "bypass_mode": "pull_request" } ],
  "rules": [
    { "type": "deletion" },
    { "type": "non_fast_forward" },
    { "type": "required_linear_history" },
    { "type": "pull_request", "parameters": {
        "required_approving_review_count": 0,
        "dismiss_stale_reviews_on_push": false,
        "require_code_owner_review": false,
        "require_last_push_approval": false,
        "required_review_thread_resolution": false,
        "allowed_merge_methods": ["squash"] } },
    { "type": "required_status_checks", "parameters": {
        "strict_required_status_checks_policy": false,
        "required_status_checks": [
          { "context": "build-test", "integration_id": 15368 },
          { "context": "ext4-check", "integration_id": 15368 } ] } }
  ]
}
```

(`15368` is the GitHub Actions app; it makes sure only Actions can satisfy the check.)

| Rule | Requirement |
|------|-------------|
| pull_request + required_status_checks | FR-003, FR-003a (no direct pushes; green CI) |
| deletion, non_fast_forward | FR-003a (no deleting main, no force push) |
| `required_approving_review_count: 0` | FR-003b |
| `allowed_merge_methods: ["squash"]`, required_linear_history | FR-003e |
| bypass RepositoryRole admin, mode `pull_request` | FR-003a (owner's deliberate emergency bypass: merge a PR despite red checks; direct pushes stay blocked for everyone) |

## `.github/rulesets/release-tags.json`

```json
{
  "name": "release-tags",
  "target": "tag",
  "enforcement": "active",
  "conditions": { "ref_name": { "include": ["refs/tags/v*"], "exclude": [] } },
  "bypass_actors": [ { "actor_type": "RepositoryRole", "actor_id": 5, "bypass_mode": "always" } ],
  "rules": [
    { "type": "creation" },
    { "type": "update" },
    { "type": "deletion" },
    { "type": "non_fast_forward" }
  ]
}
```

| Rule | Requirement |
|------|-------------|
| creation (bypass = owner) | FR-003c (only the owner creates `v*` tags) |
| update, non_fast_forward, deletion | FR-003c (tags cannot be moved or deleted) |

The owner creates tags through the bypass. GitHub records bypass use in the rule insights,
which is accepted for an owner-only release flow. The release workflow never creates or moves
tags, so its token needs no bypass.

## Immutable releases (same script)

`PUT /repos/{owner}/{repo}/immutable-releases`. After a draft is published, its tag and assets
can no longer be changed or deleted, the owner included. Drafts stay editable and deletable.
This covers the owner side of FR-003c, which the tag rule set alone cannot do because its
bypass is `always`.

## Repository settings (same script)

```json
{ "allow_squash_merge": true, "allow_merge_commit": false, "allow_rebase_merge": false,
  "delete_branch_on_merge": true, "squash_merge_commit_title": "PR_TITLE",
  "squash_merge_commit_message": "PR_BODY" }
```

Squash commits take the PR title and description, so the commit on main explains the change
(constitution: commit messages in English).
