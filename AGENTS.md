# Repository instructions

- Work directly on the user's main branch. Do not create branches unless explicitly requested.

- Read README.md and relevant architecture/API documents before changing behavior.
- Source belongs in src/, tests in tests/, architecture and runbooks in docs/, automation in scripts/.
- Never commit credentials, user documents, SQLite state, SDK binaries or build outputs.
- Contracts must stay independent of host, persistence and document implementation packages.
- The executor is loopback-only. Worker credentials must never authorize approvals.
- Every tool request needs an exact-request approval. New tools require validation, path policy and tests.
- Do not enable arbitrary shell execution, Word macros or automatic write replay during recovery.
- Keep centralized package versions, package lock files and global.json aligned.
- Run scripts/ci/Verify.ps1 before reporting an implementation complete.
- Scope PRs around a user-visible behavior. Add an ADR before changing authentication, transport or storage.
- Prefer small cohesive features over speculative projects or empty abstractions.
