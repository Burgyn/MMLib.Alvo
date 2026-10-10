# A descriptor that calls a host function, refused by every host that did not register it

`vehicles.alvo.json` here is `examples/vehicle-registry/vehicles.alvo.json` with one addition: a `beforeCreate` hook on
`vehicles` whose `mutate` calls `normalizeVin(new.vin)`. `normalizeVin` is a CEL function the embedded sample
(`samples/MMLib.Alvo.Samples.EmbeddedHost`) registers with `AddCelFunction`; the standalone image, `alvo validate` and
the validator the snippet tests run know only the built-in functions, so they refuse the file as calling an unknown
function.

The guide *Custom CEL functions* shows it and runs the sample over it (`website/src/snippets/shell/custom-cel-functions-run.sh`).
`DocsSnippetTests.Every_snippet_marked_not_runnable_really_is_refused` asserts the validator refuses it, so this
marker cannot outlive its reason.
