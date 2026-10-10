# The landing page's dry-run typo, refused on purpose

`helpdesk.alvo.json` here is `../managed/helpdesk.alvo.json` with one deliberate typo: the before-hook calls
`trimm(new.title)` instead of `trim(new.title)`. The landing page's agent section sends it as the body of
`PUT /management/projects/helpdesk/descriptor?dryRun=true` (`website/src/exchanges/landing/agent-dry-run.exchange.json`)
to show the 422 an agent gets back: a pointer to the expression, the `descriptor` code and a fix suggestion.

`DocsSnippetTests.Every_snippet_marked_not_runnable_really_is_refused` asserts the validator refuses it, so this
marker cannot outlive its reason.
