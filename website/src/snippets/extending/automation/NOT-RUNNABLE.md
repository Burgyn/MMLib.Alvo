# A descriptor with an `entity.update` action, refused by a host that keeps no command ledger

`sales-desk.alvo.json`'s `big-order` rule ends with an `entity.update`, which is written exactly once per event through
the command ledger a relational provider keeps. The host-less validator the snippet tests run has no provider, so it
refuses the action; the standalone image and `AlvoTestHost` keep a ledger and apply it.

The page *Automation* shows it, and `AutomationTests.cs` runs it through `AlvoTestHost`.
`DocsSnippetTests.Every_snippet_marked_not_runnable_really_is_refused` asserts the validator refuses it, so this marker
cannot outlive its reason.
