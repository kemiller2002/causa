# Deployment

Conditor scaffolded a GitHub Pages deployment, `.github/workflows/deploy-pages.yml`.
It stays inert until a deployment target is chosen.

## Status

The target is not decided. The Indy-init planning set prefers GitHub Pages
"where practical" (D-203) and leaves the decision open (RQR-005). A static
Pages site cannot hold secrets: an AI provider key or an OAuth token exchange
needs a backend, which this workflow does not provide.

## Contract

- `npm run build` writes the complete deployable site to `dist/`, including
  `dist/index.html`. The scaffold defines no `build` script; add one with the
  first bundling decision.
- Every push to `main` deploys `dist/` once the workflow is enabled.
- Before publishing, the workflow scans `dist/` for GitHub token literals and
  fails on any, except the one test deploy key below, and that only in
  `dist/deploy-connection.json`.

## Test data connection (test deployments only)

Indy-init owner decision ODR-COMP-P13 (ODR-R10-36 for Rehearsal 010) allows one
exception to "credentials are never bundled", for a test deployment that holds
no secret data. The deploy build receives four optional repository secrets:

| Secret | Meaning |
|---|---|
| `REPO_OWNER` | owner of the designated test data repository |
| `DATA_SOURCE` | that repository's name |
| `CAUSA_BRANCH` | the branch the app reads and writes |
| `DATA_ACCESS_KEY_CAUSA` | a fine-grained key: contents read and write on that one repository only, short expiry, revocable |

With `CAUSA_DEPLOY_CONNECTION=1` (set only in this workflow's build step) the
application's `npm run build` writes `dist/deploy-connection.json`:

```
{"schema": "causa.deploy-connection/1",
 "connection": {"owner": "...", "repository": "...", "branch": "...", "key": "..."}}
```

and the app connects with it at load, without the user typing the key. With no
secret set the file holds only the schema and the app shows its Connect form.
The key is never committed, logged, put in a URL, error text, localStorage,
events, reports or Praxis state. **The site is public: anyone can read the key
from it.** Revoke it when the test ends. Without the owner decision, leave the
four secrets unset.

## Enable GitHub Pages

1. Settings, Pages: set the source to "GitHub Actions"
   (`gh api -X POST repos/OWNER/REPO/pages -f build_type=workflow`).
2. Set the repository variable: `gh variable set DEPLOY_TARGET --body github-pages`.
3. Push to `main`, or run the workflow from the Actions tab.

To choose another target, replace the workflow and update this file in the
same change.
