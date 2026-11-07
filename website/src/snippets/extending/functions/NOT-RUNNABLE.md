# A descriptor that calls a host function, refused by every host that did not register it

`billing-desk.alvo.json` calls `issue-invoice` from an `afterCreate` hook. `issue-invoice` is the C# function
`IssueInvoice.cs` beside it, registered with `AddFunction<IssueInvoice>("issue-invoice")`; the standalone image,
`alvo validate` and the host-less validator the snippet tests run register no function, so they refuse the file as naming
an unregistered one.

The page *Functions in the descriptor* shows it, and `IssueInvoiceTests.cs` runs it through `AlvoTestHost` with the
function registered. `DocsSnippetTests.Every_snippet_marked_not_runnable_really_is_refused` asserts the validator refuses
it, so this marker cannot outlive its reason.
