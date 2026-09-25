---
name: address-pr-review
description: Address concrete pull request review comments after a review arrives.
---

# Address Pr Review

Read `github review-comments --pr NUMBER` and `git state`. Separate actionable defects from questions and already-resolved feedback. Inspect only cited code and direct dependents; apply warranted fixes and targeted tests. For a large sanitized comment set, JEV capability `pr-triage` with purpose `review-comment-categorization` may categorize but must not dismiss security or uncertain feedback. Report each comment's disposition with evidence. Posting replies requires the user's instruction.
